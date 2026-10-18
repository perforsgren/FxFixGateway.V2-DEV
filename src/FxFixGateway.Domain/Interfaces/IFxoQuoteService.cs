using FxFixGateway.Domain.ValueObjects;

namespace FxFixGateway.Domain.Interfaces
{
    /// <summary>
    /// Our answers to FXOhub RFQs: sends the prices the pricer queues as Quotes (35=S), withdraws them with Quote Cancel
    /// (35=Z), and applies FXOhub's Quote Status Reports (35=AI) and Business Message Rejects (35=j). The two handlers are
    /// called from QuickFixApplication.FromApp, off the QuickFIX thread.
    /// </summary>
    public interface IFxoQuoteService
    {
        Task HandleQuoteStatusReportAsync(string sessionKey, FxoQuoteStatusReportDto dto);

        Task HandleBusinessRejectAsync(string sessionKey, FxoBusinessRejectDto dto);
    }

    /// <summary>Sends FXOhub Quote and Quote Cancel messages on a FIX session.</summary>
    public interface IFxoQuoteSender
    {
        /// <summary>True when the session exists and is logged on.</summary>
        bool IsLoggedOn(string sessionKey);

        /// <summary>Sends the Quote (35=S) with <paramref name="quoteId"/>; returns the message as sent.</summary>
        string SendQuote(FxoPriceToSend price, string quoteId, DateTime nowUtc);

        /// <summary>Sends the Quote Cancel (35=Z) for one quote; returns the message as sent.</summary>
        string SendQuoteCancel(FxoPriceToCancel cancel);
    }

    /// <summary>Our price versions in fxvol.fxohub_rfq_price (script docs/sql/20261018_fxohub_rfq_price.sql).</summary>
    public interface IFxoQuoteRepository
    {
        /// <summary>Live versions whose time stipulation has passed: EXPIRED. Returns how many.</summary>
        Task<int> ExpireAsync(DateTime nowUtc);

        /// <summary>Queued versions on a request no longer the trader's (released, passed, cancelled, traded): REJECTED. Returns how many.</summary>
        Task<int> RejectOrphanedAsync(DateTime nowUtc);

        /// <summary>Queued versions still valid, oldest first, with the request's fields and legs.</summary>
        Task<IReadOnlyList<FxoPriceToSend>> GetQueuedAsync(DateTime nowUtc);

        /// <summary>Takes a queued version for sending (QUEUED → SENDING). False when it is no longer queued (withdrawn).</summary>
        Task<bool> ClaimAsync(long priceId, DateTime nowUtc);

        /// <summary>Gives a taken version back (SENDING → QUEUED), e.g. the session went down.</summary>
        Task UnclaimAsync(long priceId, DateTime nowUtc);

        /// <summary>The Quote is sent: quote_id and sent_utc; SENDING → SENT (a WITHDRAW asked meanwhile stays WITHDRAW).</summary>
        Task MarkSentAsync(long priceId, string quoteId, DateTime sentUtc);

        /// <summary>A version that could not be sent: REJECTED with the reason.</summary>
        Task MarkRejectedAsync(long priceId, string reason, DateTime nowUtc);

        /// <summary>Versions the pricer asked to withdraw whose Quote Cancel is not sent yet (and that have a QuoteID).</summary>
        Task<IReadOnlyList<FxoPriceToCancel>> GetWithdrawRequestsAsync();

        /// <summary>The Quote Cancel is sent.</summary>
        Task MarkCancelSentAsync(long priceId, DateTime nowUtc);

        /// <summary>
        /// Applies a Quote Status Report to the version with <paramref name="quoteId"/>: 0 → ACCEPTED (older live versions of
        /// the request → REPLACED), 3 → WITHDRAWN, 5 / 9 → REJECTED, 7 → EXPIRED, 10 → no change. Null when no version has
        /// that QuoteID.
        /// </summary>
        Task<FxoPriceRef?> ApplyQuoteStatusAsync(string quoteId, string quoteStatus, string? text, DateTime nowUtc);

        /// <summary>A Business Message Reject of our Quote: the version with <paramref name="quoteId"/> → REJECTED. Null when unknown.</summary>
        Task<FxoPriceRef?> ApplyBusinessRejectAsync(string quoteId, string reason, DateTime nowUtc);
    }
}
