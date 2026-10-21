using FxFixGateway.Domain.Entities;
using FxFixGateway.Domain.Interfaces;
using FxFixGateway.Domain.ValueObjects;
using Microsoft.Extensions.Logging;

namespace FxFixGateway.Application.Services
{
    public class QuoteRequestService : IQuoteRequestService
    {
        private readonly IQuoteRequestRepository      _quoteRequestRepo;
        private readonly IMarketInstrumentRepository  _instrumentRepo;
        private readonly IMarketSubscriptionRepository _subscriptionRepo;
        private readonly IMarketDataService?           _marketData;
        private readonly ILogger<QuoteRequestService>  _logger;

        private static readonly HashSet<string> MarketDataSessions = new(StringComparer.OrdinalIgnoreCase)
        {
            "VOLB_FIXHUB_PROD"
        };

        // Sessions that only take requests: a 35=R is stored as on a market data session, and its instrument is neither
        // upserted nor marked subscribed, so no market data is kept from them (Volbroker's SUP environment, a test session
        // run in the same gateway as production).
        private static readonly HashSet<string> RfqOnlySessions = new(StringComparer.OrdinalIgnoreCase)
        {
            "VOLB_FIXHUB_DEV"
        };

        // Cache av tillåtna valutapar per session — laddas en gång, rensas ej (statisk konfiguration)
        private readonly Dictionary<string, HashSet<string>> _allowedPairsCache = new();
        private readonly SemaphoreSlim _cacheLock = new(1, 1);

        public QuoteRequestService(
            IQuoteRequestRepository quoteRequestRepo,
            IMarketInstrumentRepository instrumentRepo,
            IMarketSubscriptionRepository subscriptionRepo,
            ILogger<QuoteRequestService> logger,
            IMarketDataService? marketData = null)
        {
            _marketData        = marketData;
            _quoteRequestRepo  = quoteRequestRepo  ?? throw new ArgumentNullException(nameof(quoteRequestRepo));
            _instrumentRepo    = instrumentRepo    ?? throw new ArgumentNullException(nameof(instrumentRepo));
            _subscriptionRepo  = subscriptionRepo  ?? throw new ArgumentNullException(nameof(subscriptionRepo));
            _logger            = logger            ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task HandleQuoteRequestAsync(string sessionKey, QuoteRequestDto dto)
        {
            _logger.LogInformation(
                "[{Session}] HandleQuoteRequestAsync anropad — SecurityId={SecId} Legs={LegCount}",
                sessionKey, dto.SecurityId, dto.Legs.Count);

            var rfqOnly = RfqOnlySessions.Contains(sessionKey);
            if (!rfqOnly && !MarketDataSessions.Contains(sessionKey))
            {
                _logger.LogWarning(
                    "[{Session}] 35=R ignoreras — session ej i MarketDataSessions eller RfqOnlySessions. Tillåtna: {Allowed}",
                    sessionKey, string.Join(", ", MarketDataSessions.Concat(RfqOnlySessions)));
                return;
            }

            if (string.IsNullOrEmpty(dto.SecurityId))
            {
                _logger.LogDebug("[{Session}] 35=R missing SecurityId (tag 48) — skipping", sessionKey);
                return;
            }

            // TODO: TEMPORÄRT — valutaparsfiltret är inaktiverat för product=20 (Specific Interest).
            //       Alla inkommande legs sparas oavsett currency_pair.
            //       Aktivera filtret igen när market_subscriptions är komplett konfigurerad.
            //       Sök efter "TODO: TEMPORÄRT" för att hitta detta ställe igen.
            var filteredLegs = dto.Legs.ToList();

            // ── AKTIVERA DETTA NÄR FILTRET SKA SLÅ PÅ IGEN ──────────────────
            // var allowedPairs = await GetAllowedPairsAsync(sessionKey);
            // var filteredLegs = dto.Legs
            //     .Where(l => l.CurrencyPair != null && allowedPairs.Contains(l.CurrencyPair))
            //     .ToList();
            //
            // if (filteredLegs.Count == 0)
            // {
            //     _logger.LogDebug(
            //         "[{Session}] 35=R SecurityId={SecId} — inga legs matchar prenumererade valutapar ({Pairs}) — skipping",
            //         sessionKey, dto.SecurityId, string.Join(", ", allowedPairs));
            //     return;
            // }
            // ─────────────────────────────────────────────────────────────────

            // The request first: the pricer and Interbank Prices see it as soon as it is stored. Its snapshots (35=W) are
            // kept from now on (in memory at once; the instrument is marked subscribed in the database below), except on an
            // RFQ-only session.
            if (!rfqOnly)
                _marketData?.MarkSubscribed(sessionKey, dto.SecurityId);
            var started = System.Diagnostics.Stopwatch.StartNew();
            int saved = 0;
            foreach (var leg in filteredLegs)
            {
                var entity = MapToEntity(sessionKey, dto, leg);
                try
                {
                    var id = await _quoteRequestRepo.InsertAsync(entity);
                    saved++;
                    _logger.LogDebug(
                        "[{Session}] 35=R saved: Id={Id} SecurityId={SecId} Pair={Pair} Tenor={Tenor} Strike={Strike} PutOrCall={PoC}",
                        sessionKey, id, dto.SecurityId, leg.CurrencyPair, leg.Tenor, leg.StrikePrice, leg.PutOrCall);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "[{Session}] Failed to save 35=R for SecurityId={SecId}",
                        sessionKey, dto.SecurityId);
                }
            }
            var savedMs = started.ElapsedMilliseconds;

            if (rfqOnly)
            {
                _logger.LogInformation(
                    "[{Session}] 35=R {QuoteReqId} SecurityId={SecId}: {Saved}/{Legs} leg(s) stored {StoredMs} ms after handling began; RFQ-only session, instrument not subscribed; received {Received:HH:mm:ss.fff}Z",
                    sessionKey, dto.QuoteReqId, dto.SecurityId, saved, filteredLegs.Count, savedMs, dto.ReceivedUtc);
                return;
            }

            // Then the instrument: upsert + mark subscribed per leg.
            foreach (var leg in filteredLegs)
                await UpsertAndSubscribeAsync(sessionKey, dto.SecurityId, leg);

            _logger.LogInformation(
                "[{Session}] 35=R {QuoteReqId} SecurityId={SecId}: {Saved}/{Legs} leg(s) stored {StoredMs} ms after handling began, instrument subscribed after {TotalMs} ms; received {Received:HH:mm:ss.fff}Z, handled from {Began:HH:mm:ss.fff}Z",
                sessionKey, dto.QuoteReqId, dto.SecurityId, saved, filteredLegs.Count, savedMs, started.ElapsedMilliseconds,
                dto.ReceivedUtc, DateTime.UtcNow - started.Elapsed);
        }

