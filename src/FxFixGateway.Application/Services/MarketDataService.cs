using System.Collections.Concurrent;
using System.Threading.Channels;
using FxFixGateway.Domain.Entities;
using FxFixGateway.Domain.Interfaces;
using FxFixGateway.Domain.ValueObjects;
using Microsoft.Extensions.Logging;

namespace FxFixGateway.Application.Services
{
    public class MarketDataService : IMarketDataService, IAsyncDisposable
    {
        private readonly IMarketDataSnapshotRepository _snapshotRepo;
        private readonly IMarketInstrumentRepository _instrumentRepo;
        private readonly ITpicapInstrumentRepository _tpicapRepo;
        private readonly ICanonicalMarketBookRepository _canonicalRepo;
        private readonly ILogger<MarketDataService> _logger;

        private readonly Channel<(string SessionKey, MarketDataSnapshotDto Dto)> _channel;
        private readonly Task _consumerTask;
        private readonly CancellationTokenSource _cts = new();

        // Cache: sessionKey → securityId → (CurrencyPair, Product, Tenor, Cut, Strategy, Delta)
        private readonly Dictionary<string, Dictionary<string, (string? CurrencyPair, int? Product, string? Tenor, string? Cut, string? Strategy, string? Delta)>> _instrumentCache = new();
        private readonly object _cacheLock = new();

        private readonly ConcurrentDictionary<(string SessionKey, string SecurityId), bool> _subscriptionCache = new();

        private static readonly HashSet<string> MarketDataSessions = new(StringComparer.OrdinalIgnoreCase)
        {
            //"VOLB_FIXHUB_DEV",
            "VOLB_FIXHUB_PROD",
            "FXOHUB_UAT"
        };

        private static readonly HashSet<string> TpicapSessions = new(StringComparer.OrdinalIgnoreCase)
        {
            "FXOHUB_UAT"
        };

        public MarketDataService(
            IMarketDataSnapshotRepository snapshotRepo,
            IMarketInstrumentRepository instrumentRepo,
            ITpicapInstrumentRepository tpicapRepo,
            ICanonicalMarketBookRepository canonicalRepo,
            ILogger<MarketDataService> logger,
            int channelCapacity = 1000)
        {
            _snapshotRepo = snapshotRepo ?? throw new ArgumentNullException(nameof(snapshotRepo));
            _instrumentRepo = instrumentRepo ?? throw new ArgumentNullException(nameof(instrumentRepo));
            _tpicapRepo = tpicapRepo ?? throw new ArgumentNullException(nameof(tpicapRepo));
            _canonicalRepo = canonicalRepo ?? throw new ArgumentNullException(nameof(canonicalRepo));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            _channel = Channel.CreateBounded<(string, MarketDataSnapshotDto)>(
                new BoundedChannelOptions(channelCapacity)
                {
                    FullMode = BoundedChannelFullMode.DropOldest,
                    SingleReader = true,
                    SingleWriter = false
                });

            _consumerTask = Task.Run(() => ConsumeAsync(_cts.Token));
        }

        public async Task HandleMarketDataSnapshotAsync(string sessionKey, MarketDataSnapshotDto dto)
        {
            if (!MarketDataSessions.Contains(sessionKey))
                return;

            if (string.IsNullOrEmpty(dto.SecurityId))
            {
                _logger.LogDebug("[{Session}] 35=W missing SecurityId — skipping", sessionKey);
                return;
            }

            // TPICAP-sessioner har ingen prenumerationsgating — alla instrument processas
            if (!TpicapSessions.Contains(sessionKey))
            {
                var cacheKey = (sessionKey, dto.SecurityId);
                if (!_subscriptionCache.TryGetValue(cacheKey, out var isSubscribed))
                {
                    isSubscribed = await _snapshotRepo.IsSubscribedAsync(sessionKey, dto.SecurityId);
                    _subscriptionCache.TryAdd(cacheKey, isSubscribed);
                }

                if (!isSubscribed)
                {
                    _logger.LogDebug(
                        "[{Session}] 35=W SecurityId={SecId} not subscribed — skipping",
                        sessionKey, dto.SecurityId);
                    return;
                }
            }

            if (!_channel.Writer.TryWrite((sessionKey, dto)))
            {
                _logger.LogWarning(
                    "[{Session}] Market data channel full — dropping snapshot for SecurityId={SecId}",
                    sessionKey, dto.SecurityId);
            }
        }

