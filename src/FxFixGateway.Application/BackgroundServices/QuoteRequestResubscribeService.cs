using FxFixGateway.Domain.Interfaces;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FxFixGateway.Application.BackgroundServices
{
    /// <summary>
    /// Every minute: the instruments of the last 24 hours' Volbroker requests (fxvol.quote_requests) that are not subscribed
    /// in the database are subscribed again (<see cref="IQuoteRequestService.ResubscribeAsync"/>): storing or marking the
    /// instrument failed when the request came, or the gateway restarted since. The requests are stored before their
    /// instrument, so they are the list to check against. A failed round is logged and the next one tries again.
    /// </summary>
    public sealed class QuoteRequestResubscribeService : BackgroundService
    {
        private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);
        private static readonly TimeSpan Window = TimeSpan.FromHours(24);

        private readonly IQuoteRequestService _service;
        private readonly ILogger<QuoteRequestResubscribeService> _logger;

        public QuoteRequestResubscribeService(IQuoteRequestService service, ILogger<QuoteRequestResubscribeService> logger)
        {
            _service = service ?? throw new ArgumentNullException(nameof(service));
            _logger  = logger  ?? throw new ArgumentNullException(nameof(logger));
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            using var timer = new PeriodicTimer(Interval);
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await _service.ResubscribeAsync(DateTime.UtcNow - Window);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Volbroker resubscribe check failed — tried again in a minute");
                }
            }
        }
    }
}
