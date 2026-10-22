using System.Globalization;
using FxFixGateway.Domain.ValueObjects;
using QF = global::QuickFix;

namespace FxFixGateway.Infrastructure.QuickFix
{
    /// <summary>
    /// Parses Bloomberg FXGO Options messages and builds ours, per the FX FIXBook for Options RFS 4.1.5: Quote Request (35=R)
    /// and Quote Cancel (35=Z, 298 = 2 from Bloomberg) in, Quote Status Report (35=AI, 297 = 0) out.
    ///
    /// The groups are read as FIX44_Bloomberg.xml defines them (NoRelatedSym 146 / Symbol 55, NoPartyIDs 453 / PartyID 448
    /// with NoPartySubIDs 802 / PartySubID 523, NoLegs 555 / LegRefID 654), so the session must use that dictionary. A value
    /// that is not the FIXBook's type (a number, a YYYYMMDD date) is not stored: it is listed in the request's Problems.
    /// </summary>
    public static class BloombergRfqMessages
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public static BloombergQuoteRequestDto ParseQuoteRequest(QF.Message message, string rawMessage)
        {
            var r = new Reader();

            // NoRelatedSym (146): the instrument; the FIXBook sends one entry. Without the group, the fields at the top level.
            QF.FieldMap sym = message;
            var noRelatedSym = r.Count(message, 146);
            if (noRelatedSym > 0)
            {
                var g = new QF.Group(146, 55);
                message.GetGroup(1, g);
                sym = g;
            }

            var parties = ReadParties(message, r);

            var legs = new List<BloombergQuoteRequestLegDto>();
            var noLegs = r.Count(message, 555);
            for (var i = 1; i <= noLegs; i++)
            {
                var leg = new QF.Group(555, 654);
                message.GetGroup(i, leg);
                var n = $"leg {i} ";
                legs.Add(new BloombergQuoteRequestLegDto
                {
                    LegRefId              = Get(leg, 654),
                    LegSecurityId         = Get(leg, 602),
                    LegUpiCode            = Get(leg, 2893),
                    LegCurrency           = Get(leg, 556),
                    LegIsinProduct        = Get(leg, 22410),
                    CutOff                = Get(leg, 9124),
                    ExpiryDate            = r.Date(leg, 432, n + "ExpiryDate"),
                    DeliveryDate          = r.Date(leg, 743, n + "DeliveryDate"),
                    CallPutCurrency       = Get(leg, 9034),
                    CallOrPut             = Get(leg, 9072),
                    Delta                 = r.Dec(leg, 9075, n + "Delta"),
                    StrikePrice           = r.Dec(leg, 612, n + "LegStrikePrice"),
                    NotionalAmount        = r.Dec(leg, 9018, n + "LegNotionalAmount"),
                    OptionStyle           = Get(leg, 9019),
                    Position              = Get(leg, 6351),
                    ForwardRate           = r.Dec(leg, 9099, n + "ForwardRate"),
                    Ccy1DepositRate       = r.Dec(leg, 9115, n + "CCY1DepositRate"),
                    Ccy2DepositRate       = r.Dec(leg, 9118, n + "CCY2DepositRate"),
                    BarrierStyle          = Get(leg, 9130),
                    BarrierLevel          = r.Dec(leg, 9131, n + "BarrierLevel"),
                    BarrierLevel2         = r.Dec(leg, 9137, n + "BarrierLevel2"),
                    BarrierDirection      = Get(leg, 9132),
                    TouchType             = Get(leg, 9187),
                    LegHedgeAmount        = r.Dec(leg, 22562, n + "LegHedgeAmount"),
                    LegHedgeCurrency      = Get(leg, 22563),
                    LegExecDeltaHedge     = Get(leg, 22677),
                    LegDeltaHedgeSide     = Get(leg, 22678),
                    LegHedgeRate          = r.Dec(leg, 22679, n + "LegHedgeRate"),
                    LegHedgeTradeType     = Get(leg, 22680),
                    LegHedgeDate          = r.Date(leg, 22681, n + "LegHedgeDate"),
                    LegHedgeFixingDate    = r.Date(leg, 22861, n + "LegHedgeFixingDate"),
                    LegHedgeSettlMethod   = Get(leg, 22950),
                    LegHedgeSettlCurrency = Get(leg, 22968),
                    AllocationCount       = r.Count(leg, 670)
                });
            }

            var strategy = Get(message, 9126);
            return new BloombergQuoteRequestDto
            {
                SenderSubId          = Get(message.Header, 50),
                OnBehalfOfSubId      = Get(message.Header, 116),
                DeliverToCompId      = Get(message.Header, 128),
                QuoteReqId           = Get(message, 131),
                MarketSegmentId      = Get(message, 1300),
                QuoteType            = Get(message, 537),
                Symbol               = Get(sym, 55),
                SymbolSfx            = Get(sym, 65),
                SecurityId           = Get(sym, 48),
                SecurityIdSource     = Get(sym, 22),
                ExtraRelatedSymCount = Math.Max(0, noRelatedSym - 1),
                Account              = Get(message, 1),
                InCompetition        = Get(message, 6702),
                Text                 = Get(message, 58),
                TradeDate            = r.Date(message, 75, "TradeDate"),
                Side                 = Get(message, 54),
                OptionStrategy       = strategy,
                StrategyName         = StrategyName(strategy),
                VenueMic             = parties.VenueMic,
                VenueRole            = parties.VenueRole,
                VenueLei             = parties.VenueLei,
                TakerDealCode        = parties.TakerDealCode,
                TakerFirm            = parties.TakerFirm,
                TakerUuid            = parties.TakerUuid,
                TakerTrader          = parties.TakerTrader,
                TakerLei             = parties.TakerLei,
                MakerDealCode        = parties.MakerDealCode,
                MakerFirm            = parties.MakerFirm,
                MakerLei             = parties.MakerLei,
                OtherPartyCount      = parties.Other,
                Legs                 = legs,
                SpotRate             = r.Dec(message, 5235, "SpotRate"),
                PremiumCurrency      = Get(message, 5830),
                PremiumDeliveryDate  = r.Date(message, 5020, "PremiumDeliveryDate"),
                PremiumDeliveryType  = Get(message, 22195),
                ExecDeltaHedge       = Get(message, 9015),
                HedgeTradeType       = Get(message, 9016),
                HedgeDirection       = Get(message, 6666),
                HedgeSide            = Get(message, 9679),
                HedgeAmount          = r.Dec(message, 9123, "HedgeAmount"),
                HedgeRate            = r.Dec(message, 9657, "HedgeRate"),
                HedgeDate            = r.Date(message, 9112, "HedgeDate"),
                HedgeFixingDate      = r.Date(message, 541, "HedgeFixingDate"),
                HedgeSettlMethod     = Get(message, 22514),
                HedgeSettlCurrency   = Get(message, 22656),
                HedgeSecurityId      = Get(message, 22292),
                HedgeUpiCode         = Get(message, 22898),
                SettlType            = Get(message, 63),
                SettlCurrency        = Get(message, 120),
                MarketType           = Get(message, 9102),
                QuotePriceType       = Get(message, 692),
                SolveForType         = Get(message, 22720),
                SolveForLegRefId     = Get(message, 22723),
                UpiCode              = Get(message, 2891),
                AllocationCount      = r.Count(message, 78),
                Problems             = r.Problems,
                RawPayload           = rawMessage
            };
        }

