using FxFixGateway.Domain.Entities;

namespace FxFixGateway.Domain.Interfaces
{
    /// <summary>
    /// Persisterar inkommande QuoteRequest (35=R) i fxvol.quote_requests.
    /// </summary>
    public interface IQuoteRequestRepository
    {
        /// <summary>Spara ett QuoteRequest och returnera det genererade ID:t.</summary>
        Task<long> InsertAsync(QuoteRequest request);

        /// <summary>The rows (one per leg) of <paramref name="sessionKey"/> received since <paramref name="sinceUtc"/>, oldest first, without raw_payload.</summary>
        Task<IReadOnlyList<QuoteRequest>> GetSinceAsync(string sessionKey, DateTime sinceUtc);
    }
}