using FxFixGateway.Domain.ValueObjects;

namespace FxFixGateway.Domain.Interfaces
{
    /// <summary>
    /// Hanterar inkommande MarketDataSnapshot (35=W) från Volbroker/TFSICAP.
    /// Filtrerar på prenumererade instrument och persisterar i fxvol.
    /// </summary>
    public interface IMarketDataService
    {
        /// <summary>
        /// Anropas från QuickFixApplication.FromApp när MsgType=W tas emot.
        /// </summary>
        Task HandleMarketDataSnapshotAsync(string sessionKey, MarketDataSnapshotDto dto);

        Task HandleMarketDataIncrementalRefreshAsync(string sessionKey, IReadOnlyList<TpicapIncrementalEntryDto> entries);

        /// <summary>
        /// The instrument is subscribed from now on (a QuoteRequest for it has come): its snapshots are kept, also one that came
        /// before and was found not subscribed (the answer is cached per instrument).
        /// </summary>
        void MarkSubscribed(string sessionKey, string securityId);
    }
}