using FxFixGateway.Application.Services;
using FxFixGateway.Domain.Interfaces;
using FxFixGateway.Domain.ValueObjects;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FxFixGateway.Tests
{
    public class BloombergRfqServiceTests
    {
        private sealed class FakeRepository : IBloombergRfqRepository
        {
            public BloombergRfqStored Stored = new(17, "INCOMING", null);
            public bool FailStore;
            public bool CancelFound = true;
            public readonly List<BloombergQuoteRequestDto> Upserts = new();
            public readonly List<(long Id, string QuoteId)> Acks = new();
            public readonly List<(string QuoteReqId, string Reason)> Cancels = new();
            public readonly List<(string? QuoteReqId, string MsgType, string EventType, string? Detail, string Raw)> Events = new();

            public Task<BloombergRfqStored> UpsertQuoteRequestAsync(string sessionKey, BloombergQuoteRequestDto dto, DateTime receivedUtc)
            {
                if (FailStore) throw new InvalidOperationException("table missing");
                Upserts.Add(dto);
                return Task.FromResult(Stored);
            }

            public Task MarkAckSentAsync(long rfqId, string quoteId, DateTime sentUtc)
            {
                Acks.Add((rfqId, quoteId));
                return Task.CompletedTask;
            }

            public Task<bool> MarkCancelledAsync(string sessionKey, string quoteReqId, string reason, DateTime receivedUtc)
            {
                Cancels.Add((quoteReqId, reason));
                return Task.FromResult(CancelFound);
            }

            public Task InsertEventAsync(string sessionKey, string? quoteReqId, string msgType, string eventType, string? detail,
                                         string rawPayload, DateTime receivedUtc)
            {
                Events.Add((quoteReqId, msgType, eventType, detail, rawPayload));
                return Task.CompletedTask;
            }
        }

        private sealed class FakeSender : IBloombergRfqSender
        {
            public bool Fail;
            public readonly List<(string Session, string QuoteReqId, string QuoteId)> Sent = new();

            public string SendQuoteRequestAck(string sessionKey, string quoteReqId, string quoteId)
            {
                if (Fail) throw new InvalidOperationException("not logged on");
                Sent.Add((sessionKey, quoteReqId, quoteId));
                return $"35=AI|131={quoteReqId}|117={quoteId}|297=0|";
            }
        }

        private readonly FakeRepository _repo = new();
        private readonly FakeSender _sender = new();
        private BloombergRfqService Service => new(_repo, _sender, NullLogger<BloombergRfqService>.Instance);

        private static BloombergQuoteRequestDto Request(string? quoteReqId = "RFQ-1") => new()
        {
            QuoteReqId = quoteReqId, Symbol = "EURSEK", OptionStrategy = "2", StrategyName = "Straddle", Side = "0",
            MarketSegmentId = "BTFE", TakerDealCode = "SWSA", TakerTrader = "Jane Sales", InCompetition = "N",
            Legs = new[] { new BloombergQuoteRequestLegDto { LegRefId = "L1" }, new BloombergQuoteRequestLegDto { LegRefId = "L2" } },
            RawPayload = "35=R|131=RFQ-1|"
        };

        [Fact]
        public async Task A_request_is_stored_then_acknowledged()
        {
            await Service.HandleQuoteRequestAsync("BBG_UAT", Request());

            Assert.Single(_repo.Upserts);
            Assert.Equal(new[] { ("BBG_UAT", "RFQ-1", "BBR17") }, _sender.Sent);
            Assert.Equal(new[] { (17L, "BBR17") }, _repo.Acks);
            Assert.Equal(new[] { "INCOMING", "ACK_SENT" }, _repo.Events.Select(e => e.EventType));
            Assert.Equal("EURSEK Straddle side=0 legs=2 · BTFE · taker SWSA (Jane Sales) · in competition N", _repo.Events[0].Detail);
            Assert.Equal("35=AI|131=RFQ-1|117=BBR17|297=0|", _repo.Events[1].Raw);
        }

        [Fact]
        public async Task A_request_that_could_not_be_stored_is_not_acknowledged()
        {
            _repo.FailStore = true;

            await Service.HandleQuoteRequestAsync("BBG_UAT", Request());

            Assert.Empty(_sender.Sent);
            var e = Assert.Single(_repo.Events);
            Assert.Equal("INVALID", e.EventType);
            Assert.Equal("not stored: table missing", e.Detail);
        }

        [Fact]
        public async Task A_request_without_QuoteReqID_is_neither_stored_nor_acknowledged()
        {
            await Service.HandleQuoteRequestAsync("BBG_UAT", Request(quoteReqId: null));

            Assert.Empty(_repo.Upserts);
            Assert.Empty(_sender.Sent);
            Assert.Equal("INVALID", Assert.Single(_repo.Events).EventType);
        }

        [Fact]
        public async Task A_resend_of_an_acknowledged_request_is_not_acknowledged_again()
        {
            _repo.Stored = new BloombergRfqStored(17, "INCOMING", new DateTime(2026, 10, 22, 12, 0, 0));

            await Service.HandleQuoteRequestAsync("BBG_UAT", Request());

            Assert.Single(_repo.Upserts);
            Assert.Empty(_sender.Sent);
            Assert.Equal("INCOMING", Assert.Single(_repo.Events).EventType);
        }

        [Fact]
        public async Task A_resend_of_a_cancelled_request_is_not_acknowledged()
        {
            _repo.Stored = new BloombergRfqStored(17, "CANCELLED", null);

            await Service.HandleQuoteRequestAsync("BBG_UAT", Request());

            Assert.Empty(_sender.Sent);
        }

        [Fact]
        public async Task An_acknowledgement_that_could_not_be_sent_is_an_event_and_not_recorded_as_sent()
        {
            _sender.Fail = true;

            await Service.HandleQuoteRequestAsync("BBG_UAT", Request());

            Assert.Empty(_repo.Acks);
            Assert.Equal(new[] { "INCOMING", "ACK_FAILED" }, _repo.Events.Select(e => e.EventType));
            Assert.Equal("not sent: not logged on", _repo.Events[1].Detail);
        }

        [Fact]
        public async Task Bloomberg_quote_request_cancel_marks_the_request_cancelled()
        {
            await Service.HandleQuoteCancelAsync("BBG_UAT", new BloombergQuoteCancelDto
            {
                QuoteReqId = "RFQ-1", QuoteId = "*", QuoteCancelType = "2", Text = "Client cancelled", RawPayload = "35=Z|"
            });

            Assert.Equal(new[] { ("RFQ-1", "Cancelled by Bloomberg: Client cancelled") }, _repo.Cancels);
            var e = Assert.Single(_repo.Events);
            Assert.Equal("CANCELLED", e.EventType);
            Assert.Equal("Cancelled by Bloomberg: Client cancelled · QuoteID=*", e.Detail);
        }

        [Fact]
        public async Task A_cancel_for_an_unknown_request_is_an_event()
        {
            _repo.CancelFound = false;

            await Service.HandleQuoteCancelAsync("BBG_UAT", new BloombergQuoteCancelDto { QuoteReqId = "RFQ-9", QuoteCancelType = "2" });

            Assert.Equal("CANCELLED_UNKNOWN", Assert.Single(_repo.Events).EventType);
        }

        [Fact]
        public async Task A_cancel_with_another_type_changes_nothing()
        {
            await Service.HandleQuoteCancelAsync("BBG_UAT", new BloombergQuoteCancelDto { QuoteReqId = "RFQ-1", QuoteCancelType = "1" });

            Assert.Empty(_repo.Cancels);
            var e = Assert.Single(_repo.Events);
            Assert.Equal("INVALID", e.EventType);
            Assert.Equal("QuoteCancelType 1 from Bloomberg (only 2 is theirs) — status unchanged", e.Detail);
        }

        [Fact]
        public async Task An_order_is_reported_as_not_handled()
        {
            await Service.HandleUnhandledAsync("BBG_UAT", "D", "RFQ-1", "35=D|");

            var e = Assert.Single(_repo.Events);
            Assert.Equal(("RFQ-1", "D", "UNHANDLED"), (e.QuoteReqId, e.MsgType, e.EventType));
            Assert.Equal("New Order Single: orders are not handled yet (step 1 only receives requests) — no Execution Report sent", e.Detail);
        }
    }
}
