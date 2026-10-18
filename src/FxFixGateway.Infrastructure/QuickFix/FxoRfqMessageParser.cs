using System.Globalization;
using FxFixGateway.Domain.ValueObjects;
using QF = global::QuickFix;

namespace FxFixGateway.Infrastructure.QuickFix
{
    /// <summary>
    /// Parses TP ICAP FXOhub RFQ messages (FXOhub FIX specification, RFQ section): QuoteRequest (R),
    /// QuoteRequestReject (AG) and the RFQ market depth (W with QuoteReqID and NoQuoteSets).
    ///
    /// The groups are read as FIX44_FXOhub.xml defines them (NoRelatedSym 146 / Symbol 55, NoLegs 555 / LegSymbol 600,
    /// NoPartyIDs 453 / PartyID 448, NoQuoteSets 296 / QuoteSetID 302, NoMDEntries 268 / MDEntryType 269), so the RFQ
    /// session must use that dictionary.
    /// </summary>
    public static class FxoRfqMessageParser
    {
        /// <summary>
        /// An FXOhub QuoteRequest carries the required top-level tags ExecDeltaHedge (9015) and PremiumDel (5475);
        /// Volbroker's does not (it identifies the instrument with SecurityID 48 instead).
        /// </summary>
        public static bool IsFxoQuoteRequest(QF.Message message)
            => message.IsSetField(9015) || message.IsSetField(5475);

        /// <summary>An RFQ market depth: a 35=W that names its QuoteRequest (131), unlike order book market data.</summary>
        public static bool IsFxoRfqDepth(QF.Message message)
            => message.IsSetField(131) && (message.IsSetField(296) || message.IsSetField(6003));

        public static FxoQuoteRequestDto ParseQuoteRequest(QF.Message message, string rawMessage)
        {
            string? symbol = null, securityType = null, optionStrategy = null, quoteRequestType = null,
                    quoteType = null, side = null, netDelta = null, netVega = null;
            DateTime? transactTime = null, validUntil = null, expireTime = null;
            var isOwn = false;
            var legs = new List<FxoQuoteRequestLegDto>();

            var noRelatedSym = GetInt(message, 146) ?? 0;
            if (noRelatedSym > 0)
            {
                var sym = new QF.Group(146, 55);
                message.GetGroup(1, sym);

                symbol           = Get(sym, 55);
                securityType     = Get(sym, 167);
                optionStrategy   = Get(sym, 9126);
                quoteRequestType = Get(sym, 303);
                quoteType        = Get(sym, 537);
                side             = Get(sym, 54);
                transactTime     = GetUtcTimestamp(sym, 60);
                validUntil       = GetUtcTimestamp(sym, 62);
                expireTime       = GetUtcTimestamp(sym, 126);
                netDelta         = Get(sym, 9083);
                netVega          = Get(sym, 9086);
                isOwn            = (GetInt(sym, 453) ?? 0) > 0;

                var noLegs = GetInt(sym, 555) ?? 0;
                for (var i = 1; i <= noLegs; i++)
                {
                    var leg = new QF.Group(555, 600);
                    sym.GetGroup(i, leg);
                    var tenorCode = Get(leg, 6215);
                    legs.Add(new FxoQuoteRequestLegDto
                    {
                        LegSymbol       = Get(leg, 600),
                        LegId           = Get(leg, 7940),
                        LegQtyMillions  = GetDecimal(leg, 687),
                        MaturityDate    = GetLocalDate(leg, 611),
                        StrikePrice     = GetDecimal(leg, 612),
                        LegSide         = Get(leg, 624),
                        TenorCode       = tenorCode,
                        Tenor           = TenorName(tenorCode),
                        SettlDate       = GetLocalDate(leg, 588),
                        LegType         = Get(leg, 6713),
                        OptionStyle     = Get(leg, 9019),
                        Cutoff          = Get(leg, 9125),
                        PitType         = Get(leg, 6007),
                        DeliveryDate    = GetLocalDate(leg, 743),
                        LegDelta        = GetDecimal(leg, 9075),
                        AtmType         = Get(leg, 6005),
                        LegCurrency     = Get(leg, 556),
                        Currency2       = Get(leg, 9074),
                        CallPutCurrency = Get(leg, 9034),
                        SettlCurrency   = Get(leg, 120)
                    });
                }
            }

            return new FxoQuoteRequestDto
            {
                QuoteReqId           = Get(message, 131),
                Text                 = Get(message, 58),
                BrokerId             = Get(message, 7933),
                TraderId             = Get(message, 9536),
                TradeDate            = Get(message, 75),
                ExecDeltaHedge       = Get(message, 9015),
                HedgeTradeType       = Get(message, 9016),
                PremiumDel           = Get(message, 5475),
                Symbol               = symbol,
                SecurityType         = securityType,
                OptionStrategy       = optionStrategy,
                StrategyName         = StrategyName(optionStrategy),
                QuoteRequestType     = quoteRequestType,
                QuoteType            = quoteType,
                Side                 = side,
                TransactTimeUtc      = transactTime,
                ValidUntilUtc        = validUntil,
                ExpireTimeUtc        = expireTime,
                NetDelta             = netDelta,
                NetVega              = netVega,
                IsOwnRequest         = isOwn,
                Legs                 = legs,
                ExtraRelatedSymCount = Math.Max(0, noRelatedSym - 1),
                RawPayload           = rawMessage
            };
        }

