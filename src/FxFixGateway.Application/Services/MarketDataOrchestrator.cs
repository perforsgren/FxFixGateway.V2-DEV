using FxFixGateway.Domain.Interfaces;
using FxFixGateway.Domain.ValueObjects;
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
        private readonly IMarketDataService? _marketData;
        private readonly ILogger<MarketDataOrchestrator> _logger;

        private static readonly HashSet<string> MarketDataSessions = new(StringComparer.OrdinalIgnoreCase)
        {
            "VOLB_FIXHUB_PROD"
        };

        // The instruments requested (35=V) since each session's logon: one listed again (a second security list, or twice in
        // one) is not requested or marked again.
        private readonly Dictionary<string, HashSet<string>> _requested = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _requestedLock = new();

        public MarketDataOrchestrator(
            IMarketDataSubscriber subscriber,
            IMarketInstrumentRepository instrumentRepo,
            IMarketSubscriptionRepository subscriptionRepo,
            ILogger<MarketDataOrchestrator> logger,
            IMarketDataService? marketData = null)
        {
            _marketData = marketData;
            _subscriber = subscriber ?? throw new ArgumentNullException(nameof(subscriber));
            _instrumentRepo = instrumentRepo ?? throw new ArgumentNullException(nameof(instrumentRepo));
            _subscriptionRepo = subscriptionRepo ?? throw new ArgumentNullException(nameof(subscriptionRepo));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task OnSessionLoggedOnAsync(string sessionKey)
        {
            if (sessionKey == "FXOHUB_UAT")
            {
                IReadOnlyList<MarketSubscriptionFilter> subs;
                try
                {
                    subs = await _subscriptionRepo.GetEnabledSubscriptionsAsync(sessionKey);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[{Session}] fix_config_prod.market_subscriptions could not be read — no MarketDataRequest sent", sessionKey);
                    return;
                }
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

            lock (_requestedLock) _requested[sessionKey] = new HashSet<string>(StringComparer.Ordinal);
            _logger.LogInformation("[{Session}] Logon detected — sending SecurityListRequest (35=x)", sessionKey);
            _subscriber.SendSecurityListRequest(sessionKey);
        }

        public async Task OnSecurityListReadyAsync(string sessionKey, IReadOnlyList<string> listedIds)
        {
            if (listedIds.Count == 0)
            {
                _logger.LogWarning("[{Session}] SecurityList ready but no matching instruments — skipping MarketDataRequest", sessionKey);
                return;
            }

            List<string> securityIds;
            lock (_requestedLock)
            {
                if (!_requested.TryGetValue(sessionKey, out var requested))
                    _requested[sessionKey] = requested = new HashSet<string>(StringComparer.Ordinal);
                securityIds = listedIds.Where(requested.Add).ToList();
            }
            if (securityIds.Count == 0)
            {
                _logger.LogInformation(
                    "[{Session}] SecurityList ready — all {Count} instrument(s) already requested since logon — no MarketDataRequest",
                    sessionKey, listedIds.Count);
                return;
            }

            _logger.LogInformation(
                "[{Session}] SecurityList ready — {Count} instrument(s) — sending MarketDataRequest (35=V){Skipped}",
                sessionKey, securityIds.Count,
                securityIds.Count < listedIds.Count ? $"; {listedIds.Count - securityIds.Count} already requested since logon" : "");

            try
            {
                var marked = await _instrumentRepo.MarkAsSubscribedAsync(sessionKey, securityIds);
                if (marked < securityIds.Count)
                    _logger.LogWarning(
                        "[{Session}] {Marked} of {Count} instrument(s) marked subscribed in the database: the others are not in market_instruments",
                        sessionKey, marked, securityIds.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "[{Session}] {Count} instrument(s) not marked subscribed in the database — their snapshots are kept for this session; marked again at the next logon",
                    sessionKey, securityIds.Count);
            }
            // Their snapshots are kept from now on whatever the database says (the market data service caches its answer).
            foreach (var id in securityIds) _marketData?.MarkSubscribed(sessionKey, id);
            _subscriber.SendMarketDataRequest(sessionKey, securityIds);
        }
    }
}