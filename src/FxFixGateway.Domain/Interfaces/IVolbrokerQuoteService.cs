using FxFixGateway.Domain.ValueObjects;

namespace FxFixGateway.Domain.Interfaces
{
    /// <summary>
    /// Our answers to Volbroker RFQs (Volbroker FIX Hub API, 22 Aug 2024, sections 3.8–3.11): sends the prices the pricer
    /// queues as Quotes (35=S), withdraws them with Quote Cancel (35=Z), and applies Volbroker's Quote Responses (35=AJ),
    /// Quote Status Reports (35=AI) and Business Message Rejects (35=j). Only on the sessions it handles; the handlers are
    /// called from QuickFixApplication.FromApp, off the QuickFIX thread.
    /// </summary>
    public interface IVolbrokerQuoteService
    {
        /// <summary>True for a session our Volbroker quotes are sent on (its AJ, AI and j are this service's).</summary>
        bool HandlesSession(string sessionKey);

        Task HandleQuoteResponseAsync(string sessionKey, VolbrokerQuoteResponseDto dto);

        Task HandleQuoteStatusReportAsync(string sessionKey, FxoQuoteStatusReportDto dto);

        Task HandleBusinessRejectAsync(string sessionKey, FxoBusinessRejectDto dto);
    }

    /// <summary>Sends Volbroker Quote and Quote Cancel messages on a FIX session.</summary>
    public interface IVolbrokerQuoteSender
    {
        /// <summary>True when the session exists and is logged on.</summary>
        bool IsLoggedOn(string sessionKey);

        /// <summary>Sends the Quote (35=S) with <paramref name="quoteId"/>; returns the message as sent.</summary>
        string SendQuote(VolbrokerPriceToSend price, string quoteId, DateTime nowUtc);

        /// <summary>Sends the Quote Cancel (35=Z) for one quote; returns the message as sent.</summary>
        string SendQuoteCancel(VolbrokerPriceToCancel cancel);
    }

    /// <summary>
    /// Our Volbroker price versions in fxvol.volbroker_rfq_price (script OptionSuite.Pricer.Wpf/Docs/Sql/
    /// 20261022_volbroker_rfq_send.sql in OptionSuite.V2) and the messages about them in fxvol.volbroker_rfq_event.
    /// </summary>
    public interface IVolbrokerQuoteRepository
    {
        /// <summary>Versions whose time stipulation has passed: never sent → EXPIRED, sent → WITHDRAW (time ran out). Returns how many.</summary>
        Task<int> ExpireAsync(DateTime nowUtc);

        /// <summary>Queued versions on a request no longer the trader's (released, passed): REJECTED. Returns how many.</summary>
        Task<int> RejectOrphanedAsync(DateTime nowUtc);

        /// <summary>Queued versions still valid, oldest first, with the request's fields and the legs.</summary>
        Task<IReadOnlyList<VolbrokerPriceToSend>> GetQueuedAsync(DateTime nowUtc);

        /// <summary>Takes a queued version for sending (QUEUED → SENDING). False when it is no longer queued (withdrawn).</summary>
        Task<bool> ClaimAsync(long priceId, DateTime nowUtc);

        /// <summary>Gives a taken version back (SENDING → QUEUED), e.g. the session went down.</summary>
        Task UnclaimAsync(long priceId, DateTime nowUtc);

        /// <summary>The Quote is sent: quote_id and sent_utc; SENDING → SENT (a WITHDRAW asked meanwhile stays WITHDRAW).</summary>
        Task MarkSentAsync(long priceId, string quoteId, DateTime sentUtc);

        /// <summary>A version that could not be sent, or that Volbroker refused: REJECTED with the reason.</summary>
        Task MarkRejectedAsync(long priceId, string reason, DateTime nowUtc);

        /// <summary>Versions to withdraw whose Quote Cancel is not sent yet (and that have a QuoteID).</summary>
        Task<IReadOnlyList<VolbrokerPriceToCancel>> GetWithdrawRequestsAsync();

        /// <summary>The Quote Cancel is sent.</summary>
        Task MarkCancelSentAsync(long priceId, DateTime nowUtc);

        /// <summary>
        /// Applies a Quote Response to the version with <paramref name="quoteId"/> (else the one with
        /// <paramref name="quoteRespId"/>): 10 → ACCEPTED (older live versions → REPLACED), 1 → DONE, 3 → EXPIRED (WITHDRAWN when
        /// we were withdrawing it), 11 / 12 → WITHDRAW (the side that went in is withdrawn), 6 / 13 / 14 / 15 / 99 → REJECTED.
        /// Null when no version is found.
        /// </summary>
        Task<VolbrokerPriceRef?> ApplyQuoteResponseAsync(string? quoteId, string? quoteRespId, string quoteRespType, string reason, DateTime nowUtc);

        /// <summary>
        /// Applies a Quote Status Report (our Quote Cancel's answer) to the version with <paramref name="quoteId"/>: 0 / 9 →
        /// WITHDRAWN (EXPIRED when its time ran out), 5 / 99 → back to ACCEPTED with the reason (the price is still on
        /// Volbroker; the trader can withdraw again). Null when unknown.
        /// </summary>
        Task<VolbrokerPriceRef?> ApplyQuoteStatusAsync(string quoteId, string quoteStatus, string? text, DateTime nowUtc);

        /// <summary>A Business Message Reject of our Quote (→ REJECTED) or Quote Cancel (→ back to ACCEPTED). Null when unknown.</summary>
        Task<VolbrokerPriceRef?> ApplyBusinessRejectAsync(string quoteId, string refMsgType, string reason, DateTime nowUtc);

        /// <summary>Stores one message about our quotes in fxvol.volbroker_rfq_event.</summary>
        Task InsertEventAsync(string sessionKey, string? quoteReqId, string msgType, string eventType, string? detail, string rawPayload, DateTime nowUtc);
    }
}
