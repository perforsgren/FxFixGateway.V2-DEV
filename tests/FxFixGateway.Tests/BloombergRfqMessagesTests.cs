using System.Xml.Linq;
using FxFixGateway.Infrastructure.QuickFix;
using Xunit;

namespace FxFixGateway.Tests
{
    /// <summary>
    /// Bloomberg messages built from the FX FIXBook for Options RFS 4.1.5 tables, parsed with FIX44_Bloomberg.xml as a session
    /// does, then read by <see cref="BloombergRfqMessages"/>.
    /// </summary>
    public class BloombergRfqMessagesTests
    {
        private const string Parties =
            "453=3|"
            + "448=BTFE|447=G|452=64|802=1|523=254900QBKK4WBSO3GE51|803=4025|"
            + "448=SWSA|447=D|452=13|802=5|523=Swedbank AB|803=1|523=12345678|803=2|523=Jane Sales|803=9|523=Y|803=4047|523=M312CIUE8CH3MH7RRK28|803=4025|"
            + "448=SWTR|447=D|452=1|802=3|523=Swedbank AB|803=1|523=M312CIUE8CH3MH7RRK28|803=4025|523=Y|803=4046|";

        // EUR/SEK 1M straddle, two-way, delta hedged at spot, on Bloomberg's MTF (BTFE).
        private static readonly string Straddle = Fix.Raw("R",
            "131=RFQ-STRADDLE-1|1300=BTFE|537=1|146=1|55=EURSEK|65=FX|48=EURSEK|22=6|1=ACC42|" + Parties
            + "6702=N|75=20261022|54=0|9126=2|555=2|"
            + "654=L1|556=EUR|22410=Vanilla_Option|9124=NYK|432=20261124|743=20261126|9034=EUR|9072=1|9075=51.2|612=11.05|9018=10000000|9019=2|6351=0|"
            + "654=L2|556=EUR|22410=Vanilla_Option|9124=NYK|432=20261124|743=20261126|9034=EUR|9072=2|9075=48.8|612=11.05|9018=10000000|9019=2|6351=0|"
            + "5235=11.0412|5830=EUR|5020=20261026|9015=Y|9016=1|6666=0|9679=2|9123=240000|9657=11.0412|9112=20261026|22195=1|9102=R|692=1|",
            extraHeader: "50=12345678|116=SWSA|128=SWTR|");