        /// <summary>
        /// Hämtar tillåtna valutapar från market_subscriptions (produkt 20 = Specific Interest).
        /// Cachar resultatet i minnet per session.
        /// </summary>
        private async Task<HashSet<string>> GetAllowedPairsAsync(string sessionKey)
        {
            await _cacheLock.WaitAsync();
            try
            {
                if (_allowedPairsCache.TryGetValue(sessionKey, out var cached))
                    return cached;

                var subs = await _subscriptionRepo.GetEnabledSubscriptionsAsync(sessionKey);
                var pairs = new HashSet<string>(
                    subs.Where(s => s.Product == 20).Select(s => s.CurrencyPair),
                    StringComparer.OrdinalIgnoreCase);

                _allowedPairsCache[sessionKey] = pairs;
                _logger.LogInformation(
                    "[{Session}] Tillåtna valutapar för Specific Interest: {Pairs}",
                    sessionKey, string.Join(", ", pairs));
                return pairs;
            }
            finally
            {
                _cacheLock.Release();
            }
        }

        // Requests younger than this are left to their own handling (it stores the rows first and subscribes after).
        private static readonly TimeSpan ResubscribeAfter = TimeSpan.FromMinutes(2);

        public async Task<int> ResubscribeAsync(DateTime sinceUtc)
        {
            int done = 0;
            var settledBefore = DateTime.UtcNow - ResubscribeAfter;
            foreach (var sessionKey in MarketDataSessions)
            {
                var requests = (await _quoteRequestRepo.GetSinceAsync(sessionKey, sinceUtc))
                    .Where(r => !string.IsNullOrEmpty(r.SecurityId) && r.ReceivedUtc <= settledBefore)
                    .GroupBy(r => r.SecurityId!)
                    .ToList();
                if (requests.Count == 0) continue;

                var subscribed = await _instrumentRepo.GetSubscribedAsync(sessionKey, requests.Select(g => g.Key).ToList());
                foreach (var request in requests.Where(g => !subscribed.Contains(g.Key)))
                {
                    // The newest leg: the instrument as the request handling stores it (each leg overwrites it, the last stays).
                    var row = request.OrderBy(r => r.Id).Last();
                    _marketData?.MarkSubscribed(sessionKey, request.Key);
                    try
                    {
                        if (await StoreAndMarkAsync(Instrument(sessionKey, request.Key, row.CurrencyPair, row.Tenor, row.ExpiryDate, row.Cut,
                                                                row.StrikePrice, row.QuoteStyle, row.DeltaStyle, row.PremiumCurrency, row.NotionalCurrency)))
                        {
                            done++;
                            _logger.LogWarning(
                                "[{Session}] SecurityId={SecId} (QuoteReqID {QuoteReqId}, received {Received:yyyy-MM-dd HH:mm:ss}Z) was not subscribed in the database — subscribed now",
                                sessionKey, request.Key, row.QuoteReqId, row.ReceivedUtc);
                        }
                        else
                            _logger.LogError(
                                "[{Session}] SecurityId={SecId} still not in market_instruments after the upsert — tried again in a minute",
                                sessionKey, request.Key);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex,
                            "[{Session}] SecurityId={SecId} still not subscribed in the database — tried again in a minute",
                            sessionKey, request.Key);
                    }
                }
            }
            return done;
        }

