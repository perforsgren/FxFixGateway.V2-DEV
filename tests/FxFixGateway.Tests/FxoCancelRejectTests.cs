using FxFixGateway.Application.BackgroundServices;
using FxFixGateway.Domain.Interfaces;
using FxFixGateway.Domain.ValueObjects;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FxFixGateway.Tests
{
    /// <summary>
    /// FXOhub's Business Message Reject of our Quote Cancel (UAT 4 Nov: "Quote already cancelled", 372=Z, 380=0, 379 empty, no
    /// 45): it closes the price whose cancel went out last; a reject of anything else leaves the quote on FXOhub.
    /// </summary>
    public class FxoCancelRejectTests
    {
        private const string Session = "FXOHUB_UAT";

        [Theory]
        [InlineData("Quote already cancelled", true)]
        [InlineData("QUOTE ALREADY CANCELLED", true)]
        [InlineData("Quote already canceled", true)]
        [InlineData("Unknown quote", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void Already_cancelled_is_read_from_the_text(string? text, bool alreadyOff)
            => Assert.Equal(alreadyOff, FxoCancelReject.IsAlreadyOff(text));

        [Fact]
        public async Task A_reject_of_our_cancel_without_a_QuoteID_closes_the_price_whose_cancel_went_out_last()
        {
            var repo = new FakeRepository();
            var service = new FxoQuoteService(repo, repo, new FakeSender(), NullLogger<FxoQuoteService>.Instance);

            await service.HandleBusinessRejectAsync(Session, new FxoBusinessRejectDto
            {
                RefMsgType = "Z", RefId = null, Reason = "0", Text = "Quote already cancelled",
                RawPayload = "8=FIX.4.4|35=j|58=Quote already cancelled|372=Z|379=|380=0|"
            });

            var call = Assert.Single(repo.CancelRejects);
            Assert.Equal(Session, call.Session);
            Assert.Null(call.QuoteId);
            Assert.True(call.AlreadyOff);
            Assert.Equal("FXOhub: Quote already cancelled", call.Reason);
            Assert.Equal(TimeSpan.FromSeconds(30), call.Window);
            Assert.Empty(repo.QuoteRejects);
            Assert.Contains("v1 now WITHDRAWN", Assert.Single(repo.Events).Detail);
        }

        [Fact]
        public async Task A_reject_of_our_cancel_for_another_reason_leaves_the_quote_on_FXOhub()
        {
            var repo = new FakeRepository();
            var service = new FxoQuoteService(repo, repo, new FakeSender(), NullLogger<FxoQuoteService>.Instance);

            await service.HandleBusinessRejectAsync(Session, new FxoBusinessRejectDto { RefMsgType = "Z", RefId = "OS42-1", Text = "Not allowed" });

            var call = Assert.Single(repo.CancelRejects);
            Assert.Equal("OS42-1", call.QuoteId);
            Assert.False(call.AlreadyOff);
            Assert.Equal("cancel rejected by FXOhub: Not allowed", call.Reason);
        }

        [Fact]
        public async Task No_single_cancel_to_match_changes_nothing_and_says_why()
        {
            var repo = new FakeRepository { Found = false };
            var service = new FxoQuoteService(repo, repo, new FakeSender(), NullLogger<FxoQuoteService>.Instance);

            await service.HandleBusinessRejectAsync(Session, new FxoBusinessRejectDto { RefMsgType = "Z", Text = "Quote already cancelled" });

            Assert.Contains("not exactly one cancel in the last 30 s", Assert.Single(repo.Events).Detail);
        }

        [Fact]
        public async Task A_reject_of_our_quote_is_still_a_rejected_price()
        {
            var repo = new FakeRepository();
            var service = new FxoQuoteService(repo, repo, new FakeSender(), NullLogger<FxoQuoteService>.Instance);

            await service.HandleBusinessRejectAsync(Session, new FxoBusinessRejectDto { RefMsgType = "S", RefId = "OS42-1", Text = "Bad leg" });

            Assert.Equal("OS42-1", Assert.Single(repo.QuoteRejects));
            Assert.Empty(repo.CancelRejects);
        }

        private sealed class FakeSender : IFxoQuoteSender
        {
            public bool IsLoggedOn(string sessionKey) => true;
            public string SendQuote(FxoPriceToSend price, string quoteId, DateTime nowUtc) => throw new NotSupportedException();
            public string SendQuoteCancel(FxoPriceToCancel cancel) => throw new NotSupportedException();
        }

        private sealed class FakeRepository : IFxoQuoteRepository, IFxoRfqRepository
        {
            public bool Found = true;
            public readonly List<(string Session, string? QuoteId, bool AlreadyOff, string Reason, TimeSpan Window)> CancelRejects = new();
            public readonly List<string> QuoteRejects = new();
            public readonly List<(string MsgType, string EventType, string? Detail)> Events = new();

            private static FxoPriceRef Price(string status) => new()
            {
                PriceId = 42, RfqId = 246, Version = 1, Status = status, SessionKey = Session, QuoteReqId = "<105753><FXO>2f87"
            };

            public Task<FxoPriceRef?> ApplyCancelRejectAsync(string sessionKey, string? quoteId, bool alreadyOff, string reason, TimeSpan window, DateTime nowUtc)
            {
                CancelRejects.Add((sessionKey, quoteId, alreadyOff, reason, window));
                return Task.FromResult(Found ? Price(alreadyOff ? "WITHDRAWN" : "ACCEPTED") : null);
            }

            public Task<FxoPriceRef?> ApplyBusinessRejectAsync(string quoteId, string reason, DateTime nowUtc)
            {
                QuoteRejects.Add(quoteId);
                return Task.FromResult<FxoPriceRef?>(Price("REJECTED"));
            }

            public Task InsertEventAsync(string sessionKey, string? quoteReqId, string msgType, string eventType, string? detail,
                                         string rawPayload, DateTime receivedUtc)
            {
                Events.Add((msgType, eventType, detail));
                return Task.CompletedTask;
            }

            public Task<int> ExpireAsync(DateTime nowUtc) => throw new NotSupportedException();
            public Task<int> RejectOrphanedAsync(DateTime nowUtc) => throw new NotSupportedException();
            public Task<IReadOnlyList<FxoPriceToSend>> GetQueuedAsync(DateTime nowUtc) => throw new NotSupportedException();
            public Task<bool> ClaimAsync(long priceId, DateTime nowUtc) => throw new NotSupportedException();
            public Task UnclaimAsync(long priceId, DateTime nowUtc) => throw new NotSupportedException();
            public Task MarkSentAsync(long priceId, string quoteId, DateTime sentUtc) => throw new NotSupportedException();
            public Task MarkRejectedAsync(long priceId, string reason, DateTime nowUtc) => throw new NotSupportedException();
            public Task<IReadOnlyList<FxoPriceToCancel>> GetWithdrawRequestsAsync() => throw new NotSupportedException();
            public Task MarkCancelSentAsync(long priceId, DateTime nowUtc) => throw new NotSupportedException();
            public Task<FxoPriceRef?> ApplyQuoteStatusAsync(string quoteId, string quoteStatus, string? text, DateTime nowUtc) => throw new NotSupportedException();
            public Task<long> UpsertQuoteRequestAsync(string sessionKey, FxoQuoteRequestDto dto, DateTime receivedUtc) => throw new NotSupportedException();
            public Task<bool> MarkCancelledAsync(string sessionKey, string quoteReqId, string reason, DateTime receivedUtc) => throw new NotSupportedException();
            public Task ReplaceDepthAsync(string sessionKey, FxoRfqDepthDto dto, DateTime receivedUtc) => throw new NotSupportedException();
        }
    }
}
