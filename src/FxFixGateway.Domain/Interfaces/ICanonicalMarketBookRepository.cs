using FxFixGateway.Domain.Entities;

namespace FxFixGateway.Domain.Interfaces
{
    public interface ICanonicalMarketBookRepository
    {
        Task UpsertEntriesAsync(IReadOnlyList<CanonicalBookEntry> entries);
        Task DeactivateEntriesAsync(string venue, string sessionKey, string securityId);

        /// <summary>
        /// Ren upsert av enskilda entries från 35=X — soft-deletar INTE övriga entries
        /// för instrumentet (till skillnad från UpsertEntriesAsync).
        /// </summary>
        Task UpsertIncrementalEntriesAsync(IReadOnlyList<CanonicalBookEntry> entries);

        /// <summary>
        /// Soft-deletar EN entry (venue + security_id + sida + position_no) för 35=X Delete.
        /// </summary>
        Task DeactivateEntryAsync(string venue, string sessionKey, string securityId,
            string mdEntryType, int positionNo);

        Task<IReadOnlyList<CanonicalBookEntry>> GetBookAsync(
            string currencyPair,
            string? tenor = null,
            string? strategy = null,
            string? cut = null,
            bool activeOnly = true);

        /// <summary>
        /// Se motsvarande metod på IMarketDataSnapshotRepository — samma resonemang,
        /// men för canonical_market_book.
        /// </summary>
        Task DeactivateStaleOwnEntriesAsync(
            string venue, string sessionKey, string securityId, string mdEntryType,
            string originator, string? traderId, int keepPositionNo);
    }
}