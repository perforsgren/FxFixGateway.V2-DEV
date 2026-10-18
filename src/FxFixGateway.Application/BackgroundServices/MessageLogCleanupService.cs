using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using FxFixGateway.Domain.Interfaces;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FxFixGateway.Application.BackgroundServices
{
    /// <summary>
    /// Deletes MessageIn log rows older than the retention (MessageLog:RetentionDays in appsettings.json) once a day, the first
    /// time a few minutes after start (after the startup burst of market data), in batches oldest first. On 18 Oct the table had
    /// 3.2 million rows (1.9 GB) from July. Only the gateway's message log window reads it (the latest 500 per session);
    /// trade_stp.MessageIn (STP) is another table and is not touched.
    /// </summary>
    public sealed class MessageLogCleanupService : BackgroundService
    {
        private const int BatchSize = 10_000;
        private static readonly TimeSpan FirstRunAfter = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan RunEvery = TimeSpan.FromDays(1);
        private static readonly TimeSpan PauseBetweenBatches = TimeSpan.FromMilliseconds(200);

        private readonly IMessageLogRetention _retention;
        private readonly int _retentionDays;
        private readonly ILogger<MessageLogCleanupService> _logger;

        public MessageLogCleanupService(IMessageLogRetention retention, int retentionDays, ILogger<MessageLogCleanupService> logger)
        {
            _retention = retention ?? throw new ArgumentNullException(nameof(retention));
            _retentionDays = retentionDays;
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            if (_retentionDays < 1)
            {
                _logger.LogWarning("MessageIn cleanup off: MessageLog:RetentionDays is {Days}, it must be 1 or more", _retentionDays);
                return;
            }
            _logger.LogInformation("MessageIn cleanup: rows older than {Days} days are deleted once a day, the first time in {Minutes:0} min",
                _retentionDays, FirstRunAfter.TotalMinutes);

            try
            {
                await Task.Delay(FirstRunAfter, stoppingToken);
                using var timer = new PeriodicTimer(RunEvery);
                do
                {
                    await RunOnceAsync(stoppingToken);
                }
                while (await timer.WaitForNextTickAsync(stoppingToken));
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
            }
        }

        /// <summary>Deletes every row received before now minus the retention; an error is logged and the next run tries again.</summary>
        public async Task RunOnceAsync(CancellationToken ct)
        {
            var cutoffUtc = DateTime.UtcNow.AddDays(-_retentionDays);
            var started = Stopwatch.StartNew();
            long deleted = 0;
            try
            {
                var keepFrom = await _retention.GetKeepFromIdAsync(cutoffUtc, ct);
                if (keepFrom != null)
                {
                    int batch;
                    do
                    {
                        batch = await _retention.DeleteBelowAsync(keepFrom.Value, BatchSize, ct);
                        deleted += batch;
                        if (batch == BatchSize)
                            await Task.Delay(PauseBetweenBatches, ct);
                    }
                    while (batch == BatchSize);
                }
                _logger.LogInformation("MessageIn cleanup: {Deleted} row(s) received before {Cutoff:yyyy-MM-dd HH:mm} UTC deleted in {Seconds:0} s",
                    deleted, cutoffUtc, started.Elapsed.TotalSeconds);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "MessageIn cleanup failed after {Deleted} row(s) deleted in {Seconds:0} s; the next run tries again",
                    deleted, started.Elapsed.TotalSeconds);
            }
        }
    }
}
