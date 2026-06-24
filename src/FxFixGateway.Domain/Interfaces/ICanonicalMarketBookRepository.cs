using FxFixGateway.Domain.Entities;

namespace FxFixGateway.Domain.Interfaces
{
    public interface ICanonicalMarketBookRepository
    {
        Task UpsertEntriesAsync(IReadOnlyList<CanonicalBookEntry> entries);
        Task DeactivateEntriesAsync(string venue, string sessionKey, string securityId);
        Task<IReadOnlyList<CanonicalBookEntry>> GetBookAsync(
            string currencyPair,
            string? tenor = null,
            string? strategy = null,
            string? cut = null,
            bool activeOnly = true);
    }
}