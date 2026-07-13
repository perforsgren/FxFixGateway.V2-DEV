namespace FxFixGateway.Domain.Entities
{
    /// <summary>
    /// En trade print ur en MarketDataSnapshot/IncrementalRefresh (MdEntryType=2).
    /// Sparas i fxvol.market_trades.
    ///
    /// TradeCondition-källa skiljer sig per venue:
    ///   Volbroker: tag 277 (TradeCondition) används direkt — exakt semantik för
    ///              värdena är inte fullt verifierad mot dokumentation, se observerad data.
    ///   TPICAP:    tag 277 är TPICAP:s egen "Implied Trade"-flagga (inte köp/sälj)
    ///              och används INTE. Köp/sälj kommer från tag 6009 (TradeIndication):
    ///              0=Given (sålt i bid) → "G", 1=Paid (köpt i offer) → "P".
    ///              Se TpicapTradeIndicationToCanonical i QuickFixApplication.cs.
    /// </summary>
    public class MarketTrade
    {
        public long      Id             { get; set; }
        public string    SecurityId     { get; set; } = string.Empty;
        public string    SessionKey     { get; set; } = string.Empty;
        public string?   CurrencyPair   { get; set; }
        public string?   Tenor          { get; set; }  // från market_instruments
        public string?   Cut            { get; set; }  // från market_instruments (NY/TK/LN...)
        public string?   Strategy       { get; set; }  // från market_instruments (STRADDLE etc)
        public string?   Delta          { get; set; }  // från market_instruments (ATM/25D etc)
        public decimal?  Price          { get; set; }  // tag 270
        public decimal?  Size           { get; set; }  // tag 271
        public DateOnly? TradeDate      { get; set; }  // tag 272
        public TimeOnly? TradeTime      { get; set; }  // tag 273
        public string?   TradeCondition { get; set; }  // Volbroker: tag 277. TPICAP: G/P härlett ur tag 6009
        public string? Originator       { get; set; }
        public string? TraderId         { get; set; }
        public long      SnapshotId     { get; set; }
        public DateTime  ReceivedUtc    { get; set; }
    }
}