        public static BloombergQuoteCancelDto ParseQuoteCancel(QF.Message message, string rawMessage)
            => new()
            {
                QuoteReqId      = Get(message, 131),
                QuoteId         = Get(message, 117),
                QuoteCancelType = Get(message, 298),
                Text            = Get(message, 58),
                RawPayload      = rawMessage
            };

        /// <summary>
        /// Quote Status Report (35=AI) acknowledging the request: QuoteReqID, QuoteID (required by the FIXBook; ours, no quote
        /// exists yet) and QuoteStatus 0 = accepted for pricing, a business-level acknowledgement (FIXBook 4.1.4).
        /// </summary>
        public static QF.Message BuildQuoteRequestAck(string quoteReqId, string quoteId)
        {
            var m = new QF.Message();
            m.Header.SetField(new QF.Fields.MsgType("AI"));
            m.SetField(new QF.Fields.StringField(131, quoteReqId));
            m.SetField(new QF.Fields.StringField(117, quoteId));
            m.SetField(new QF.Fields.StringField(297, "0"));
            return m;
        }

        /// <summary>OptionStrategy (9126), FIXBook section 4.0.</summary>
        public static string? StrategyName(string? code) => code switch
        {
            "1"  => "Vanilla",
            "2"  => "Straddle",
            "3"  => "Strangle",
            "4"  => "Risk Reversal",
            "5"  => "Call/Put Spread",
            "6"  => "Knock-in",
            "7"  => "Knock-out",
            "8"  => "One-touch",
            "9"  => "No-touch",
            "10" => "Double No-touch",
            "11" => "Calendar Spread",
            "12" => "Diagonal Spread",
            "13" => "Two Leg Custom Vanilla",
            "14" => "Seagull",
            "15" => "Butterfly (4 Legs)",
            "16" => "Butterfly Call/Puts (3 Legs)",
            "17" => "3 Leg Custom Vanilla",
            "18" => "4 Leg Custom Vanilla",
            "19" => "Digital",
            "20" => "Digital Knock-out",
            "21" => "Digital Knock-in",
            _    => null
        };

        // ── Parties ──────────────────────────────────────────────────────────

