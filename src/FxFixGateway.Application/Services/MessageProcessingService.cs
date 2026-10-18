using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using FxFixGateway.Domain.Events;
using FxFixGateway.Domain.Interfaces;
using Microsoft.Extensions.Logging;

namespace FxFixGateway.Application.Services
{
    /// <summary>
    /// Writes every FIX message (in and out, every session) to the MessageIn log table, off QuickFIX's session thread: the
    /// event handlers only queue the message (bounded; when full the oldest are dropped and counted), and one background
    /// task writes them, with the time the message was received or sent. Before, the write ran on the session thread, and
    /// MySql.Data waits for a free pooled connection by blocking its thread: with the pool full, a write held the session
    /// for the 30 s connection timeout per message (16 Oct: one Volbroker message handled a minute, requests up to 9 min
    /// late). Once a minute, on its own timer (so a write that hangs is reported too), a warning is logged when writes
    /// failed, were slow or were dropped, when a write has run a minute or more, or when the oldest queued message has
    /// waited a minute or more.
    /// </summary>
    public sealed class MessageProcessingService : IDisposable
    {
        private const int Capacity = 20_000;
        private static readonly TimeSpan SlowWrite = TimeSpan.FromSeconds(2);
        private static readonly TimeSpan ReportEvery = TimeSpan.FromMinutes(1);
        private static readonly TimeSpan Behind = TimeSpan.FromMinutes(1);

        private readonly IFixEngine _fixEngine;
        private readonly IMessageLogger _messageLogger;
        private readonly ILogger<MessageProcessingService> _logger;
        private readonly Channel<Entry> _queue;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _writer;
        private readonly Task _reporter;

        // Since the last report: _dropped by the channel, the rest under _lock (the writer sets them, the reporter reads them).
        private long _dropped;
        private readonly object _lock = new();
        private int _written, _failed, _slow;
        private TimeSpan _slowest;
        private Exception? _lastError;
        private DateTime? _writeStartedUtc;

        private sealed record Entry(bool Incoming, string SessionKey, string MsgType, string RawMessage, DateTime AtUtc);

        public MessageProcessingService(
            IFixEngine fixEngine,
            IMessageLogger messageLogger,
            ILogger<MessageProcessingService> logger)
        {
            _fixEngine = fixEngine ?? throw new ArgumentNullException(nameof(fixEngine));
            _messageLogger = messageLogger ?? throw new ArgumentNullException(nameof(messageLogger));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            _queue = Channel.CreateBounded<Entry>(
                new BoundedChannelOptions(Capacity) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = false },
                _ => Interlocked.Increment(ref _dropped));
            _writer = Task.Run(() => WriteAsync(_cts.Token));
            _reporter = Task.Run(() => ReportAsync(_cts.Token));

            // Auto-register event handlers
            _fixEngine.MessageReceived += OnMessageReceived;
            _fixEngine.MessageSent += OnMessageSent;

            _logger.LogInformation("MessageProcessingService initialized and listening for FIX events");
        }

        // On QuickFIX's session thread: only queue (never waits).
        private void OnMessageReceived(object? sender, MessageReceivedEvent e)
            => _queue.Writer.TryWrite(new Entry(true, e.SessionKey, e.MsgType, e.RawMessage, e.ReceivedAtUtc));

        private void OnMessageSent(object? sender, MessageSentEvent e)
            => _queue.Writer.TryWrite(new Entry(false, e.SessionKey, e.MsgType, e.RawMessage, e.SentAtUtc));

        private async Task WriteAsync(CancellationToken ct)
        {
            try
            {
                await foreach (var e in _queue.Reader.ReadAllAsync(ct))
                {
                    lock (_lock) _writeStartedUtc = DateTime.UtcNow;
                    var started = Stopwatch.StartNew();
                    Exception? error = null;
                    try
                    {
                        if (e.Incoming) await _messageLogger.LogIncomingAsync(e.SessionKey, e.MsgType, e.RawMessage, e.AtUtc);
                        else await _messageLogger.LogOutgoingAsync(e.SessionKey, e.MsgType, e.RawMessage, e.AtUtc);
                    }
                    catch (Exception ex) when (!ct.IsCancellationRequested)
                    {
                        error = ex;
                    }
                    var took = started.Elapsed;
                    lock (_lock)
                    {
                        _writeStartedUtc = null;
                        if (error != null)
                        {
                            _failed++;
                            _lastError = error;
                        }
                        else
                        {
                            _written++;
                            if (took >= SlowWrite) _slow++;
                        }
                        if (took > _slowest) _slowest = took;
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
            }
        }

        private async Task ReportAsync(CancellationToken ct)
        {
            using var timer = new PeriodicTimer(ReportEvery);
            try
            {
                while (await timer.WaitForNextTickAsync(ct))
                {
                    try { Report(); }
                    catch (Exception ex) { _logger.LogError(ex, "MessageIn log: report failed"); }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
            }
        }

        /// <summary>Once a minute: a warning when anything is wrong with the writes since the last report, or they are behind.</summary>
        private void Report()
        {
            var now = DateTime.UtcNow;
            int written, failed, slow;
            TimeSpan slowest, running;
            Exception? lastError;
            lock (_lock)
            {
                written = _written; failed = _failed; slow = _slow; slowest = _slowest; lastError = _lastError;
                running = _writeStartedUtc is { } startedUtc ? now - startedUtc : TimeSpan.Zero;
                _written = 0; _failed = 0; _slow = 0; _slowest = TimeSpan.Zero; _lastError = null;
            }
            var dropped = Interlocked.Exchange(ref _dropped, 0);
            var oldest = _queue.Reader.TryPeek(out var first) ? now - first.AtUtc : TimeSpan.Zero;

            if (failed == 0 && slow == 0 && dropped == 0 && running < Behind && oldest < Behind) return;
            _logger.LogWarning(lastError,
                "MessageIn log, last minute: {Written} written, {Failed} failed, {Slow} took {Threshold} s or more (slowest {Slowest:0.0} s), {Dropped} dropped with the queue full; now {Queued} queued, the oldest waiting {Oldest:0} s, the current write running {Running:0} s. The FIX sessions are not held up by it.",
                written, failed, slow, SlowWrite.TotalSeconds, slowest.TotalSeconds, dropped, _queue.Reader.Count, oldest.TotalSeconds, running.TotalSeconds);
        }

        public void Dispose()
        {
            _fixEngine.MessageReceived -= OnMessageReceived;
            _fixEngine.MessageSent -= OnMessageSent;
            _queue.Writer.TryComplete();
            _cts.Cancel();
            _cts.Dispose();
        }
    }
}
