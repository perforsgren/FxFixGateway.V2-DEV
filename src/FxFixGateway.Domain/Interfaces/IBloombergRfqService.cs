using FxFixGateway.Domain.ValueObjects;

namespace FxFixGateway.Domain.Interfaces
{
    /// <summary>
    /// Handles the messages on Bloomberg FXGO Options RFQ sessions (VenueCode BLOOMBERG): keeps each QuoteRequest in fxvol and
    /// acknowledges it (Quote Status Report 297 = 0), and closes a request Bloomberg cancels (Quote Cancel 298 = 2). Called
    /// from QuickFixApplication.FromApp after parsing, off the QuickFIX thread.
    /// </summary>
    public interface IBloombergRfqService
    {
        Task HandleQuoteRequestAsync(string sessionKey, BloombergQuoteRequestDto dto);

        Task HandleQuoteCancelAsync(string sessionKey, BloombergQuoteCancelDto dto);

        /// <summary>A message the gateway does not handle on a Bloomberg session yet (e.g. a New Order Single): an event, an error in the log.</summary>
        Task HandleUnhandledAsync(string sessionKey, string msgType, string? quoteReqId, string rawPayload);
    }

    /// <summary>Sends our messages on a Bloomberg session.</summary>
    public interface IBloombergRfqSender
    {
        /// <summary>
        /// Sends the Quote Status Report (35=AI, 297 = 0) that acknowledges the request: "accepted for pricing", Bloomberg's
        /// ticket goes from Requested to Acked. Returns the message as sent.
        /// </summary>
        string SendQuoteRequestAck(string sessionKey, string quoteReqId, string quoteId);
    }

    /// <summary>Bloomberg requests in fxvol (bloomberg_rfq, bloomberg_rfq_leg, bloomberg_rfq_event; docs/sql/20261022_bloomberg_rfq.sql).</summary>
    public interface IBloombergRfqRepository
    {
        /// <summary>
        /// Stores the request and its legs. A request already stored (same session and QuoteReqID, e.g. a resend) keeps its
        /// status and acknowledgement; its fields and legs are replaced.
        /// </summary>
        Task<BloombergRfqStored> UpsertQuoteRequestAsync(string sessionKey, BloombergQuoteRequestDto dto, DateTime receivedUtc);

        /// <summary>The request is acknowledged with <paramref name="quoteId"/>.</summary>
        Task MarkAckSentAsync(long rfqId, string quoteId, DateTime sentUtc);

        /// <summary>Marks the request cancelled with the reason. Returns false when the request is not stored.</summary>
        Task<bool> MarkCancelledAsync(string sessionKey, string quoteReqId, string reason, DateTime receivedUtc);

        /// <summary>Appends an event (every message on the session, ours included).</summary>
        Task InsertEventAsync(string sessionKey, string? quoteReqId, string msgType, string eventType, string? detail,
                              string rawPayload, DateTime receivedUtc);
    }
}
