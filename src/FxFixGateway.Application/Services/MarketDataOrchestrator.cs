using FxFixGateway.Domain.Interfaces;
using Microsoft.Extensions.Logging;

namespace FxFixGateway.Application.Services
{
    /// <summary>
    /// Orchestrerar market data-flödet för market data-sessioner (VOLB_FIXHUB_*, FXOHUB_UAT).
    ///
    /// Flöde Volbroker:
    ///   OnLogon → SendSecurityListRequest (35=x)
    ///   → SecurityListService tar emot 35=y, filtrerar, sparar instrument
    ///   → OnSecurityListReadyAsync → SendMarketDataRequest (35=V)
    ///
    /// Flöde TPICAP (FXOHUB_UAT):
    ///   OnLogon → läs enabled valutapar ur fix_config_prod.market_subscriptions
    ///   → SendMarketDataRequestProbe (35=V) direkt, ingen SecurityList-runda.
    /// </summary>
    public class MarketDataOrchestrator : IMarketDataOrchestrator
    {
        private readonly IMarketDataSubscriber _subscriber;
        private readonly IMarketInstrumentRepository _instrumentRepo;
        private readonly IMarketSubscriptionRepository _subscriptionRepo;
        private readonly ILogger<MarketDataOrchestrator> _logger;

        private static readonly HashSet<string> MarketDataSessions = new(StringComparer.OrdinalIgnoreCase)
        {
            "VOLB_FIXHUB_PROD"
        };

        public MarketDataOrchestrator(
            IMarketDataSubscriber subscriber,
            IMarketInstrumentRepository instrumentRepo,
            IMarketSubscriptionRepository subscriptionRepo,
            ILogger<MarketDataOrchestrator> logger)
        {
            _subscriber = subscriber ?? throw new ArgumentNullException(nameof(subscriber));
            _instrumentRepo = instrumentRepo ?? throw new ArgumentNullException(nameof(instrumentRepo));
            _subscriptionRepo = subscriptionRepo ?? throw new ArgumentNullException(nameof(subscriptionRepo));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task OnSessionLoggedOnAsync(string sessionKey)
        {
            if (sessionKey == "FXOHUB_UAT")
            {
                var subs = await _subscriptionRepo.GetEnabledSubscriptionsAsync(sessionKey);
                var pairs = subs
                    .Select(s => s.CurrencyPair)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (pairs.Count == 0)
                {
                    _logger.LogWarning(
                        "[{Session}] Inga aktiva valutapar i market_subscriptions — skickar ingen MarketDataRequest",
                        sessionKey);
                    return;
                }

                _logger.LogInformation(
                    "[{Session}] FXOHUB probe — sending MarketDataRequest (35=V) for {Count} pair(s): {Pairs}",
                    sessionKey, pairs.Count, string.Join(", ", pairs));
                _subscriber.SendMarketDataRequestProbe(sessionKey, pairs);
                return;
            }

            if (!MarketDataSessions.Contains(sessionKey))
                return;

            _logger.LogInformation("[{Session}] Logon detected — sending SecurityListRequest (35=x)", sessionKey);
            _subscriber.SendSecurityListRequest(sessionKey);
        }

        public async Task OnSecurityListReadyAsync(string sessionKey, IReadOnlyList<string> securityIds)
        {
            if (securityIds.Count == 0)
            {
                _logger.LogWarning("[{Session}] SecurityList ready but no matching instruments — skipping MarketDataRequest", sessionKey);
                return;
            }

            _logger.LogInformation(
                "[{Session}] SecurityList ready — {Count} instrument(s) — sending MarketDataRequest (35=V)",
                sessionKey, securityIds.Count);

            await _instrumentRepo.MarkAsSubscribedAsync(sessionKey, securityIds);
            _subscriber.SendMarketDataRequest(sessionKey, securityIds);
        }
    }
}