        [Fact]
        public void Straddle_reads_instrument_parties_legs_and_hedge()
        {
            var dto = BloombergRfqMessages.ParseQuoteRequest(Fix.Parse(Straddle), "raw");

            Assert.Equal("RFQ-STRADDLE-1", dto.QuoteReqId);
            Assert.Equal("BTFE", dto.MarketSegmentId);
            Assert.Equal("1", dto.QuoteType);
            Assert.Equal("EURSEK", dto.Symbol);
            Assert.Equal("FX", dto.SymbolSfx);
            Assert.Equal("EURSEK", dto.SecurityId);
            Assert.Equal("6", dto.SecurityIdSource);
            Assert.Equal(0, dto.ExtraRelatedSymCount);
            Assert.Equal("ACC42", dto.Account);
            Assert.Equal("N", dto.InCompetition);
            Assert.Equal(new DateTime(2026, 10, 22), dto.TradeDate);
            Assert.Equal("0", dto.Side);
            Assert.Equal("2", dto.OptionStrategy);
            Assert.Equal("Straddle", dto.StrategyName);

            Assert.Equal("12345678", dto.SenderSubId);
            Assert.Equal("SWSA", dto.OnBehalfOfSubId);
            Assert.Equal("SWTR", dto.DeliverToCompId);

            Assert.Equal("BTFE", dto.VenueMic);
            Assert.Equal("64", dto.VenueRole);
            Assert.Equal("254900QBKK4WBSO3GE51", dto.VenueLei);
            Assert.Equal("SWSA", dto.TakerDealCode);
            Assert.Equal("Swedbank AB", dto.TakerFirm);
            Assert.Equal("12345678", dto.TakerUuid);
            Assert.Equal("Jane Sales", dto.TakerTrader);
            Assert.Equal("M312CIUE8CH3MH7RRK28", dto.TakerLei);
            Assert.Equal("SWTR", dto.MakerDealCode);
            Assert.Equal("Swedbank AB", dto.MakerFirm);
            Assert.Equal("M312CIUE8CH3MH7RRK28", dto.MakerLei);
            Assert.Equal(0, dto.OtherPartyCount);

            Assert.Equal(2, dto.Legs.Count);
            var call = dto.Legs[0];
            Assert.Equal("L1", call.LegRefId);
            Assert.Equal("EUR", call.LegCurrency);
            Assert.Equal("Vanilla_Option", call.LegIsinProduct);
            Assert.Equal("NYK", call.CutOff);
            Assert.Equal(new DateTime(2026, 11, 24), call.ExpiryDate);
            Assert.Equal(new DateTime(2026, 11, 26), call.DeliveryDate);
            Assert.Equal("EUR", call.CallPutCurrency);
            Assert.Equal("1", call.CallOrPut);
            Assert.Equal(51.2m, call.Delta);
            Assert.Equal(11.05m, call.StrikePrice);
            Assert.Equal(10000000m, call.NotionalAmount);
            Assert.Equal("2", call.OptionStyle);
            Assert.Equal("0", call.Position);
            Assert.Equal("2", dto.Legs[1].CallOrPut);
            Assert.Equal(48.8m, dto.Legs[1].Delta);

            Assert.Equal(11.0412m, dto.SpotRate);
            Assert.Equal("EUR", dto.PremiumCurrency);
            Assert.Equal(new DateTime(2026, 10, 26), dto.PremiumDeliveryDate);
            Assert.Equal("1", dto.PremiumDeliveryType);
            Assert.Equal("Y", dto.ExecDeltaHedge);
            Assert.Equal("1", dto.HedgeTradeType);
            Assert.Equal("0", dto.HedgeDirection);
            Assert.Equal("2", dto.HedgeSide);
            Assert.Equal(240000m, dto.HedgeAmount);
            Assert.Equal(11.0412m, dto.HedgeRate);
            Assert.Equal(new DateTime(2026, 10, 26), dto.HedgeDate);
            Assert.Equal("R", dto.MarketType);
            Assert.Equal("1", dto.QuotePriceType);
            Assert.Null(dto.SolveForType);
            Assert.Empty(dto.Problems);
            Assert.Equal("raw", dto.RawPayload);
        }

        [Fact]
        public void Calendar_reads_two_expiries_their_positions_and_the_forward_rate()
        {
            var raw = Fix.Raw("R",
                "131=RFQ-CAL-1|1300=XOFF|537=1|146=1|55=USDSEK|65=FX|48=USDSEK|22=6|453=1|448=XOFF|447=G|452=16|54=1|9126=11|555=2|"
                + "654=A|556=USD|9124=NYK|432=20261124|743=20261127|9034=USD|9072=1|612=10.6|9018=5000000|9019=2|6351=0|"
                + "654=B|556=USD|9124=NYK|432=20270122|743=20270126|9034=USD|9072=1|612=10.6|9018=5000000|9019=2|6351=1|9099=10.55|"
                + "5235=10.62|5830=USD|5020=20261026|9015=N|22195=1|9102=R|692=2|");

            var dto = BloombergRfqMessages.ParseQuoteRequest(Fix.Parse(raw), raw);

            Assert.Equal("Calendar Spread", dto.StrategyName);
            Assert.Equal("XOFF", dto.VenueMic);
            Assert.Equal("16", dto.VenueRole);
            Assert.Null(dto.TakerDealCode);
            Assert.Equal(2, dto.Legs.Count);
            Assert.Equal(new DateTime(2026, 11, 24), dto.Legs[0].ExpiryDate);
            Assert.Equal(new DateTime(2027, 1, 22), dto.Legs[1].ExpiryDate);
            Assert.Equal("0", dto.Legs[0].Position);
            Assert.Equal("1", dto.Legs[1].Position);
            Assert.Null(dto.Legs[0].ForwardRate);
            Assert.Equal(10.55m, dto.Legs[1].ForwardRate);
            Assert.Equal(10.62m, dto.SpotRate);
            Assert.Equal("N", dto.ExecDeltaHedge);
            Assert.Null(dto.HedgeAmount);
            Assert.Empty(dto.Problems);
        }