        public async Task HandleMarketDataIncrementalRefreshAsync(
            string sessionKey, IReadOnlyList<TpicapIncrementalEntryDto> entries)
        {
            if (entries.Count == 0) return;

            var now = DateTime.UtcNow;
            var bookUpserts = new List<ActiveMarketBookEntry>();
            var canonicalUpserts = new List<CanonicalBookEntry>();
            var deletes = new List<(string SecurityId, string MdEntryType, int PositionNo)>();
            var wholeBookClears = new HashSet<string>();   // 269=J Empty Book
            var trades = new List<MarketTrade>();

            foreach (var e in entries)
            {
                // Trade prints (269=2) → market_trades, aldrig till boken.
                if (e.MdEntryType == "2")
                {
                    DateOnly? tradeDate = null;
                    if (e.EntryDate is { Length: 8 } d &&
                        DateTime.TryParseExact(d, "yyyyMMdd", null,
                            System.Globalization.DateTimeStyles.None, out var parsedDate))
                        tradeDate = DateOnly.FromDateTime(parsedDate);

                    TimeOnly? tradeTime = null;
                    if (!string.IsNullOrEmpty(e.EntryTime) &&
                        TimeSpan.TryParse(e.EntryTime, out var parsedTime))
                        tradeTime = TimeOnly.FromTimeSpan(parsedTime);

                    trades.Add(new MarketTrade
                    {
                        SecurityId = e.SecurityId,
                        SessionKey = sessionKey,
                        CurrencyPair = e.CurrencyPair,
                        Tenor = e.Tenor,
                        Cut = e.Cut,
                        Strategy = e.Strategy,
                        Delta = e.Delta,
                        Price = e.Price,
                        Size = e.Size,
                        TradeDate = tradeDate,
                        TradeTime = tradeTime,
                        TradeCondition = e.TradeCondition,
                        SnapshotId = 0,
                        ReceivedUtc = now
                    });
                    continue;
                }

                // Empty Book (269=J) → rensa hela instrumentets bok.
                if (e.MdEntryType == "J")
                {
                    wholeBookClears.Add(e.SecurityId);
                    continue;
                }

                // Endast Bid/Ask lever i boken (hoppa B=Trade Volume, C=Open Interest, m.fl.).
                if (e.MdEntryType != "0" && e.MdEntryType != "1")
                    continue;

                if (!e.PositionNo.HasValue)
                    continue;

                // Delete (279=2) — en specifik entry (MDEntryID → position_no).
                if (e.MdUpdateAction == "2")
                {
                    deletes.Add((e.SecurityId, e.MdEntryType!, e.PositionNo.Value));
                    continue;
                }

                // New/Change (279=0/1) — upsert en entry (ingen deactivate-all).
                bookUpserts.Add(new ActiveMarketBookEntry
                {
                    SecurityId = e.SecurityId,
                    SessionKey = sessionKey,
                    CurrencyPair = e.CurrencyPair,
                    MdEntryType = e.MdEntryType!,
                    PositionNo = e.PositionNo.Value,
                    Price = e.Price,
                    Size = e.Size,
                    Originator = e.Originator,
                    TraderId = e.TraderId,
                    SnapshotId = 0,
                    UpdatedUtc = now
                });

                if (e.CurrencyPair != null && e.Tenor != null && e.Cut != null && e.Strategy != null)
                {
                    canonicalUpserts.Add(new CanonicalBookEntry
                    {
                        Venue = "TPICAP",
                        SessionKey = sessionKey,
                        SecurityId = e.SecurityId,
                        CurrencyPair = e.CurrencyPair!,
                        Tenor = e.Tenor!,
                        Cut = e.Cut!,
                        Strategy = e.Strategy!,
                        Delta = e.Delta,
                        MdEntryType = e.MdEntryType!,
                        PositionNo = e.PositionNo.Value,
                        Price = e.Price,
                        Size = e.Size,
                        Originator = e.Originator,
                        TraderId = e.TraderId,
                        IsActive = true,
                        UpdatedUtc = now
                    });
                }
            }

            // Empty Book → rensa hela instrumentet (båda böckerna).
            foreach (var secId in wholeBookClears)
            {
                await _snapshotRepo.DeleteBookEntriesAsync(sessionKey, secId);
                await _canonicalRepo.DeactivateEntriesAsync("TPICAP", sessionKey, secId);
            }

            // Entry-nivå deletes (en MDEntryID i taget — nollar inte hela instrumentet).
            foreach (var d in deletes)
            {
                await _snapshotRepo.DeleteBookEntryAsync(sessionKey, d.SecurityId, d.MdEntryType, d.PositionNo);
                await _canonicalRepo.DeactivateEntryAsync("TPICAP", sessionKey, d.SecurityId, d.MdEntryType, d.PositionNo);
            }

            // Entry-nivå upserts (ren upsert — konkurrerande quotes bevaras).
            if (bookUpserts.Count > 0)
                await _snapshotRepo.UpsertIncrementalBookEntriesAsync(bookUpserts);
            if (canonicalUpserts.Count > 0)
                await _canonicalRepo.UpsertIncrementalEntriesAsync(canonicalUpserts);

            // Trades → market_trades.
            if (trades.Count > 0)
                await _snapshotRepo.InsertTradesAsync(trades);

            // Dimensionen: tpicap_instruments för instrument som bara syns i 35=X (variant E).
            var instruments = entries
                .Where(e => e.MdUpdateAction != "2"
                         && (e.MdEntryType == "0" || e.MdEntryType == "1")
                         && !string.IsNullOrEmpty(e.SecurityId))
                .GroupBy(e => e.SecurityId)
                .Select(g => g.First())
                .Select(e => new TpicapInstrument
                {
                    SessionKey = sessionKey,
                    SecurityId = e.SecurityId,
                    Symbol = e.Symbol,
                    CurrencyPair = e.CurrencyPair,
                    SecurityType = e.SecurityType,
                    SecurityExchange = e.SecurityExchange,
                    TenorValue = e.TenorValue,
                    OptionStrategy = e.OptionStrategy,
                    Tenor = e.Tenor,
                    Cut = e.Cut,
                    Strategy = e.Strategy,
                    Delta = e.Delta,
                    DiscoveredUtc = now,
                })
                .ToList();
            foreach (var instrument in instruments)
                await _tpicapRepo.UpsertCanonicalAsync(instrument);

            _logger.LogDebug(
                "[{Session}] 35=X: {Upsert} upsert, {Delete} delete, {Trade} trade, {Clear} clear",
                sessionKey, bookUpserts.Count, deletes.Count, trades.Count, wholeBookClears.Count);
        }