        /// <summary>A Specific Interest instrument (product 20) as a request's leg describes it.</summary>
        private static MarketInstrument Instrument(string sessionKey, string securityId, string? pair, string? tenor, string? expiry,
                                                   string? cut, decimal? strike, string? quoteStyle, string? deltaStyle,
                                                   string? premiumCcy, string? amountCcy)
        {
            DateTime? expiryDate = null;
            if (!string.IsNullOrEmpty(expiry) &&
                DateTime.TryParseExact(expiry, "yyyyMMdd",
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var parsed))
                expiryDate = parsed;

            return new MarketInstrument
            {
                SessionKey    = sessionKey,
                SecurityId    = securityId,
                CurrencyPair  = pair,
                Product       = 20,
                Tenor         = tenor,
                ExpiryDate    = expiryDate,
                Cut           = cut,
                Strike        = strike?.ToString(System.Globalization.CultureInfo.InvariantCulture),
                QuoteStyle    = quoteStyle,
                DeltaStyle    = deltaStyle,
                PremiumCcy    = premiumCcy,
                AmountCcy     = amountCcy,
                DiscoveredUtc = DateTime.UtcNow
            };
        }

        /// <summary>Upserts the instrument and marks it subscribed; false when it was not found to mark. A database error is thrown.</summary>
        private async Task<bool> StoreAndMarkAsync(MarketInstrument instrument)
        {
            await _instrumentRepo.UpsertAsync(instrument);
            return await _instrumentRepo.MarkAsSubscribedAsync(instrument.SessionKey, new[] { instrument.SecurityId }) > 0;
        }

        private async Task UpsertAndSubscribeAsync(string sessionKey, string securityId, QuoteRequestLegDto? leg)
        {
            try
            {
                if (!await StoreAndMarkAsync(Instrument(sessionKey, securityId, leg?.CurrencyPair, leg?.Tenor, leg?.ExpiryDate, leg?.Cut,
                                                        leg?.StrikePrice, leg?.QuoteStyle, leg?.DeltaStyle, leg?.PremiumCurrency, leg?.NotionalCurrency)))
                {
                    _logger.LogError(
                        "[{Session}] 35=R SecurityId={SecId} is not in market_instruments after the upsert: not marked subscribed in the database — tried again by the resubscribe check",
                        sessionKey, securityId);
                    return;
                }

                _logger.LogInformation(
                    "[{Session}] 35=R: is_subscribed=true för SecurityId={SecId} Pair={Pair} Tenor={Tenor}",
                    sessionKey, securityId, leg?.CurrencyPair, leg?.Tenor);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "[{Session}] Failed to upsert/subscribe SecurityId={SecId} — not marked subscribed in the database; tried again by the resubscribe check",
                    sessionKey, securityId);
            }
        }

        private static QuoteRequest MapToEntity(string sessionKey, QuoteRequestDto dto, QuoteRequestLegDto? leg) =>
            new()
            {
                SessionKey       = sessionKey,
                QuoteReqId       = dto.QuoteReqId,
                SecurityId       = dto.SecurityId,
                Symbol           = dto.Symbol,
                Product          = dto.Product,
                QuoteStyle       = dto.QuoteStyle,
                DeltaStyle       = dto.DeltaStyle,
                StrategyType     = dto.StrategyType,
                CurrencyPair     = leg?.CurrencyPair,
                PutOrCall        = leg?.PutOrCall,
                Tenor            = leg?.Tenor,           // ← OD/ON/1M/3M etc. sparas alltid
                ExpiryDate       = leg?.ExpiryDate,
                Cut              = leg?.Cut,
                StrikePrice      = leg?.StrikePrice,
                LegOrderQty      = leg?.LegOrderQty,
                NotionalCurrency = leg?.NotionalCurrency,
                LegSide          = leg?.LegSide,
                PremiumCurrency  = leg?.PremiumCurrency,
                RawPayload       = dto.RawPayload,
                // When the gateway received the message, not when the row is written.
                ReceivedUtc      = dto.ReceivedUtc
            };
    }
}