        [Fact]
        public void Solve_for_strike_has_no_strike_on_the_solved_leg()
        {
            var raw = Fix.Raw("R",
                "131=RFQ-RR-1|1300=BTFE|537=1|146=1|55=EURUSD|65=FX|48=EURUSD|22=6|54=1|9126=4|555=2|"
                + "654=P|556=EUR|9124=NYK|432=20261222|743=20261224|9034=EUR|9072=2|612=1.12|9018=20000000|9019=2|6351=1|"
                + "654=C|556=EUR|9124=NYK|432=20261222|743=20261224|9034=EUR|9072=1|9018=20000000|9019=2|6351=0|"
                + "5235=1.165|5830=USD|5020=20261026|9015=Y|9016=1|9123=0|9657=1.165|9112=20261026|22195=1|9102=R|692=2|22720=14|22723=C|");

            var dto = BloombergRfqMessages.ParseQuoteRequest(Fix.Parse(raw), raw);

            Assert.Equal("Risk Reversal", dto.StrategyName);
            Assert.Equal(1.12m, dto.Legs[0].StrikePrice);
            Assert.Null(dto.Legs[1].StrikePrice);
            Assert.Equal("14", dto.SolveForType);
            Assert.Equal("C", dto.SolveForLegRefId);
            Assert.Equal(0m, dto.HedgeAmount);
            Assert.Empty(dto.Problems);
        }

        [Fact]
        public void Leg_allocations_with_nested_parties_do_not_break_the_legs()
        {
            var raw = Fix.Raw("R",
                "131=RFQ-ALLOC-1|1300=BTFE|537=1|146=1|55=EURSEK|65=FX|48=EURSEK|22=6|54=1|9126=2|555=2|"
                + "654=L1|556=EUR|9072=1|612=11.05|9018=10000000|6351=0|"
                + "670=2|671=SUB1|672=A1|756=1|757=549300ABCDEFGHIJ0123|758=N|759=24|673=6000000|671=SUB2|672=A2|673=4000000|"
                + "654=L2|556=EUR|9072=2|612=11.05|9018=10000000|6351=0|"
                + "5235=11.04|5830=EUR|78=1|79=SUB1|22559=100000|");

            var dto = BloombergRfqMessages.ParseQuoteRequest(Fix.Parse(raw), raw);

            Assert.Equal(2, dto.Legs.Count);
            Assert.Equal(2, dto.Legs[0].AllocationCount);
            Assert.Equal("L2", dto.Legs[1].LegRefId);
            Assert.Equal("2", dto.Legs[1].CallOrPut);
            Assert.Equal(0, dto.Legs[1].AllocationCount);
            Assert.Equal(11.04m, dto.SpotRate);
            Assert.Equal("EUR", dto.PremiumCurrency);
            Assert.Equal(1, dto.AllocationCount);
            Assert.Empty(dto.Problems);
        }

        [Fact]
        public void A_value_that_is_not_the_FIXBook_type_is_listed_and_not_stored()
        {
            var raw = Fix.Raw("R",
                "131=RFQ-BAD-1|537=1|146=1|55=EURSEK|65=FX|48=EURSEK|22=6|54=1|9126=1|555=1|"
                + "654=L1|556=EUR|432=2026-11-24|9072=1|612=11.05|9018=10000000|6351=0|"
                + "9015=Y|9123=abc|");

            var dto = BloombergRfqMessages.ParseQuoteRequest(Fix.Parse(raw), raw);

            Assert.Null(dto.HedgeAmount);
            Assert.Null(dto.Legs[0].ExpiryDate);
            Assert.Equal(11.05m, dto.Legs[0].StrikePrice);
            Assert.Equal(
                new[] { "432 leg 1 ExpiryDate '2026-11-24' is not a YYYYMMDD date", "9123 HedgeAmount 'abc' is not a number" },
                dto.Problems);
        }

