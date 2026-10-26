using FxFixGateway.Application.BackgroundServices;
using FxFixGateway.Domain.Interfaces;
using FxFixGateway.Domain.ValueObjects;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FxFixGateway.Tests
{
    /// <summary>
    /// Volbroker keeps no RFQs over a logout: a request received before its session's latest logon is dropped there, so no
    /// Quote or Quote Cancel goes out on it and our live versions on it are expired (the request itself is left alone).
    /// </summary>
    public class VolbrokerReconnectTests
    {
        private static readonly DateTime Logon = new DateTime(2026, 10, 26, 8, 33, 1, 200, DateTimeKind.Utc);

        [Fact]
        public void Dropped_OnlyWhenReceivedAtLeastASecondBeforeTheLogon()
        {
            Assert.True(VolbrokerReconnect.IsDropped(new DateTime(2026, 10, 26, 8, 24, 31, DateTimeKind.Utc), Logon));
            Assert.True(VolbrokerReconnect.IsDropped(Logon.AddSeconds(-1), Logon));
            // received_utc in whole seconds: 08:33:01 may be 08:33:01.700, after the logon.
            Assert.False(VolbrokerReconnect.IsDropped(new DateTime(2026, 10, 26, 8, 33, 1, DateTimeKind.Utc), Logon));
            Assert.False(VolbrokerReconnect.IsDropped(Logon.AddMinutes(5), Logon));
            // Not logged on since the gateway started: nothing to compare with.
            Assert.False(VolbrokerReconnect.IsDropped(Logon.AddHours(-3), null));
        }

        [Fact]
        public void Reason_SaysWhenWeLoggedOnAgain()
        {
            Assert.Equal("Volbroker dropped the request when we logged on again at 08:33:01Z (it keeps no RFQs over a logout)",
                         VolbrokerReconnect.Reason(Logon));
        }

        [Fact]
        public async Task Service_RequestFromBeforeTheLogon_NothingSent_LiveVersionsExpired()
        {
            var before = Logon.AddMinutes(-9);
            var after = Logon.AddMinutes(2);
            var legs = new[] { new VolbrokerPriceLegToSend { LegNo = 1, LegSymbol = "EUR/SEK", BidVol = 7m, OfferVol = 7.5m } };
            var repo = new VolbrokerDayTests.FakeRepository
            {
                Live =
                {
                    new VolbrokerLiveVersion { PriceId = 1, Version = 1, Status = "ACCEPTED", SessionKey = "VOLB_FIXHUB_PROD", QuoteReqId = "Q1",
                                               RequestUtc = DateTime.UtcNow, LastReceivedUtc = before },
                    new VolbrokerLiveVersion { PriceId = 3, Version = 1, Status = "ACCEPTED", SessionKey = "VOLB_FIXHUB_PROD", QuoteReqId = "Q2",
                                               RequestUtc = DateTime.UtcNow, LastReceivedUtc = after }
                },
                Queued =
                {
                    // Queued before the logout, or on a request from before it: not sent.
                    new VolbrokerPriceToSend { PriceId = 4, Version = 1, SessionKey = "VOLB_FIXHUB_PROD", QuoteReqId = "Q3", RequestUtc = DateTime.UtcNow,
                                               LastReceivedUtc = before, Legs = legs },
                    // A request that came after the logon (also one Volbroker resent): sent as before.
                    new VolbrokerPriceToSend { PriceId = 6, Version = 1, SessionKey = "VOLB_FIXHUB_PROD", QuoteReqId = "Q5", RequestUtc = DateTime.UtcNow,
                                               LastReceivedUtc = after, Legs = legs }
                },
                Cancels = { new VolbrokerPriceToCancel { PriceId = 5, Version = 1, SessionKey = "VOLB_FIXHUB_PROD", QuoteReqId = "Q4", QuoteId = "VB5-1",
                                                         RequestUtc = DateTime.UtcNow, LastReceivedUtc = before } }
            };
            var sender = new VolbrokerDayTests.FakeSender();
            var service = new VolbrokerQuoteService(repo, sender, NullLogger<VolbrokerQuoteService>.Instance, new Logons("VOLB_FIXHUB_PROD", Logon));

            await service.RunOnceAsync();

            Assert.Equal(new[] { "VB6-1" }, sender.Quotes);
            Assert.Empty(sender.Cancels);
            Assert.Equal(new long[] { 1, 4, 5 }, repo.Cleared.Select(c => c.Id).OrderBy(x => x));
            Assert.Equal(VolbrokerReconnect.Reason(Logon), repo.Cleared.Single(c => c.Id == 1).Reason);
            Assert.Equal("not sent: " + VolbrokerReconnect.Reason(Logon), repo.Cleared.Single(c => c.Id == 4).Reason);
            Assert.Equal(3, repo.Events.Count(e => e == "DROPPED"));
        }

        [Fact]
        public async Task Service_NoLogonKnown_SentAsBefore()
        {
            var repo = new VolbrokerDayTests.FakeRepository
            {
                Queued = { new VolbrokerPriceToSend { PriceId = 7, Version = 1, SessionKey = "VOLB_FIXHUB_PROD", QuoteReqId = "Q7", RequestUtc = DateTime.UtcNow,
                                                      LastReceivedUtc = Logon.AddMinutes(-30),
                                                      Legs = new[] { new VolbrokerPriceLegToSend { LegNo = 1, LegSymbol = "EUR/SEK", BidVol = 7m, OfferVol = 7.5m } } } }
            };
            var sender = new VolbrokerDayTests.FakeSender();
            var service = new VolbrokerQuoteService(repo, sender, NullLogger<VolbrokerQuoteService>.Instance, new Logons("VOLB_FIXHUB_DEV", Logon));

            await service.RunOnceAsync();

            Assert.Equal(new[] { "VB7-1" }, sender.Quotes);
            Assert.Empty(repo.Cleared);
        }

        private sealed class Logons : ISessionLogonTimes
        {
            private readonly string _session;
            private readonly DateTime _utc;
            public Logons(string session, DateTime utc) { _session = session; _utc = utc; }
            public DateTime? LastLogonUtc(string sessionKey) => string.Equals(sessionKey, _session, StringComparison.OrdinalIgnoreCase) ? _utc : null;
        }
    }
}
