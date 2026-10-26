using FxFixGateway.Application.BackgroundServices;
using FxFixGateway.Domain.Interfaces;
using FxFixGateway.Domain.ValueObjects;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FxFixGateway.Tests
{
    /// <summary>Volbroker's day ends at 17:00 New York: the end per request, the request's time, and what the quote service does after it.</summary>
    public class VolbrokerDayTests
    {
        private static DateTime Utc(int y, int m, int d, int h, int min, int s = 0) => new(y, m, d, h, min, s, DateTimeKind.Utc);

        [Fact]
        public void EndUtc_17NewYork_InEveryClockCombination()
        {
            // Both on summer time (Wed 21 Oct): 17:00 EDT = 21:00 UTC = 23:00 Stockholm.
            Assert.Equal(Utc(2026, 10, 21, 21, 0), VolbrokerDay.EndUtc(Utc(2026, 10, 21, 12, 0)));
            // Europe on winter time, the US still on summer time (Mon 26 Oct): 21:00 UTC = 22:00 Stockholm.
            Assert.Equal(Utc(2026, 10, 26, 21, 0), VolbrokerDay.EndUtc(Utc(2026, 10, 26, 8, 30)));
            // Both on winter time (Mon 2 Nov): 17:00 EST = 22:00 UTC = 23:00 Stockholm.
            Assert.Equal(Utc(2026, 11, 2, 22, 0), VolbrokerDay.EndUtc(Utc(2026, 11, 2, 9, 0)));
            // The US on summer time from 8 Mar, Europe from 29 Mar (Mon 9 Mar): 21:00 UTC = 22:00 Stockholm.
            Assert.Equal(Utc(2026, 3, 9, 21, 0), VolbrokerDay.EndUtc(Utc(2026, 3, 9, 10, 0)));
        }

        [Fact]
        public void EndUtc_AtOrAfter17_IsTheNextDay()
        {
            Assert.Equal(Utc(2026, 10, 26, 21, 0), VolbrokerDay.EndUtc(Utc(2026, 10, 26, 20, 59, 59)));
            Assert.Equal(Utc(2026, 10, 27, 21, 0), VolbrokerDay.EndUtc(Utc(2026, 10, 26, 21, 0, 0)));
            Assert.Equal(Utc(2026, 10, 27, 21, 0), VolbrokerDay.EndUtc(Utc(2026, 10, 26, 23, 30)));
            // Sun 1 Nov: the US goes to winter time at 02:00; a request on Sat 31 Oct after 17:00 ends Sun 1 Nov 17:00 EST.
            Assert.Equal(Utc(2026, 11, 1, 22, 0), VolbrokerDay.EndUtc(Utc(2026, 10, 31, 21, 30)));
        }

        [Fact]
        public void IsCleared_FromTheEnd()
        {
            var request = Utc(2026, 10, 26, 14, 0);
            Assert.False(VolbrokerDay.IsCleared(request, Utc(2026, 10, 26, 20, 59, 59)));
            Assert.True(VolbrokerDay.IsCleared(request, Utc(2026, 10, 26, 21, 0, 0)));
        }

        [Fact]
        public void RequestUtc_SendingTime_ElseReceived()
        {
            var received = Utc(2026, 10, 26, 14, 35, 0);
            Assert.Equal(new DateTime(2026, 10, 26, 14, 32, 5, 123, DateTimeKind.Utc),
                         VolbrokerDay.RequestUtc("8=FIX.4.4\u00019=200\u000135=R\u000152=20261026-14:32:05.123\u000156=SWED\u0001", received));
            Assert.Equal(Utc(2026, 10, 26, 14, 32, 5), VolbrokerDay.RequestUtc("8=FIX.4.4\u000152=20261026-14:32:05\u0001", received));
            Assert.Equal(received, VolbrokerDay.RequestUtc("8=FIX.4.4\u000135=R\u0001", received));
            Assert.Equal(received, VolbrokerDay.RequestUtc("8=FIX.4.4\u000152=yesterday\u0001", received));
            Assert.Equal(received, VolbrokerDay.RequestUtc(null, received));
            Assert.Equal(DateTimeKind.Utc, VolbrokerDay.RequestUtc(null, DateTime.SpecifyKind(received, DateTimeKind.Unspecified)).Kind);
        }

        [Fact]
        public async Task Service_ClearedRequest_NothingSent_VersionsExpired()
        {
            var yesterday = DateTime.UtcNow.AddDays(-2);
            var repo = new FakeRepository
            {
                Live =
                {
                    new VolbrokerLiveVersion { PriceId = 1, Version = 1, Status = "ACCEPTED", SessionKey = "VOLB_FIXHUB_PROD", QuoteReqId = "Q1", RequestUtc = yesterday },
                    new VolbrokerLiveVersion { PriceId = 2, Version = 2, Status = "QUEUED", SessionKey = "VOLB_FIXHUB_PROD", QuoteReqId = "Q1", RequestUtc = yesterday },
                    new VolbrokerLiveVersion { PriceId = 3, Version = 1, Status = "ACCEPTED", SessionKey = "VOLB_FIXHUB_PROD", QuoteReqId = "Q2", RequestUtc = DateTime.UtcNow }
                },
                // Queued between the clear round and the send (a race): not sent either.
                Queued = { new VolbrokerPriceToSend { PriceId = 4, Version = 1, SessionKey = "VOLB_FIXHUB_PROD", QuoteReqId = "Q3", RequestUtc = yesterday,
                                                      Legs = new[] { new VolbrokerPriceLegToSend { LegNo = 1, LegSymbol = "EUR/SEK", BidVol = 7m, OfferVol = 7.5m } } } },
                Cancels = { new VolbrokerPriceToCancel { PriceId = 5, Version = 1, SessionKey = "VOLB_FIXHUB_PROD", QuoteReqId = "Q4", QuoteId = "VB5-1", RequestUtc = yesterday } }
            };
            var sender = new FakeSender();
            var service = new VolbrokerQuoteService(repo, sender, NullLogger<VolbrokerQuoteService>.Instance);

            await service.RunOnceAsync();

            Assert.Empty(sender.Quotes);
            Assert.Empty(sender.Cancels);
            Assert.Equal(new long[] { 1, 2, 4, 5 }, repo.Cleared.Select(c => c.Id).OrderBy(x => x));
            Assert.StartsWith("not sent: ", repo.Cleared.Single(c => c.Id == 2).Reason);
            Assert.Equal(VolbrokerDay.ClearedReason, repo.Cleared.Single(c => c.Id == 1).Reason);
            Assert.Equal(4, repo.Events.Count(e => e == "CLEARED"));
        }

        [Fact]
        public async Task Service_RequestOfToday_SentAsBefore()
        {
            var repo = new FakeRepository
            {
                Queued = { new VolbrokerPriceToSend { PriceId = 7, Version = 1, SessionKey = "VOLB_FIXHUB_PROD", QuoteReqId = "Q7", RequestUtc = DateTime.UtcNow,
                                                      Legs = new[] { new VolbrokerPriceLegToSend { LegNo = 1, LegSymbol = "EUR/SEK", BidVol = 7m, OfferVol = 7.5m } } } }
            };
            var sender = new FakeSender();
            var service = new VolbrokerQuoteService(repo, sender, NullLogger<VolbrokerQuoteService>.Instance);

            await service.RunOnceAsync();

            Assert.Equal(new[] { "VB7-1" }, sender.Quotes);
            Assert.Empty(repo.Cleared);
        }

        private sealed class FakeSender : IVolbrokerQuoteSender
        {
            public List<string> Quotes { get; } = new();
            public List<string> Cancels { get; } = new();
            public bool IsLoggedOn(string sessionKey) => true;
            public string SendQuote(VolbrokerPriceToSend price, string quoteId, DateTime nowUtc) { Quotes.Add(quoteId); return "35=S"; }
            public string SendQuoteCancel(VolbrokerPriceToCancel cancel) { Cancels.Add(cancel.QuoteId); return "35=Z"; }
        }

        private sealed class FakeRepository : IVolbrokerQuoteRepository
        {
            public List<VolbrokerLiveVersion> Live { get; } = new();
            public List<VolbrokerPriceToSend> Queued { get; } = new();
            public List<VolbrokerPriceToCancel> Cancels { get; } = new();
            public List<(long Id, string Reason)> Cleared { get; } = new();
            public List<string> Events { get; } = new();

            public Task<int> ExpireAsync(DateTime nowUtc) => Task.FromResult(0);
            public Task<int> RejectOrphanedAsync(DateTime nowUtc) => Task.FromResult(0);
            public Task<IReadOnlyList<VolbrokerLiveVersion>> GetLiveVersionsAsync() => Task.FromResult<IReadOnlyList<VolbrokerLiveVersion>>(Live);
            public Task<bool> MarkClearedAsync(long priceId, string reason, DateTime nowUtc)
            {
                if (Cleared.Any(c => c.Id == priceId)) return Task.FromResult(false);
                Cleared.Add((priceId, reason));
                return Task.FromResult(true);
            }
            public Task<IReadOnlyList<VolbrokerPriceToSend>> GetQueuedAsync(DateTime nowUtc)
                => Task.FromResult<IReadOnlyList<VolbrokerPriceToSend>>(Queued.Where(q => Cleared.All(c => c.Id != q.PriceId)).ToList());
            public Task<bool> ClaimAsync(long priceId, DateTime nowUtc) => Task.FromResult(true);
            public Task UnclaimAsync(long priceId, DateTime nowUtc) => Task.CompletedTask;
            public Task MarkSentAsync(long priceId, string quoteId, DateTime sentUtc) => Task.CompletedTask;
            public Task MarkRejectedAsync(long priceId, string reason, DateTime nowUtc) => Task.CompletedTask;
            public Task<IReadOnlyList<VolbrokerPriceToCancel>> GetWithdrawRequestsAsync()
                => Task.FromResult<IReadOnlyList<VolbrokerPriceToCancel>>(Cancels.Where(c => Cleared.All(x => x.Id != c.PriceId)).ToList());
            public Task MarkCancelSentAsync(long priceId, DateTime nowUtc) => Task.CompletedTask;
            public Task<VolbrokerPriceRef?> ApplyQuoteResponseAsync(string? quoteId, string? quoteRespId, string quoteRespType, string reason, DateTime nowUtc) => Task.FromResult<VolbrokerPriceRef?>(null);
            public Task<VolbrokerPriceRef?> ApplyQuoteStatusAsync(string quoteId, string quoteStatus, string? text, DateTime nowUtc) => Task.FromResult<VolbrokerPriceRef?>(null);
            public Task<VolbrokerPriceRef?> ApplyBusinessRejectAsync(string quoteId, string refMsgType, string reason, DateTime nowUtc) => Task.FromResult<VolbrokerPriceRef?>(null);
            public Task InsertEventAsync(string sessionKey, string? quoteReqId, string msgType, string eventType, string? detail, string rawPayload, DateTime nowUtc)
            {
                Events.Add(eventType);
                return Task.CompletedTask;
            }
        }
    }
}