        private async Task ConsumeAsync(CancellationToken ct)
        {
            await foreach (var (sessionKey, dto) in _channel.Reader.ReadAllAsync(ct))
            {
                try { await ProcessSnapshotAsync(sessionKey, dto); }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[{Session}] Consumer failed for SecurityId={SecId}",
                        sessionKey, dto.SecurityId);
                }
            }
        }

        private async Task ProcessSnapshotAsync(string sessionKey, MarketDataSnapshotDto dto)
        {
            string? currencyPair, cut, strategy, delta;
            int? product;
            string? tenor;

            if (TpicapSessions.Contains(sessionKey))
            {
                // Upserta TPICAP-instrumentet och använd DTO-fälten som metadata
                var tpicapInstrument = new TpicapInstrument
                {
                    SessionKey = sessionKey,
                    SecurityId = dto.SecurityId!,
                    Symbol = dto.Symbol,
                    CurrencyPair = dto.Symbol,
                    SecurityType = dto.SecurityType,
                    SecurityExchange = dto.SecurityExchange,
                    TenorValue = dto.TenorValue,
                    OptionStrategy = dto.OptionStrategy,
                    MaturityMonthYear = dto.MaturityMonthYear,
                    PremiumType = dto.PremiumType,
                    DeliveryType = dto.DeliveryType,
                    DeltaBasis = dto.DeltaBasis,
                    PremiumCcy = dto.PremiumCurrencyTag,
                    Tenor = dto.TenorValue,
                    Cut = TpicapCutToCanonical(dto.SecurityExchange),
                    Strategy = TpicapStrategyToCanonical(dto.OptionStrategy),
                    Delta = TpicapDeltaToCanonical(dto.OptionStrategy, dto.DeltaType),
                    DiscoveredUtc = DateTime.UtcNow,
                };
                await _tpicapRepo.UpsertAsync(tpicapInstrument);

                currencyPair = tpicapInstrument.CurrencyPair;
                product = null;
                tenor = tpicapInstrument.Tenor;
                cut = tpicapInstrument.Cut;
                strategy = tpicapInstrument.Strategy;
                delta = tpicapInstrument.Delta;   // FIX: var hårdkodat null — tappade beräknat delta
            }
            else
            {
                (currencyPair, product, tenor, cut, strategy, delta) =
                    await GetInstrumentMetaAsync(sessionKey, dto.SecurityId!);
            }

            var snapshot = new MarketDataSnapshot
            {
                SessionKey = sessionKey,
                SecurityId = dto.SecurityId!,
                MdReqId = dto.MdReqId,
                CurrencyPair = currencyPair,
                Product = product,
                Tenor = tenor,
                Cut = cut,
                Strategy = strategy,
                Delta = delta,
                RawPayload = dto.RawPayload,
                ReceivedUtc = DateTime.UtcNow,
                Entries = dto.Entries.Select(e => MapEntry(e, dto.SecurityId!)).ToList(),
                EntryCount = dto.Entries.Count
            };

            if (dto.Entries.Count == 0)
            {
                _logger.LogInformation(
                    "[{Session}] 35=W 268=0 for SecurityId={SecId} — clearing active_market_book",
                    sessionKey, dto.SecurityId);
                await _snapshotRepo.InsertSnapshotAsync(snapshot, Array.Empty<MarketTrade>());
                await _snapshotRepo.DeleteBookEntriesAsync(sessionKey, dto.SecurityId!);

                var venue0 = TpicapSessions.Contains(sessionKey) ? "TPICAP" : "VOLBROKER";
                await _canonicalRepo.DeactivateEntriesAsync(venue0, sessionKey, dto.SecurityId!);
                return;
            }

            // Kanoniska display-värden. Volbroker: råkod → display-namn.
            // TPICAP: snapshot.Strategy/snapshot.Delta är redan kanoniska — passera igenom
            // oförändrat (tidigare kördes de av misstag genom Volbrokers råkod-konvertering,
            // vilket alltid gav null för TPICAP eftersom "Straddle" m.fl. inte matchar någon
            // Volbroker-case).
            var venue = TpicapSessions.Contains(sessionKey) ? "TPICAP" : "VOLBROKER";
            var canonicalStrategy = TpicapSessions.Contains(sessionKey)
                ? snapshot.Strategy
                : ToStrategyDisplayName(snapshot.Strategy);
            var canonicalDelta = TpicapSessions.Contains(sessionKey)
                ? snapshot.Delta
                : ToDeltaDisplayName(snapshot.Delta);

            var trades = BuildTrades(snapshot, snapshotId: 0, canonicalStrategy, canonicalDelta);
            var snapshotId = await _snapshotRepo.InsertSnapshotAsync(snapshot, trades);

            _logger.LogDebug(
                "[{Session}] 35=W saved: SnapshotId={Id} SecurityId={SecId} Pair={Pair} Tenor={Tenor} Entries={Count}",
                sessionKey, snapshotId, dto.SecurityId, currencyPair, tenor, snapshot.Entries.Count);

            var bookEntries = BuildBookEntries(snapshot, snapshotId);
            if (bookEntries.Count > 0)
                await _snapshotRepo.UpsertBookEntriesAsync(bookEntries);

            var canonical = BuildCanonicalEntries(
                venue, sessionKey, dto.SecurityId!,
                currencyPair, tenor, cut, canonicalStrategy, canonicalDelta, product, snapshot.Entries);
            if (canonical.Count > 0)
                await _canonicalRepo.UpsertEntriesAsync(canonical);
        }

        private static List<CanonicalBookEntry> BuildCanonicalEntries(
            string venue, string sessionKey, string securityId,
            string? currencyPair, string? tenor, string? cut, string? strategy, string? delta,
            int? product, IEnumerable<MarketDataEntry> entries)
        {
            // Kräver att alla kanoniska dimensioner finns — annars går raden inte att merga.
            if (currencyPair == null || tenor == null || cut == null || strategy == null)
                return new List<CanonicalBookEntry>();

            var now = DateTime.UtcNow;
            return entries
                .Where(e => e.PositionNo.HasValue && e.MdEntryType != "2")
                .Select(e => new CanonicalBookEntry
                {
                    Venue = venue,
                    SessionKey = sessionKey,
                    SecurityId = securityId,
                    CurrencyPair = currencyPair,
                    Tenor = tenor,
                    Cut = cut,
                    Strategy = strategy,
                    Delta = delta,
                    Product = product,
                    MdEntryType = e.MdEntryType,
                    PositionNo = e.PositionNo!.Value,
                    Price = e.Price,
                    Size = e.Size,
                    Originator = e.Originator,
                    TraderId = e.TraderId,
                    IsActive = true,
                    UpdatedUtc = now
                })
                .ToList();
        }

        private static string? TpicapCutToCanonical(string? securityExchange) => securityExchange switch
        {
            "1000NYK" => "NY",
            "1000LDN" => "LN",
            "1500TKY" => "TK",
            _ => securityExchange
        };

        private static string? TpicapStrategyToCanonical(string? optionStrategy) => optionStrategy switch
        {
            "2" => "Straddle",
            "4" => "Risk Reversal",
            "A" => "Straddle Calendar",
            "B" => "Butterfly",
            _ => optionStrategy
        };

        // TPICAP DeltaType (tag 6008): "0"=AllPitIncludingTenDeltaPit, "1"=TenDeltaPit.
        // Straddle är ATM. RR/BF: 6008=1 → 10D, annars 25D.
        private static string? TpicapDeltaToCanonical(string? optionStrategy, string? deltaType)
        {
            if (optionStrategy == "2") return "ATM";   // Straddle
            return deltaType == "1" ? "10D" : "25D";
        }

        private static List<ActiveMarketBookEntry> BuildBookEntries(MarketDataSnapshot snapshot, long snapshotId)
        {
            var now = DateTime.UtcNow;
            return snapshot.Entries
                .Where(e => e.PositionNo.HasValue && e.MdEntryType != "2")
                .Select(e => new ActiveMarketBookEntry
                {
                    SecurityId = snapshot.SecurityId,
                    SessionKey = snapshot.SessionKey,
                    CurrencyPair = snapshot.CurrencyPair,
                    MdEntryType = e.MdEntryType,
                    PositionNo = e.PositionNo!.Value,
                    Price = e.Price,
                    Size = e.Size,
                    Originator = e.Originator,
                    TraderId = e.TraderId,
                    QuoteCondition = e.QuoteCondition,
                    SnapshotId = snapshotId,
                    UpdatedUtc = now
                })
                .ToList();
        }

        private static List<MarketTrade> BuildTrades(
            MarketDataSnapshot snapshot, long snapshotId, string? canonicalStrategy, string? canonicalDelta)
        {
            var now = DateTime.UtcNow;
            return snapshot.Entries
                .Where(e => e.MdEntryType == "2")
                .Select(e => new MarketTrade
                {
                    SecurityId = snapshot.SecurityId,
                    SessionKey = snapshot.SessionKey,
                    CurrencyPair = snapshot.CurrencyPair,
                    Tenor = snapshot.Tenor,
                    Cut = snapshot.Cut,
                    Strategy = canonicalStrategy,
                    Delta = canonicalDelta,
                    Price = e.Price,
                    Size = e.Size,
                    TradeDate = e.EntryDate.HasValue ? DateOnly.FromDateTime(e.EntryDate.Value) : null,
                    TradeTime = e.EntryTime.HasValue ? TimeOnly.FromTimeSpan(e.EntryTime.Value) : null,
                    TradeCondition = e.TradeCondition,
                    SnapshotId = snapshotId,
                    ReceivedUtc = now
                })
                .ToList();
        }

        private static string? ToStrategyDisplayName(string? fixValue) => fixValue?.Trim() switch
        {
            "1" => "Single Leg",
            "2" => "Straddle",
            "3" => "Strangle",
            "4" => "Risk Reversal",
            "G" => "Butterfly",
            "S" => "Generic Spread",
            _ => null
        };

        private static string? ToDeltaDisplayName(string? fixValue)
        {
            if (string.IsNullOrWhiteSpace(fixValue)) return null;
            var trimmed = fixValue.Trim();
            return trimmed == "0" ? "ATM" : trimmed;
        }

        private async Task<(string? CurrencyPair, int? Product, string? Tenor, string? Cut, string? Strategy, string? Delta)>
            GetInstrumentMetaAsync(string sessionKey, string securityId)
        {
            lock (_cacheLock)
            {
                if (_instrumentCache.TryGetValue(sessionKey, out var sessionCache) &&
                    sessionCache.TryGetValue(securityId, out var cached))
                    return cached;
            }

            var instrument = await _instrumentRepo.GetBySecurityIdAsync(sessionKey, securityId);
            var meta = (instrument?.CurrencyPair, instrument?.Product,
                        instrument?.Tenor, instrument?.Cut,
                        instrument?.Strategy, instrument?.Delta);

            lock (_cacheLock)
            {
                if (!_instrumentCache.ContainsKey(sessionKey))
                    _instrumentCache[sessionKey] = new();
                _instrumentCache[sessionKey][securityId] = meta;
            }

            return meta;
        }

        private static MarketDataEntry MapEntry(MarketDataEntryDto dto, string securityId)
        {
            DateTime? entryDate = null;
            if (dto.EntryDate is { Length: 8 } d &&
                DateTime.TryParseExact(d, "yyyyMMdd", null,
                    System.Globalization.DateTimeStyles.None, out var parsedDate))
                entryDate = parsedDate;

            TimeSpan? entryTime = null;
            if (!string.IsNullOrEmpty(dto.EntryTime) &&
                TimeSpan.TryParse(dto.EntryTime, out var parsedTime))
                entryTime = parsedTime;

            return new MarketDataEntry
            {
                SecurityId = securityId,
                MdEntryType = dto.MdEntryType ?? string.Empty,
                Price = dto.Price,
                Size = dto.Size,
                QuoteCondition = dto.QuoteCondition,
                TradeCondition = dto.TradeCondition,
                PositionNo = dto.PositionNo,
                Originator = dto.Originator,
                TraderId = dto.TraderId,
                ExecInst = dto.ExecInst,
                Scope = dto.Scope,
                EntryDate = entryDate,
                EntryTime = entryTime
            };
        }

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync();
            _channel.Writer.Complete();
            try { await _consumerTask.ConfigureAwait(false); } catch { /* intentional */ }
            _cts.Dispose();
        }
    }
}