        private sealed class Parties
        {
            public string? VenueMic, VenueRole, VenueLei;
            public string? TakerDealCode, TakerFirm, TakerUuid, TakerTrader, TakerLei;
            public string? MakerDealCode, MakerFirm, MakerLei;
            public int Other;
            public bool HasVenue, HasTaker, HasMaker;
        }

        /// <summary>
        /// NoPartyIDs (453): the venue (PartyIDSource G, PartyRole 16 / 64 / 73), the liquidity taker (PartyRole 13) and the
        /// liquidity maker (PartyRole 1), each with its PartySubIDs by PartySubIDType (1 firm name, 2 person = UUID, 9
        /// contact name, 4025 LEI). A second entry of a kind, or another role, is counted in Other.
        /// </summary>
        private static Parties ReadParties(QF.Message message, Reader r)
        {
            var p = new Parties();
            var n = r.Count(message, 453);
            for (var i = 1; i <= n; i++)
            {
                var g = new QF.Group(453, 448);
                message.GetGroup(i, g);
                var id = Get(g, 448);
                var source = Get(g, 447);
                var role = Get(g, 452);
                var subs = new Dictionary<string, string>(StringComparer.Ordinal);
                var noSubs = r.Count(g, 802);
                for (var j = 1; j <= noSubs; j++)
                {
                    var s = new QF.Group(802, 523);
                    g.GetGroup(j, s);
                    var type = Get(s, 803);
                    var value = Get(s, 523);
                    if (type != null && value != null && !subs.ContainsKey(type)) subs[type] = value;
                }
                string? Sub(string type) => subs.TryGetValue(type, out var v) ? v : null;

                if (!p.HasVenue && (source == "G" || role is "16" or "64" or "73"))
                {
                    p.HasVenue = true;
                    p.VenueMic = id; p.VenueRole = role; p.VenueLei = Sub("4025");
                }
                else if (!p.HasTaker && role == "13")
                {
                    p.HasTaker = true;
                    p.TakerDealCode = id; p.TakerFirm = Sub("1"); p.TakerUuid = Sub("2"); p.TakerTrader = Sub("9"); p.TakerLei = Sub("4025");
                }
                else if (!p.HasMaker && role == "1")
                {
                    p.HasMaker = true;
                    p.MakerDealCode = id; p.MakerFirm = Sub("1"); p.MakerLei = Sub("4025");
                }
                else p.Other++;
            }
            return p;
        }

        // ── Field reading ────────────────────────────────────────────────────

        private static string? Get(QF.FieldMap map, int tag)
            => map.IsSetField(tag) ? map.GetString(tag) : null;

        /// <summary>Reads typed values and lists the ones that are not the FIXBook's type.</summary>
        private sealed class Reader
        {
            private readonly List<string> _problems = new();
            public IReadOnlyList<string> Problems => _problems;

            public decimal? Dec(QF.FieldMap map, int tag, string name)
            {
                var s = Get(map, tag);
                if (s == null) return null;
                if (decimal.TryParse(s, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, Inv, out var v)) return v;
                _problems.Add($"{tag} {name} '{s}' is not a number");
                return null;
            }

            public DateTime? Date(QF.FieldMap map, int tag, string name)
            {
                var s = Get(map, tag);
                if (s == null) return null;
                if (DateTime.TryParseExact(s, "yyyyMMdd", Inv, DateTimeStyles.None, out var v)) return v;
                _problems.Add($"{tag} {name} '{s}' is not a YYYYMMDD date");
                return null;
            }

            /// <summary>A group's count (0 when absent).</summary>
            public int Count(QF.FieldMap map, int tag)
            {
                var s = Get(map, tag);
                if (s == null) return 0;
                if (int.TryParse(s, NumberStyles.None, Inv, out var v)) return v;
                _problems.Add($"{tag} '{s}' is not a group count");
                return 0;
            }
        }
    }

    /// <summary>Sends our messages on a Bloomberg session (looked up by session key when sending).</summary>
    public sealed class BloombergRfqSender : FxFixGateway.Domain.Interfaces.IBloombergRfqSender
    {
        private readonly Func<string, QF.SessionID?> _sessionId;

        /// <summary><paramref name="sessionId"/> resolves a session key to its QuickFIX session (null when unknown).</summary>
        public BloombergRfqSender(Func<string, QF.SessionID?> sessionId)
        {
            _sessionId = sessionId ?? throw new ArgumentNullException(nameof(sessionId));
        }

        public string SendQuoteRequestAck(string sessionKey, string quoteReqId, string quoteId)
            => Send(sessionKey, BloombergRfqMessages.BuildQuoteRequestAck(quoteReqId, quoteId));

        private string Send(string sessionKey, QF.Message message)
        {
            var id = _sessionId(sessionKey) ?? throw new InvalidOperationException($"No FIX session '{sessionKey}'.");
            if (!QF.Session.SendToTarget(message, id))
                throw new InvalidOperationException($"Session '{sessionKey}' did not send the message (not logged on?).");
            return message.ToString();
        }
    }
}
