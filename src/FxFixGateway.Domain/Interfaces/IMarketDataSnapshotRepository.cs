using FxFixGateway.Domain.Entities;

namespace FxFixGateway.Domain.Interfaces
{
    /// <summary>
    /// Persisterar MarketDataSnapshot (35=W) och dess MDEntries
    /// i fxvol.market_data_snapshots + fxvol.market_data_entries.
    /// </summary>
    public interface IMarketDataSnapshotRepository
    {
        /// <summary>
        /// Kontrollera om ett SecurityID är prenumererat (is_subscribed=true).
        /// Används för att filtrera bort icke-prenumererade 35=W-meddelanden.
        /// </summary>
        Task<bool> IsSubscribedAsync(string sessionKey, string securityId);

        /// <summary>
        /// Spara snapshot + entries i ett transaction-scope.
        /// </summary>
        Task<long> InsertSnapshotAsync(MarketDataSnapshot snapshot, IReadOnlyList<MarketTrade> trades);

        /// <summary>
        /// Upsertar prisdjupet för ett instrument baserat på en ny 35=W.
        /// Varje position identifieras unikt av (security_id, session_key, md_entry_type, position_no).
        /// </summary>
        Task UpsertBookEntriesAsync(IReadOnlyList<ActiveMarketBookEntry> entries);

        /// <summary>
        /// Tar bort alla book-entries för ett instrument (268=0 — tom market).
        /// </summary>
        Task DeleteBookEntriesAsync(string sessionKey, string securityId);

        /// <summary>
        /// Sparar trade prints (MdEntryType=2) från 35=W i market_trades.
        /// </summary>
        Task InsertTradesAsync(IReadOnlyList<MarketTrade> trades);

        /// <summary>
        /// Upsertar enskilda book-entries från 35=X UTAN att soft-deleta övriga entries
        /// för instrumentet (till skillnad från UpsertBookEntriesAsync som speglar en full
        /// 35=W-snapshot). Nödvändigt eftersom TPICAP:s 35=X är entry-inkrementell —
        /// konkurrerande quotes på samma sida får inte skrivas över.
        /// </summary>
        Task UpsertIncrementalBookEntriesAsync(IReadOnlyList<ActiveMarketBookEntry> entries);

        /// <summary>
        /// Soft-deletar EN book-entry (security_id + sida + position_no ur MDEntryID) för
        /// TPICAP 35=X MDUpdateAction=2 (Delete).
        /// </summary>
        Task DeleteBookEntryAsync(string sessionKey, string securityId, string mdEntryType, int positionNo);

        /// <summary>
        /// Deaktiverar alla ANDRA aktiva rader för samma (session, security, sida, desk)
        /// än keepPositionNo. Behövs eftersom TPICAP:s 35=W (fallback position_no=1) och
        /// 35=X (position_no härlett ur MDEntryID) kan ge SAMMA egna pris olika position_no
        /// beroende på vilken vägen som senast skrev det — utan detta blir den gamla raden
        /// en permanent, aldrig-deaktiverad dubblett. Rör bara egna priser (matchar på
        /// originator/trader_id) — externa anonyma kvoter (NULL originator) påverkas aldrig.
        /// </summary>
        Task DeactivateStaleOwnEntriesAsync(
            string sessionKey, string securityId, string mdEntryType,
            string originator, string? traderId, int keepPositionNo);
    }
}