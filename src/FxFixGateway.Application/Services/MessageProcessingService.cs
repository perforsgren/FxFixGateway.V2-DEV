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
    /// MySql.Data's async calls block: a write hitting the 30 s timeout held the session 30 s per message (16 Oct: one
    /// Volbroker message handled a minute, requests up to 9 min late). Failed, slow or dropped writes are logged, at most
    /// once a minute.
    /// </summary>
    public sealed class MessageProcessingService : IDisposable
    {
        private const int Capacity = 20_000;
        private static readonly TimeSpan SlowWrite = TimeSpan.FromSeconds(2);
        private static readonly TimeSpan ReportEvery = TimeSpan.FromMinutes(1);

        private readonly IFixEngine _fixEngine;
        private readonly IMessageLogger _messageLogger;
        private readonly ILogger<MessageProcessingService> _logger;
        private readonly Channel<Entry> _queue;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _writer;

        // Since the last report (the writer's thread only, except _dropped).
        private long _dropped;
        private int _failed, _slow;
        private TimeSpan _slowest;
        private Exception? _lastError;
        private DateTime _lastReportUtc = DateTime.MinValue;

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
                    var started = Stopwatch.StartNew();
                    try
                    {
                        if (e.Incoming) await _messageLogger.LogIncomingAsync(e.SessionKey, e.MsgType, e.RawMessage, e.AtUtc);
                        else await _messageLogger.LogOutgoingAsync(e.SessionKey, e.MsgType, e.RawMessage, e.AtUtc);
                        if (started.Elapsed >= SlowWrite)
                        {
                            _slow++;
                            if (started.Elapsed > _slowest) _slowest = started.Elapsed;
                        }
                    }
                    catch (Exception ex) when (!ct.IsCancellationRequested)
                    {
                        _failed++;
                        _lastError = ex;
                        if (started.Elapsed > _slowest) _slowest = started.Elapsed;
                    }
                    Report();
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
            }
        }

        /// <summary>At most once a minute: the writes that failed, were slow or were dropped since the last report.</summary>
        private void Report()
        {
            var now = DateTime.UtcNow;
            if (now - _lastReportUtc < ReportEvery) return;
            var dropped = Interlocked.Read(ref _dropped);
            if (_failed == 0 && _slow == 0 && dropped == 0) return;
            Interlocked.Add(ref _dropped, -dropped);
            _lastReportUtc = now;
            _logger.LogWarning(_lastError,
                "MessageIn log: {Failed} write(s) failed, {Slow} took {Threshold} s or more (slowest {Slowest:0.0} s), {Dropped} dropped with the queue full; {Queued} queued. The FIX sessions are not held up by it.",
                _failed, _slow, SlowWrite.TotalSeconds, _slowest.TotalSeconds, dropped, _queue.Reader.Count);
            _failed = 0;
            _slow = 0;
            _slowest = TimeSpan.Zero;
            _lastError = null;
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
