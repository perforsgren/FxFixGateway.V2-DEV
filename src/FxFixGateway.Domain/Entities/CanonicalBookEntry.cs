namespace FxFixGateway.Domain.Entities
{
    /// <summary>
    /// En venue-neutral, fullt normaliserad orderbok-rad.
    /// Skrivs vid ingest (35=W/35=X) så att merged reads över venues
    /// blir en ren WHERE — inga joins, ingen vy.
    /// </summary>
    public class CanonicalBookEntry
    {
        public long Id { get; set; }
        public string Venue { get; set; } = string.Empty;  // VOLBROKER | TPICAP
        public string SessionKey { get; set; } = string.Empty;
        public string SecurityId { get; set; } = string.Empty;
        public string CurrencyPair { get; set; } = string.Empty;
        public string Tenor { get; set; } = string.Empty;
        public string Cut { get; set; } = string.Empty;
        public string Strategy { get; set; } = string.Empty;
        public string? Delta { get; set; }                       // ATM | 25D | 10D
        public string MdEntryType { get; set; } = string.Empty;  // 0=Bid, 1=Ask
        public int PositionNo { get; set; }
        public decimal? Price { get; set; }
        public decimal? Size { get; set; }
        public string? Originator { get; set; }
        public bool IsActive { get; set; } = true;
        public DateTime UpdatedUtc { get; set; }
    }
}