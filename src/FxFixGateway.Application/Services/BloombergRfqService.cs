using FxFixGateway.Domain.Interfaces;
using FxFixGateway.Domain.ValueObjects;
using Microsoft.Extensions.Logging;

namespace FxFixGateway.Application.Services
{
    /// <summary>
    /// Bloomberg FXGO Options RFQs (FX FIXBook for Options RFS 4.1.5), step 1: each QuoteRequest is kept in fxvol with its legs
    /// (status INCOMING) and then acknowledged with a Quote Status Report (35=AI, 297 = 0: accepted for pricing; Bloomberg's
    /// ticket goes from Requested to Acked). A request that could not be stored is not acknowledged: nobody here would see it.
    /// A Quote Cancel from Bloomberg (35=Z, 298 = 2: the client cancelled or rejected) marks the request CANCELLED. Every
    /// message, ours included, is an event. Quoting (35=S), passing (35=AG) and orders (35=D / 35=8) come in later steps.
    /// </summary>
    public sealed class BloombergRfqService : IBloombergRfqService
    {
        private readonly IBloombergRfqRepository _repository;
        private readonly IBloombergRfqSender _sender;
        private readonly ILogger<BloombergRfqService> _logger;

        public BloombergRfqService(IBloombergRfqRepository repository, IBloombergRfqSender sender, ILogger<BloombergRfqService> logger)
        {
            _repository = repository ?? throw new ArgumentNullException(nameof(repository));
            _sender     = sender     ?? throw new ArgumentNullException(nameof(sender));
            _logger     = logger     ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>The QuoteID (117) of our acknowledgement: the FIXBook requires one, and no quote exists yet.</summary>
        public static string AckQuoteId(long rfqId) => $"BBR{rfqId}";

        public async Task HandleQuoteRequestAsync(string sessionKey, BloombergQuoteRequestDto dto)
        {
            var now = DateTime.UtcNow;
            if (string.IsNullOrEmpty(dto.QuoteReqId))
            {
                _logger.LogWarning("[{Session}] Bloomberg 35=R without QuoteReqID (131) — not stored, not acknowledged", sessionKey);
                await TryInsertEventAsync(sessionKey, null, "R", "INVALID", "QuoteReqID (131) missing", dto.RawPayload, now);
                return;
            }

            BloombergRfqStored stored;
            try
            {
                stored = await _repository.UpsertQuoteRequestAsync(sessionKey, dto, now);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[{Session}] Bloomberg RFQ {QuoteReqId} could not be stored — not acknowledged", sessionKey, dto.QuoteReqId);
                await TryInsertEventAsync(sessionKey, dto.QuoteReqId, "R", "INVALID", "not stored: " + ex.Message, dto.RawPayload, now);
                return;
            }

            await TryInsertEventAsync(sessionKey, dto.QuoteReqId, "R", "INCOMING", Describe(dto), dto.RawPayload, now);
            _logger.LogInformation(
                "[{Session}] Bloomberg RFQ stored: Id={Id} QuoteReqID={QuoteReqId} {Symbol} {Strategy} Side={Side} Legs={Legs} Segment={Segment} Taker={Taker}",
                sessionKey, stored.Id, dto.QuoteReqId, dto.Symbol, dto.StrategyName ?? dto.OptionStrategy, dto.Side, dto.Legs.Count,
                dto.MarketSegmentId, dto.TakerDealCode);

            if (dto.Problems.Count > 0)
                _logger.LogWarning("[{Session}] Bloomberg RFQ {QuoteReqId}: values not stored: {Problems}",
                    sessionKey, dto.QuoteReqId, string.Join("; ", dto.Problems));
            if (dto.ExtraRelatedSymCount > 0 || dto.OtherPartyCount > 0)
                _logger.LogWarning("[{Session}] Bloomberg RFQ {QuoteReqId}: {Sym} NoRelatedSym entries beyond the first and {Parties} other parties are only in the raw message",
                    sessionKey, dto.QuoteReqId, dto.ExtraRelatedSymCount, dto.OtherPartyCount);

            if (stored.Status == "CANCELLED")
            {
                _logger.LogInformation("[{Session}] Bloomberg RFQ {QuoteReqId} is already cancelled — not acknowledged", sessionKey, dto.QuoteReqId);
                return;
            }
            if (stored.AckSentUtc.HasValue)
            {
                _logger.LogInformation("[{Session}] Bloomberg RFQ {QuoteReqId} resent — acknowledged at {Ack:HH:mm:ss.fff}Z, not again",
                    sessionKey, dto.QuoteReqId, stored.AckSentUtc.Value);
                return;
            }

            var quoteId = AckQuoteId(stored.Id);
            string raw;
            try
            {
                raw = _sender.SendQuoteRequestAck(sessionKey, dto.QuoteReqId, quoteId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[{Session}] Bloomberg RFQ {QuoteReqId}: acknowledgement (35=AI) not sent", sessionKey, dto.QuoteReqId);
                await TryInsertEventAsync(sessionKey, dto.QuoteReqId, "AI", "ACK_FAILED", "not sent: " + ex.Message, string.Empty, DateTime.UtcNow);
                return;
            }

            var sentUtc = DateTime.UtcNow;
            try
            {
                await _repository.MarkAckSentAsync(stored.Id, quoteId, sentUtc);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[{Session}] Bloomberg RFQ {QuoteReqId}: acknowledgement sent but not recorded", sessionKey, dto.QuoteReqId);
            }
            await TryInsertEventAsync(sessionKey, dto.QuoteReqId, "AI", "ACK_SENT", $"QuoteStatus 0 (accepted for pricing) · QuoteID={quoteId}", raw, sentUtc);
            _logger.LogInformation("[{Session}] Bloomberg RFQ {QuoteReqId} acknowledged (35=AI 297=0, QuoteID={QuoteId})", sessionKey, dto.QuoteReqId, quoteId);
        }

        public async Task HandleQuoteCancelAsync(string sessionKey, BloombergQuoteCancelDto dto)
        {
            var now = DateTime.UtcNow;
            if (string.IsNullOrEmpty(dto.QuoteReqId))
            {
                _logger.LogWarning("[{Session}] Bloomberg 35=Z without QuoteReqID (131) — ignored", sessionKey);
                await TryInsertEventAsync(sessionKey, null, "Z", "INVALID", "QuoteReqID (131) missing", dto.RawPayload, now);
                return;
            }

            // 298 = 2 is Bloomberg's: the request is cancelled (client cancelled, or rejected the quote). 1 is the LP's own.
            if (dto.QuoteCancelType != "2")
            {
                var detail = $"QuoteCancelType {dto.QuoteCancelType ?? "missing"} from Bloomberg (only 2 is theirs) — status unchanged";
                _logger.LogWarning("[{Session}] Bloomberg 35=Z for {QuoteReqId}: {Detail}", sessionKey, dto.QuoteReqId, detail);
                await TryInsertEventAsync(sessionKey, dto.QuoteReqId, "Z", "INVALID", detail, dto.RawPayload, now);
                return;
            }

            var reason = "Cancelled by Bloomberg" + (string.IsNullOrEmpty(dto.Text) ? "" : $": {dto.Text}");
            try
            {
                var found = await _repository.MarkCancelledAsync(sessionKey, dto.QuoteReqId, reason, now);
                await TryInsertEventAsync(sessionKey, dto.QuoteReqId, "Z", found ? "CANCELLED" : "CANCELLED_UNKNOWN",
                                          reason + (dto.QuoteId != null ? $" · QuoteID={dto.QuoteId}" : ""), dto.RawPayload, now);
                if (found)
                    _logger.LogInformation("[{Session}] Bloomberg RFQ {QuoteReqId} cancelled ({Reason})", sessionKey, dto.QuoteReqId, reason);
                else
                    _logger.LogWarning("[{Session}] Bloomberg 35=Z for unknown RFQ {QuoteReqId} ({Reason})", sessionKey, dto.QuoteReqId, reason);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[{Session}] Failed to cancel Bloomberg RFQ {QuoteReqId}", sessionKey, dto.QuoteReqId);
            }
        }

        public async Task HandleUnhandledAsync(string sessionKey, string msgType, string? quoteReqId, string rawPayload)
        {
            var detail = msgType == "D"
                ? "New Order Single: orders are not handled yet (step 1 only receives requests) — no Execution Report sent"
                : $"35={msgType} is not handled on Bloomberg sessions yet — nothing sent back";
            _logger.LogError("[{Session}] Bloomberg {Detail} (QuoteReqID {QuoteReqId})", sessionKey, detail, quoteReqId ?? "-");
            await TryInsertEventAsync(sessionKey, quoteReqId, msgType, "UNHANDLED", detail, rawPayload, DateTime.UtcNow);
        }

        /// <summary>"EURSEK Straddle side=0 legs=2 · BTFE · taker SWSA (Jane Sales) · in competition N · 1 problem(s)".</summary>
        private static string Describe(BloombergQuoteRequestDto d)
            => $"{d.Symbol} {d.StrategyName ?? d.OptionStrategy} side={d.Side} legs={d.Legs.Count}"
               + (d.MarketSegmentId != null ? $" · {d.MarketSegmentId}" : "")
               + (d.TakerDealCode != null ? $" · taker {d.TakerDealCode}" + (d.TakerTrader != null ? $" ({d.TakerTrader})" : "") : "")
               + (d.InCompetition != null ? $" · in competition {d.InCompetition}" : "")
               + (d.Problems.Count > 0 ? $" · {d.Problems.Count} value(s) not stored" : "");

        private async Task TryInsertEventAsync(string sessionKey, string? quoteReqId, string msgType, string eventType,
                                               string? detail, string rawPayload, DateTime receivedUtc)
        {
            try
            {
                await _repository.InsertEventAsync(sessionKey, quoteReqId, msgType, eventType, detail, rawPayload, receivedUtc);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[{Session}] Failed to store Bloomberg RFQ event {Event} for {QuoteReqId}", sessionKey, eventType, quoteReqId);
            }
        }
    }
}
