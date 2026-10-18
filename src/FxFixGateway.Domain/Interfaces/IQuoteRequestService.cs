using FxFixGateway.Domain.ValueObjects;

namespace FxFixGateway.Domain.Interfaces
{
    /// <summary>
    /// Hanterar inkommande QuoteRequest (35=R).
    /// Sparar requesten och aktiverar prenumerationen för SecurityId
    /// så att efterföljande 35=W-meddelanden inte filtreras bort.
    /// </summary>
    public interface IQuoteRequestService
    {
        /// <summary>
        /// Anropas från QuickFixApplication.FromApp när MsgType=R tas emot.
        /// </summary>
        Task HandleQuoteRequestAsync(string sessionKey, QuoteRequestDto dto);

        /// <summary>
        /// Subscribes again the instruments of requests received since <paramref name="sinceUtc"/> that are not subscribed in
        /// the database (storing or marking them failed when the request came, or the gateway restarted since); requests of
        /// the last two minutes are left to their own handling. Returns how many were subscribed now.
        /// </summary>
        Task<int> ResubscribeAsync(DateTime sinceUtc);
    }
}