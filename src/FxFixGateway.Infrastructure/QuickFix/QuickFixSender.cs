using FxFixGateway.Domain.Interfaces;
using Microsoft.Extensions.Logging;
using QF = global::QuickFix;

namespace FxFixGateway.Infrastructure.QuickFix
{
    /// <summary>
    /// Skickar FIX-meddelanden för market data-prenumeration via QuickFix.
    /// Implementerar IMarketDataSubscriber (definierad i Domain).
    /// </summary>
    public class QuickFixSender : IMarketDataSubscriber
    {
        private readonly Dictionary<string, QF.SessionID> _sessionIdMap;
        private readonly ILogger<QuickFixSender> _logger;

        public QuickFixSender(
            Dictionary<string, QF.SessionID> sessionIdMap,
            ILogger<QuickFixSender> logger)
        {
            _sessionIdMap = sessionIdMap ?? throw new ArgumentNullException(nameof(sessionIdMap));
            _logger       = logger       ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// Skickar SecurityListRequest (35=x).
        /// Volbroker svarar med ett eller flera 35=y meddelanden.
        /// Tag 320 (SecurityReqID) måste vara unikt per request.
        /// </summary>
        public void SendSecurityListRequest(string sessionKey)
        {
            if (!_sessionIdMap.TryGetValue(sessionKey, out var sessionId))
            {
                _logger.LogError("[{Session}] SendSecurityListRequest — session not found in map", sessionKey);
                return;
            }

            try
            {
                var msg = new QF.Message();
                msg.Header.SetField(new QF.Fields.MsgType("x"));
                msg.SetField(new QF.Fields.SecurityReqID($"SECLIST-{sessionKey}-{DateTime.UtcNow:yyyyMMddHHmmss}"));

                QF.Session.SendToTarget(msg, sessionId);

                _logger.LogInformation("[{Session}] SecurityListRequest (35=x) sent", sessionKey);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[{Session}] Failed to send SecurityListRequest: {Error}", sessionKey, ex.Message);
            }
        }

        /// <summary>
        /// Skickar MarketDataRequest (35=V).
        /// Enligt Volbroker API-guiden är 35=V mycket tunn — bara MDReqID krävs.
        /// Tag 262 (MDReqID) måste vara unikt per request.
        /// </summary>
        public void SendMarketDataRequest(string sessionKey, IEnumerable<string> securityIds)
        {
            if (!_sessionIdMap.TryGetValue(sessionKey, out var sessionId))
            {
                _logger.LogError("[{Session}] SendMarketDataRequest — session not found in map", sessionKey);
                return;
            }

            var ids = securityIds.ToList();
            if (ids.Count == 0)
            {
                _logger.LogWarning("[{Session}] SendMarketDataRequest called with empty securityIds", sessionKey);
                return;
            }

            try
            {
                var msg = new QF.Message();
                msg.Header.SetField(new QF.Fields.MsgType("V"));
                msg.SetField(new QF.Fields.MDReqID($"MD-{sessionKey}-{DateTime.UtcNow:yyyyMMddHHmmss}"));

                QF.Session.SendToTarget(msg, sessionId);

                _logger.LogInformation(
                    "[{Session}] MarketDataRequest (35=V) sent for {Count} instrument(s): [{Ids}]",
                    sessionKey, ids.Count, string.Join(", ", ids.Take(5)) + (ids.Count > 5 ? "..." : ""));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[{Session}] Failed to send MarketDataRequest: {Error}", sessionKey, ex.Message);
            }
        }

        /// <summary>
        /// Probe: skickar en fullständig MarketDataRequest (35=V) för FXOHUB.
        /// Till skillnad från Volbrokers tunna 35=V namnger denna symbolerna explicit.
        /// För varje symbol begärs tre strategier: Straddle (ATM), Risk Reversal och
        /// Butterfly. RR/BF begärs med DeltaType=0 (AllPitIncludingTenDeltaPit) så att
        /// både 25D- och 10D-pit levereras.
        /// </summary>
        public void SendMarketDataRequestProbeOLD(string sessionKey, IEnumerable<string> symbols)
        {
            if (!_sessionIdMap.TryGetValue(sessionKey, out var sessionId))
            {
                _logger.LogError("[{Session}] 35=V probe – session not found in map", sessionKey);
                return;
            }

            var symbolList = symbols?.ToList() ?? new List<string>();
            if (symbolList.Count == 0)
            {
                _logger.LogWarning("[{Session}] 35=V probe called with no symbols", sessionKey);
                return;
            }

            // (OptionStrategy 9126, DeltaType 6008|null). Straddle=ATM → ingen delta-pit.
            // RR/BF med 6008=0 (AllPitIncludingTenDeltaPit) → inkluderar 10D-pit.
            var strategySpecs = new (string OptionStrategy, string? DeltaType)[]
            {
                ("2", null),  // Straddle (ATM)
                ("4", "0"),   // Risk Reversal
                ("B", "0"),   // Butterfly
            };

            try
            {
                var msg = new QF.Message();
                msg.Header.SetField(new QF.Fields.MsgType("V"));

                // 262 MDReqID – måste vara unikt per request
                msg.SetField(new QF.Fields.MDReqID(
                    $"MDPROBE-{sessionKey}-{DateTime.UtcNow:yyyyMMddHHmmssfff}"));

                // 263 SubscriptionRequestType: 1 = Snapshot + Updates (prenumeration)
                msg.SetField(new QF.Fields.SubscriptionRequestType('1'));

                // 264 MarketDepth: 0 = full book  (1 = top of book)
                msg.SetField(new QF.Fields.MarketDepth(0));

                // 265 MDUpdateType: 1 = Incremental (krävs ofta tillsammans med 263=1)
                msg.SetField(new QF.Fields.MDUpdateType(1));

                // 267 NoMDEntryTypes – Bid + Offer + Trade
                msg.SetField(new QF.Fields.NoMDEntryTypes(3));

                var bidGroup = new QF.Group(267, 269);
                bidGroup.SetField(new QF.Fields.MDEntryType('0')); // 0 = Bid
                msg.AddGroup(bidGroup);

                var offerGroup = new QF.Group(267, 269);
                offerGroup.SetField(new QF.Fields.MDEntryType('1')); // 1 = Offer/Ask
                msg.AddGroup(offerGroup);

                var tradeGroup = new QF.Group(267, 269);
                tradeGroup.SetField(new QF.Fields.MDEntryType('2')); // 2 = Trade
                msg.AddGroup(tradeGroup);

                // 146 NoRelatedSym – en grupp per (symbol × strategi)
                msg.SetField(new QF.Fields.NoRelatedSym(symbolList.Count * strategySpecs.Length));
                foreach (var symbol in symbolList)
                {
                    foreach (var (optionStrategy, deltaType) in strategySpecs)
                    {
                        var symGroup = new QF.Group(146, 55);
                        symGroup.SetField(new QF.Fields.Symbol(symbol));              // 55
                        symGroup.SetField(new QF.Fields.SecurityType("FOR"));          // 167 - Foreign Exchange Contract
                        symGroup.SetField(new QF.Fields.SecurityExchange("1000NYK"));  // 207 - NY cut 10:00
                        symGroup.SetField(new QF.Fields.StringField(6215, "ALL"));            // TenorValue - alla tenorer
                        symGroup.SetField(new QF.Fields.StringField(9126, optionStrategy));   // OptionStrategy
                        if (deltaType != null)
                            symGroup.SetField(new QF.Fields.StringField(6008, deltaType));    // DeltaType
                        msg.AddGroup(symGroup);
                    }
                }

                QF.Session.SendToTarget(msg, sessionId);

                _logger.LogInformation(
                    "[{Session}] 35=V probe sent for {Count} symbol(s) × {Strat} strategies: [{Symbols}]",
                    sessionKey, symbolList.Count, strategySpecs.Length, string.Join(", ", symbolList));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[{Session}] Failed to send 35=V probe: {Error}", sessionKey, ex.Message);
            }
        }

        /// <summary>
        /// Probe: skickar en fullständig MarketDataRequest (35=V) för FXOHUB.
        /// TILLFÄLLIGT NEDBANTAD för felsökning av "system is not ready yet, try later" —
        /// enbart Straddle, Bid+Offer, ingen DeltaType/Trade. Samma form som den
        /// ursprungliga probe som är känd att ha fungerat.
        /// </summary>
        public void SendMarketDataRequestProbe(string sessionKey, IEnumerable<string> symbols)
        {
            if (!_sessionIdMap.TryGetValue(sessionKey, out var sessionId))
            {
                _logger.LogError("[{Session}] 35=V probe – session not found in map", sessionKey);
                return;
            }

            var symbolList = symbols?.ToList() ?? new List<string>();
            if (symbolList.Count == 0)
            {
                _logger.LogWarning("[{Session}] 35=V probe called with no symbols", sessionKey);
                return;
            }

            try
            {
                var msg = new QF.Message();
                msg.Header.SetField(new QF.Fields.MsgType("V"));

                // 262 MDReqID – måste vara unikt per request
                msg.SetField(new QF.Fields.MDReqID(
                    $"MDPROBE-{sessionKey}-{DateTime.UtcNow:yyyyMMddHHmmssfff}"));

                // 263 SubscriptionRequestType: 1 = Snapshot + Updates (prenumeration)
                msg.SetField(new QF.Fields.SubscriptionRequestType('1'));

                // 264 MarketDepth: 0 = full book  (1 = top of book)
                msg.SetField(new QF.Fields.MarketDepth(0));

                // 265 MDUpdateType: 1 = Incremental (krävs ofta tillsammans med 263=1)
                msg.SetField(new QF.Fields.MDUpdateType(1));

                // 267 NoMDEntryTypes – Bid + Offer
                msg.SetField(new QF.Fields.NoMDEntryTypes(2));

                var bidGroup = new QF.Group(267, 269);
                bidGroup.SetField(new QF.Fields.MDEntryType('0')); // 0 = Bid
                msg.AddGroup(bidGroup);

                var offerGroup = new QF.Group(267, 269);
                offerGroup.SetField(new QF.Fields.MDEntryType('1')); // 1 = Offer/Ask
                msg.AddGroup(offerGroup);

                // 146 NoRelatedSym – EN grupp, enbart Straddle, ingen DeltaType
                msg.SetField(new QF.Fields.NoRelatedSym(symbolList.Count));
                foreach (var symbol in symbolList)
                {
                    var symGroup = new QF.Group(146, 55);
                    symGroup.SetField(new QF.Fields.Symbol(symbol));              // 55
                    symGroup.SetField(new QF.Fields.SecurityType("FOR"));          // 167
                    symGroup.SetField(new QF.Fields.SecurityExchange("1000NYK"));  // 207
                    symGroup.SetField(new QF.Fields.StringField(6215, "ALL"));     // TenorValue
                    symGroup.SetField(new QF.Fields.StringField(9126, "2"));       // OptionStrategy - Straddle
                    msg.AddGroup(symGroup);
                }

                QF.Session.SendToTarget(msg, sessionId);

                _logger.LogInformation(
                    "[{Session}] 35=V probe (minimal test) sent for {Count} symbol(s): [{Symbols}]",
                    sessionKey, symbolList.Count, string.Join(", ", symbolList));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[{Session}] Failed to send 35=V probe: {Error}", sessionKey, ex.Message);
            }
        }

    }
}