        public static FxoQuoteRequestRejectDto ParseQuoteRequestReject(QF.Message message, string rawMessage)
            => new()
            {
                QuoteReqId   = Get(message, 131),
                RejectReason = Get(message, 658),
                Text         = Get(message, 58),
                TraderId     = Get(message, 9536),
                RawPayload   = rawMessage
            };

        public static FxoRfqDepthDto ParseRfqDepth(QF.Message message, string rawMessage)
        {
            var noMarketData = Get(message, 6003) == "1";
            var quoteSets = new List<FxoRfqQuoteSetDto>();

            if (!noMarketData)
            {
                var noQuoteSets = GetInt(message, 296) ?? 0;
                for (var i = 1; i <= noQuoteSets; i++)
                {
                    var set = new QF.Group(296, 302);
                    message.GetGroup(i, set);

                    var entries = new List<FxoRfqQuoteEntryDto>();
                    var noEntries = GetInt(set, 268) ?? 0;
                    for (var j = 1; j <= noEntries; j++)
                    {
                        var entry = new QF.Group(268, 269);
                        set.GetGroup(j, entry);
                        entries.Add(new FxoRfqQuoteEntryDto
                        {
                            MdEntryType = Get(entry, 269),
                            LegId       = Get(entry, 7940),
                            Price       = GetDecimal(entry, 270),
                            Size        = GetDecimal(entry, 271),
                            PositionNo  = GetInt(entry, 290),
                            PxRelatedTo = Get(entry, 10502),
                            LegSide     = Get(entry, 624),
                            LegType     = Get(entry, 6713),
                            Text        = Get(entry, 58)
                        });
                    }

                    quoteSets.Add(new FxoRfqQuoteSetDto
                    {
                        QuoteSetId        = Get(set, 302),
                        QuoteId           = Get(set, 117),
                        BidStrategyVol    = GetDecimal(set, 6001),
                        OfferStrategyVol  = GetDecimal(set, 6002),
                        OwnQuote          = Get(set, 5331) == "Y",
                        OwnQuoteRequestor = Get(set, 6012) == "Y",
                        Position          = i,
                        Entries           = entries
                    });
                }
            }

            return new FxoRfqDepthDto
            {
                QuoteReqId      = Get(message, 131),
                MdReqId         = Get(message, 262),
                Symbol          = Get(message, 55),
                MdSource        = Get(message, 5177),
                NoMarketData    = noMarketData,
                LastTradedPrice = GetDecimal(message, 6004),
                TransactTimeUtc = GetUtcTimestamp(message, 60),
                QuoteSets       = quoteSets,
                RawPayload      = rawMessage
            };
        }

        // ── Codes ────────────────────────────────────────────────────────────

        /// <summary>OptionStrategy (9126) in the RFQ session.</summary>
        public static string? StrategyName(string? code) => code switch
        {
            "1" => "Single Leg",
            "2" => "Straddle",
            "3" => "Strangle",
            "4" => "Risk Reversal",
            "A" => "Straddle Calendar Spread",
            "B" => "Butterfly",
            "C" => "Currency Spread",
            "7" => "Generic Spread",
            _   => null
        };

        /// <summary>
        /// TenorValue (6215) in an RFQ leg is a numeric code. The specification lists 1–7 and 9–18; 8 is not listed, so
        /// it (and any other unknown code) maps to null and the raw code is kept.
        /// </summary>
        public static string? TenorName(string? code) => code switch
        {
            "1"  => "1D",
            "2"  => "1W",
            "3"  => "2W",
            "4"  => "1M",
            "5"  => "2M",
            "6"  => "3M",
            "7"  => "6M",
            "9"  => "2Y",
            "10" => "3Y",
            "11" => "4Y",
            "12" => "5Y",
            "13" => "7Y",
            "14" => "10Y",
            "15" => "15Y",
            "16" => "15M",
            "17" => "18M",
            "18" => "21M",
            _    => null
        };

        // ── Field helpers ────────────────────────────────────────────────────

        private static string? Get(QF.FieldMap map, int tag)
            => map.IsSetField(tag) ? map.GetString(tag) : null;

        private static int? GetInt(QF.FieldMap map, int tag)
            => int.TryParse(Get(map, tag), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;

        private static decimal? GetDecimal(QF.FieldMap map, int tag)
            => decimal.TryParse(Get(map, tag), NumberStyles.Number, CultureInfo.InvariantCulture, out var v) ? v : null;

        private static readonly string[] UtcTimestampFormats = { "yyyyMMdd-HH:mm:ss", "yyyyMMdd-HH:mm:ss.fff" };

        private static DateTime? GetUtcTimestamp(QF.FieldMap map, int tag)
            => DateTime.TryParseExact(Get(map, tag), UtcTimestampFormats, CultureInfo.InvariantCulture,
                   DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var v) ? v : null;

        private static DateTime? GetLocalDate(QF.FieldMap map, int tag)
            => DateTime.TryParseExact(Get(map, tag), "yyyyMMdd", CultureInfo.InvariantCulture,
                   DateTimeStyles.None, out var v) ? v : null;
    }
}
