namespace FxFixGateway.Domain.Entities
{
    public class TpicapInstrument
    {
        public long Id { get; set; }
        public string SessionKey { get; set; } = string.Empty;
        public string SecurityId { get; set; } = string.Empty;  // syntetisk
        public string? Symbol { get; set; }  // tag 55  (EUR/USD)
        public string? CurrencyPair { get; set; }  // härledd (EURUSD)
        public string? SecurityType { get; set; }  // tag 167
        public string? SecurityExchange { get; set; }  // tag 207
        public string? TenorValue { get; set; }  // tag 6215
        public string? OptionStrategy { get; set; }  // tag 9126
        public string? MaturityMonthYear { get; set; }  // tag 200
        public string? PremiumType { get; set; }  // tag 6010
        public string? DeliveryType { get; set; }  // tag 6011
        public string? DeltaBasis { get; set; }  // tag 9128
        public string? PremiumCcy { get; set; }  // tag 9129
        // Kanoniska kolumner (för merge)
        public string? Tenor { get; set; }  // = TenorValue
        public string? Cut { get; set; }  // härled ur SecurityExchange
        public string? Strategy { get; set; }  // härled ur OptionStrategy
        public string? Delta { get; set; }  // härled ur DeltaType (ATM/25D/10D)
        public DateTime DiscoveredUtc { get; set; }
        public DateTime UpdatedUtc { get; set; }
    }
}