        [Fact]
        public void An_unknown_party_role_is_counted_not_stored()
        {
            var raw = Fix.Raw("R",
                "131=RFQ-P-1|537=1|146=1|55=EURSEK|65=FX|48=EURSEK|22=6|"
                + "453=2|448=SWSA|447=D|452=13|448=OTHER|447=D|452=99|54=1|9126=1|");

            var dto = BloombergRfqMessages.ParseQuoteRequest(Fix.Parse(raw), raw);

            Assert.Equal("SWSA", dto.TakerDealCode);
            Assert.Equal(1, dto.OtherPartyCount);
        }

        [Fact]
        public void Bloomberg_request_carries_9015_so_the_FXOhub_check_would_take_it()
        {
            // Why Bloomberg sessions are routed before the other venues' cases in QuickFixApplication.FromApp.
            Assert.True(FxoRfqMessageParser.IsFxoQuoteRequest(Fix.Parse(Straddle)));
        }

        [Fact]
        public void Quote_cancel_from_Bloomberg_is_read()
        {
            var raw = Fix.Raw("Z", "131=RFQ-STRADDLE-1|117=*|58=Client cancelled|298=2|");

            var dto = BloombergRfqMessages.ParseQuoteCancel(Fix.Parse(raw), raw);

            Assert.Equal("RFQ-STRADDLE-1", dto.QuoteReqId);
            Assert.Equal("*", dto.QuoteId);
            Assert.Equal("2", dto.QuoteCancelType);
            Assert.Equal("Client cancelled", dto.Text);
        }

        [Fact]
        public void Acknowledgement_is_a_quote_status_report_with_status_0()
        {
            var m = BloombergRfqMessages.BuildQuoteRequestAck("RFQ-STRADDLE-1", "BBR17");

            Assert.Equal("AI", m.Header.GetString(35));
            Assert.Equal("RFQ-STRADDLE-1", m.GetString(131));
            Assert.Equal("BBR17", m.GetString(117));
            Assert.Equal("0", m.GetString(297));
        }

        [Fact]
        public void Dictionary_defines_the_FIXBook_messages_without_value_lists_on_their_fields()
        {
            var doc = XDocument.Load(Fix.BloombergDictionaryPath);
            var fields = doc.Root!.Element("fields")!.Elements("field").ToDictionary(f => (string)f.Attribute("name")!);
            var messages = doc.Root.Element("messages")!.Elements("message").ToDictionary(m => (string)m.Attribute("msgtype")!);

            foreach (var msgType in new[] { "R", "AG", "S", "Z", "AI", "D", "8", "BN" })
            {
                Assert.True(messages.ContainsKey(msgType), $"35={msgType} missing");
                foreach (var name in messages[msgType].Descendants().Where(e => e.Name == "field" || e.Name == "group")
                                                      .Select(e => (string)e.Attribute("name")!))
                {
                    Assert.True(fields.ContainsKey(name), $"35={msgType}: field {name} not defined");
                    Assert.False(fields[name].Elements("value").Any(), $"35={msgType}: {name} has a value list");
                }
            }

            // Each group starts with the field the FIXBook puts first (QuickFIX/n's delimiter).
            var r = messages["R"];
            string First(XElement m, string group) => (string)m.Descendants("group").First(g => (string)g.Attribute("name")! == group)
                                                               .Elements().First().Attribute("name")!;
            Assert.Equal("Symbol", First(r, "NoRelatedSym"));
            Assert.Equal("PartyID", First(r, "NoPartyIDs"));
            Assert.Equal("PartySubID", First(r, "NoPartySubIDs"));
            Assert.Equal("LegRefID", First(r, "NoLegs"));
            Assert.Equal("LegAllocAccount", First(r, "NoLegAllocs"));
            Assert.Equal("Nested2PartyID", First(r, "NoNested2PartyIDs"));
            Assert.Equal("AllocAccount", First(r, "NoAllocs"));
            Assert.Equal("RegulatoryTradeID", First(messages["BN"], "NoRegulatoryTradeIDs"));
        }
    }
}
