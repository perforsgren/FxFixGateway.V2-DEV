using FxFixGateway.Domain.ValueObjects;
using FxFixGateway.Infrastructure.QuickFix;
using Xunit;

namespace FxFixGateway.Tests
{
    public class VolbrokerQuoteMessagesTests
    {
        private static VolbrokerPriceToSend Price() => new()
        {
            PriceId = 43, RfqId = 1001, Version = 1, Trader = "P901PEF", TraderName = "FORSPE",
            ValidUntilUtc = new DateTime(2026, 10, 26, 13, 0, 0, DateTimeKind.Utc), SpotLow = 1.05686m, SpotHigh = 1.0611m,
            SizeMillions = 200m, SessionKey = "VOLB_FIXHUB_PROD", QuoteReqId = "1792671001245", SecurityId = "116245601",
            Legs = new[]
            {
                new VolbrokerPriceLegToSend { LegNo = 1, LegSymbol = "NOK/SEK", BidVol = 7.05m, OfferVol = 7.05m },
                new VolbrokerPriceLegToSend { LegNo = 2, LegSymbol = "NOK/SEK", BidVol = 6.8m, OfferVol = 7.325m },
            }
        };

        [Fact]
        public void The_quote_has_no_validity_period_and_no_spot_out_levels()
        {
            // Volbroker uses neither on RFQ quotes (22 Oct): our time and spot stipulations are our own cancels.
            var m = VolbrokerQuoteMessages.BuildQuote(Price(), "VB43-1", new DateTime(2026, 10, 26, 12, 0, 0, DateTimeKind.Utc));

            Assert.False(m.IsSetField(192));
            Assert.False(m.IsSetField(188));
            Assert.False(m.IsSetField(190));
            Assert.False(m.IsSetField(191));
        }

        [Fact]
        public void The_quote_carries_the_request_our_id_the_size_the_trader_and_a_vol_per_leg()
        {
            var m = VolbrokerQuoteMessages.BuildQuote(Price(), "VB43-1", new DateTime(2026, 10, 26, 12, 0, 0, DateTimeKind.Utc));
            var raw = m.ToString().Replace('\u0001', '|');

            Assert.Equal("S", m.Header.GetString(35));
            Assert.Equal("1792671001245", m.GetString(131));
            Assert.Equal("VB43-1", m.GetString(117));
            Assert.Equal("0", m.GetString(537));
            Assert.Equal("FORSPE", m.GetString(9536));
            Assert.Equal("116245601", m.GetString(48));
            Assert.Equal("200", m.GetString(134));
            Assert.Equal("200", m.GetString(135));
            Assert.Contains("555=2|600=NOK/SEK|681=7.05|684=7.05|600=NOK/SEK|681=6.8|684=7.325|", raw);
        }
    }
}
