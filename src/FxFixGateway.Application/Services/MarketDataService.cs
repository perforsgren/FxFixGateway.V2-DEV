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

        private readonly Channel<(string SessionKey, IReadOnlyList<TpicapIncrementalEntryDto> Entries)> _incrementalChannel;
        private readonly Task _incrementalConsumerTask;

        // One snapshot queue and one writer per session: a session's snapshots are written in arrival order, and a busy
        // session does not hold up the others (18 Oct: Volbroker's 944 startup snapshots waited 100 s behind FXOhub UAT's
        // 1,142 in the one shared queue). Created on a session's first snapshot.
        private readonly ConcurrentDictionary<string, Lazy<SnapshotQueue>> _queues = new(StringComparer.OrdinalIgnoreCase);
        private readonly Task _reporter;
        private readonly CancellationTokenSource _cts = new();
        private static readonly TimeSpan ReportEvery = TimeSpan.FromMinutes(1);
        private static readonly TimeSpan Behind = TimeSpan.FromMinutes(1);

        // Cache: sessionKey → securityId → (CurrencyPair, Product, Tenor, Cut, Strategy, Delta)
        private readonly Dictionary<string, Dictionary<string, (string? CurrencyPair, int? Product, string? Tenor, string? Cut, string? Strategy, string? Delta)>> _instrumentCache = new();
        private readonly object _cacheLock = new();

        private readonly ConcurrentDictionary<(string SessionKey, string SecurityId), bool> _subscriptionCache = new();

        // Subscription checks against the database: one at a time per instrument, and at most CheckConcurrency together.
        // Right after Volbroker's subscription about 1,800 instruments send their first snapshots within seconds; one query
        // per snapshot emptied the fxvol connection pool, and MySql.Data waits for a free pooled connection by blocking its
        // thread, up to the 30 s connection timeout (18 Oct: checks failed after 30 s, snapshots not stored).
        private const int CheckConcurrency = 4;
        private readonly SemaphoreSlim _checkGate = new(CheckConcurrency, CheckConcurrency);
        private readonly ConcurrentDictionary<(string SessionKey, string SecurityId), Lazy<Task<bool?>>> _checks = new();

        private readonly int _channelCapacity;

        private sealed record QueuedSnapshot(MarketDataSnapshotDto Dto, DateTime QueuedUtc);

        private sealed class SnapshotQueue
        {
            public SnapshotQueue(string sessionKey) => SessionKey = sessionKey;
            public string SessionKey { get; }
            public Channel<QueuedSnapshot> Channel = null!;
            public Task Consumer = Task.CompletedTask;
            // Dropped by the full queue (oldest first): the first drop is logged, and the count when it has caught up.
            public long Dropped;
            public int Dropping;
            // Written since the last report.
            public int Written;
        }

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

            _channelCapacity = channelCapacity;

            _incrementalChannel = Channel.CreateBounded<(string, IReadOnlyList<TpicapIncrementalEntryDto>)>(
                new BoundedChannelOptions(channelCapacity)
                {
                    FullMode = BoundedChannelFullMode.DropOldest,
                    SingleReader = true,
                    SingleWriter = false
                });

            _incrementalConsumerTask = Task.Run(() => ConsumeIncrementalAsync(_cts.Token));
            _reporter = Task.Run(() => ReportAsync(_cts.Token));
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
                    // Snapshots of an instrument that come while its check runs wait for that check (no thread is held).
                    var answer = await _checks.GetOrAdd(cacheKey, key => new Lazy<Task<bool?>>(() => CheckSubscribedAsync(key))).Value;
                    if (answer == null)
                        return;                                   // the check failed (logged once): not stored, asked again next time
                    isSubscribed = answer.Value;
                }

                if (!isSubscribed)
                {
                    _logger.LogDebug(
                        "[{Session}] 35=W SecurityId={SecId} not subscribed — skipping",
                        sessionKey, dto.SecurityId);
                    return;
                }
            }

            if (!QueueFor(sessionKey).Channel.Writer.TryWrite(new QueuedSnapshot(dto, DateTime.UtcNow)))
            {
                _logger.LogWarning(
                    "[{Session}] Market data channel full — dropping snapshot for SecurityId={SecId}",
                    sessionKey, dto.SecurityId);
            }
        }

        /// <summary>
        /// Asks the database whether the instrument is subscribed, at most <see cref="CheckConcurrency"/> checks at a time.
        /// The answer is cached before the check is removed from the in-flight list, so a later snapshot finds one or the
        /// other. Null when the check failed: logged here once for all the snapshots that waited for it, nothing cached, and
        /// the next snapshot asks again (a 35=W is a full snapshot, so the next one carries the whole book).
        /// </summary>
        private async Task<bool?> CheckSubscribedAsync((string SessionKey, string SecurityId) key)
        {
            try
            {
                await _checkGate.WaitAsync().ConfigureAwait(false);
                try
                {
                    var isSubscribed = await _snapshotRepo.IsSubscribedAsync(key.SessionKey, key.SecurityId).ConfigureAwait(false);
                    // GetOrAdd: a MarkSubscribed (QuoteRequest) that came meanwhile wins over this answer.
                    return _subscriptionCache.GetOrAdd(key, isSubscribed);
                }
                finally
                {
                    _checkGate.Release();
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "[{Session}] 35=W SecurityId={SecId}: subscription check failed — snapshot not stored, checked again on the next one",
                    key.SessionKey, key.SecurityId);
                return null;
            }
            finally
            {
                _checks.TryRemove(key, out _);
            }
        }

        private SnapshotQueue QueueFor(string sessionKey)
            => _queues.GetOrAdd(sessionKey, key => new Lazy<SnapshotQueue>(() => StartQueue(key))).Value;

        private SnapshotQueue StartQueue(string sessionKey)
        {
            var queue = new SnapshotQueue(sessionKey);
            queue.Channel = Channel.CreateBounded<QueuedSnapshot>(
                new BoundedChannelOptions(_channelCapacity)
                {
                    FullMode = BoundedChannelFullMode.DropOldest,
                    SingleReader = true,
                    SingleWriter = false
                },
                dropped => OnSnapshotDropped(queue, dropped));
            queue.Consumer = Task.Run(() => ConsumeAsync(queue, _cts.Token));
            return queue;
        }

        private void OnSnapshotDropped(SnapshotQueue queue, QueuedSnapshot item)
        {
            Interlocked.Increment(ref queue.Dropped);
            if (Interlocked.Exchange(ref queue.Dropping, 1) == 0)
                _logger.LogWarning(
                    "[{Session}] Market data queue full ({Capacity}): dropping the oldest snapshots (first SecurityId={SecId}); the count follows when it has caught up",
                    queue.SessionKey, _channelCapacity, item.Dto.SecurityId);
        }

        private async Task ReportAsync(CancellationToken ct)
        {
            using var timer = new PeriodicTimer(ReportEvery);
            try
            {
                while (await timer.WaitForNextTickAsync(ct))
                {
                    try { Report(); }
                    catch (Exception ex) { _logger.LogError(ex, "Market data queue report failed"); }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
            }
        }

        /// <summary>Once a minute: a warning for each session whose oldest queued snapshot has waited a minute or more.</summary>
        private void Report()
        {
            var now = DateTime.UtcNow;
            foreach (var lazy in _queues.Values)
            {
                if (!lazy.IsValueCreated) continue;
                var queue = lazy.Value;
                var written = Interlocked.Exchange(ref queue.Written, 0);
                if (!queue.Channel.Reader.TryPeek(out var oldest) || now - oldest.QueuedUtc < Behind) continue;
                _logger.LogWarning(
                    "[{Session}] Market data queue behind: {Queued} snapshot(s) waiting, the oldest {Oldest:0} s; {Written} written in the last minute",
                    queue.SessionKey, queue.Channel.Reader.Count, (now - oldest.QueuedUtc).TotalSeconds, written);
            }
        }

        public void MarkSubscribed(string sessionKey, string securityId)
        {
            if (string.IsNullOrEmpty(securityId)) return;
            // Overwrites a "not subscribed" cached by a snapshot that came before the QuoteRequest was handled.
            _subscriptionCache[(sessionKey, securityId)] = true;
        }

        public Task HandleMarketDataIncrementalRefreshAsync(string sessionKey, IReadOnlyList<TpicapIncrementalEntryDto> entries)
        {
            if (entries.Count == 0) return Task.CompletedTask;

            if (!_incrementalChannel.Writer.TryWrite((sessionKey, entries)))
            {
                _logger.LogWarning(
                    "[{Session}] Incremental market data channel full — dropping batch of {Count} entries",
                    sessionKey, entries.Count);
            }

            return Task.CompletedTask;
        }

        private async Task ProcessIncrementalRefreshAsync(string sessionKey, IReadOnlyList<TpicapIncrementalEntryDto> entries)
        {

            var now = DateTime.UtcNow;
            var bookUpserts = new List<ActiveMarketBookEntry>();
            var canonicalUpserts = new List<CanonicalBookEntry>();
            var deletes = new List<(string SecurityId, string MdEntryType, int PositionNo)>();
            var wholeBookClears = new HashSet<string>();
            var trades = new List<MarketTrade>();

            var ownReconciliations = new List<(string SecurityId, string MdEntryType, string Originator, string? TraderId, int KeepPositionNo)>();

            // (security_id, md_entry_type) som får granulär 35=X-aktivitet i den här
            // batchen. 35=W skriver alltid Bid/Ask med position_no=1 (fallback, saknar
            // MDEntryID). Så fort 35=X strömmar riktiga per-order-uppdateringar för en
            // sida vet vi att den gamla aggregerade 35=W-platshållaren är inaktuell —
            // annars kan den bli en spökrad som aldrig rensas förrän nästa 35=W
            // (dvs nästa omkoppling), trots att alla individuella ordrar redan är borta.
            var sidesTouchedByIncremental = new HashSet<(string SecurityId, string MdEntryType)>();

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
                        Originator = e.Originator,
                        TraderId = e.TraderId,
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

                sidesTouchedByIncremental.Add((e.SecurityId, e.MdEntryType!));

                // Delete (279=2) — en specifik entry (MDEntryID → position_no).
                if (e.MdUpdateAction == "2")
                {
                    deletes.Add((e.SecurityId, e.MdEntryType!, e.PositionNo.Value));

                    if (!string.IsNullOrEmpty(e.Originator))
                        ownReconciliations.Add((e.SecurityId, e.MdEntryType!, e.Originator!, e.TraderId, -1));

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

                if (!string.IsNullOrEmpty(e.Originator))
                    ownReconciliations.Add((e.SecurityId, e.MdEntryType!, e.Originator!, e.TraderId, e.PositionNo.Value));

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

            // Rensa 35=W:s aggregerade platshållare (position_no=1) för varje sida som nu
            // får granulär 35=X-aktivitet — INNAN dagens deletes/upserts körs, så att om
            // en riktig hash råkar bli exakt 1 (extremt osannolikt) återaktiveras den
            // korrekt av upsert-steget nedan.
            foreach (var (secId, side) in sidesTouchedByIncremental)
            {
                await _snapshotRepo.DeleteBookEntryAsync(sessionKey, secId, side, 1);
                await _canonicalRepo.DeactivateEntryAsync("TPICAP", sessionKey, secId, side, 1);
            }

            // Entry-nivå deletes (en MDEntryID i taget — nollar inte hela instrumentet).
            foreach (var d in deletes)
            {
                await _snapshotRepo.DeleteBookEntryAsync(sessionKey, d.SecurityId, d.MdEntryType, d.PositionNo);
                await _canonicalRepo.DeactivateEntryAsync("TPICAP", sessionKey, d.SecurityId, d.MdEntryType, d.PositionNo);
            }

            // Reconciliation för egna priser vars position_no kan ha bytt schema
            // (35=W-fallback=1 vs 35=X MDEntryID-hash) sedan förra gången.
            foreach (var r in ownReconciliations)
            {
                await _snapshotRepo.DeactivateStaleOwnEntriesAsync(
                    sessionKey, r.SecurityId, r.MdEntryType, r.Originator, r.TraderId, r.KeepPositionNo);
                await _canonicalRepo.DeactivateStaleOwnEntriesAsync(
                    "TPICAP", sessionKey, r.SecurityId, r.MdEntryType, r.Originator, r.TraderId, r.KeepPositionNo);
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

        private async Task ConsumeIncrementalAsync(CancellationToken ct)
        {
            await foreach (var (sessionKey, entries) in _incrementalChannel.Reader.ReadAllAsync(ct))
            {
                try { await ProcessIncrementalRefreshAsync(sessionKey, entries); }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "[{Session}] Incremental consumer failed for batch of {Count} entries",
                        sessionKey, entries.Count);
                }
            }
        }

        private async Task ConsumeAsync(SnapshotQueue queue, CancellationToken ct)
        {
            try
            {
                await foreach (var item in queue.Channel.Reader.ReadAllAsync(ct))
                {
                    try { await ProcessSnapshotAsync(queue.SessionKey, item.Dto); }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "[{Session}] Consumer failed for SecurityId={SecId}",
                            queue.SessionKey, item.Dto.SecurityId);
                    }
                    Interlocked.Increment(ref queue.Written);

                    // Caught up after dropping: how many were lost (each a full snapshot; a later one of the same instrument
                    // carries its whole book, but trades only in a dropped one are not stored).
                    if (queue.Channel.Reader.Count == 0 && Interlocked.Exchange(ref queue.Dropping, 0) == 1)
                        _logger.LogWarning("[{Session}] Market data queue caught up: {Dropped} snapshot(s) were dropped while it was full ({Capacity})",
                            queue.SessionKey, Interlocked.Exchange(ref queue.Dropped, 0), _channelCapacity);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
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
            {
                await _snapshotRepo.UpsertBookEntriesAsync(bookEntries);
            }
            else
            {
                // Snapshoten hade dto.Entries.Count > 0 (t.ex. en enda Trade-entry) men
                // INGA bid/ask kvar efter att BuildBookEntries filtrerat bort trades.
                // Utan det här rensas aldrig en tidigare aktiv bid/ask som dragits tillbaka
                // — 268=0-grenen ovan triggas bara när meddelandet är helt tomt, inte när
                // det bara innehåller en trade-print.
                await _snapshotRepo.DeleteBookEntriesAsync(sessionKey, dto.SecurityId!);
            }

            var canonical = BuildCanonicalEntries(
                venue, sessionKey, dto.SecurityId!,
                currencyPair, tenor, cut, canonicalStrategy, canonicalDelta, product, snapshot.Entries);
            if (canonical.Count > 0)
            {
                await _canonicalRepo.UpsertEntriesAsync(canonical);
            }
            else
            {
                await _canonicalRepo.DeactivateEntriesAsync(venue, sessionKey, dto.SecurityId!);
            }
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
                    Originator = e.Originator,
                    TraderId = e.TraderId,
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
            var queues = _queues.Values.Where(q => q.IsValueCreated).Select(q => q.Value).ToList();
            foreach (var queue in queues) queue.Channel.Writer.TryComplete();
            _incrementalChannel.Writer.Complete();
            foreach (var queue in queues)
                try { await queue.Consumer.ConfigureAwait(false); } catch { /* intentional */ }
            try { await _incrementalConsumerTask.ConfigureAwait(false); } catch { /* intentional */ }
            try { await _reporter.ConfigureAwait(false); } catch { /* intentional */ }
            _cts.Dispose();
        }

    }
}