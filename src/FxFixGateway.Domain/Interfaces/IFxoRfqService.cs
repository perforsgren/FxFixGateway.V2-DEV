using FxFixGateway.Domain.ValueObjects;

namespace FxFixGateway.Domain.Interfaces
{
    /// <summary>
    /// Handles FXOhub RFQ messages (QuoteRequest, QuoteRequestReject and the RFQ market depth) and keeps them in fxvol.
    /// Called from QuickFixApplication.FromApp after parsing, off the QuickFIX thread.
    /// </summary>
    public interface IFxoRfqService
    {
        Task HandleQuoteRequestAsync(string sessionKey, FxoQuoteRequestDto dto);

        Task HandleQuoteRequestRejectAsync(string sessionKey, FxoQuoteRequestRejectDto dto);

        Task HandleRfqDepthAsync(string sessionKey, FxoRfqDepthDto dto);
    }

    /// <summary>Persistence of FXOhub RFQs in fxvol (fxohub_rfq, fxohub_rfq_leg, fxohub_rfq_quote, fxohub_rfq_event).</summary>
    public interface IFxoRfqRepository
    {
        /// <summary>
        /// Stores the request and its legs. A request already stored (same session and QuoteReqID, e.g. a resend) keeps
        /// its status; its fields and legs are replaced. Returns the request's id.
        /// </summary>
        Task<long> UpsertQuoteRequestAsync(string sessionKey, FxoQuoteRequestDto dto, DateTime receivedUtc);

        /// <summary>Marks the request cancelled with the reason. Returns false when the request is not stored.</summary>
        Task<bool> MarkCancelledAsync(string sessionKey, string quoteReqId, string reason, DateTime receivedUtc);

        /// <summary>Replaces the request's depth with the quotes in the message (none when there is no market data).</summary>
        Task ReplaceDepthAsync(string sessionKey, FxoRfqDepthDto dto, DateTime receivedUtc);

        /// <summary>Appends an event (every RFQ message, for the audit trail and the hit ratio).</summary>
        Task InsertEventAsync(string sessionKey, string? quoteReqId, string msgType, string eventType, string? detail,
                              string rawPayload, DateTime receivedUtc);
    }
}
