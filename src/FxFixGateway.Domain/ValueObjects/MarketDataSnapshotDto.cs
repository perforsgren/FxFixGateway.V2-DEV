using FxFixGateway.Domain.Entities;

namespace FxFixGateway.Domain.ValueObjects
{
    public sealed class MarketDataSnapshotDto
    {
        public string? SecurityId { get; init; }      // tag 48 (Volbroker) eller syntetisk (TPICAP)
        public string? MdReqId { get; init; }         // tag 262
        public string RawPayload { get; init; } = string.Empty;
        public IReadOnlyList<MarketDataEntryDto> Entries { get; init; } = Array.Empty<MarketDataEntryDto>();

        // Venue — null/tom = Volbroker
        public string? Venue { get; init; }

        // TPICAP composite identity (null för Volbroker)
        public string? Symbol { get; init; }  // tag 55
        public string? SecurityType { get; init; }  // tag 167
        public string? SecurityExchange { get; init; }  // tag 207
        public string? TenorValue { get; init; }  // tag 6215
        public string? OptionStrategy { get; init; }  // tag 9126
        public string? DeltaType { get; init; }  // tag 6008 (0=AllPit/25D, 1=TenDeltaPit/10D)
        public string? MaturityMonthYear { get; init; }  // tag 200
        public string? PremiumType { get; init; }  // tag 6010
        public string? DeliveryType { get; init; }  // tag 6011
        public string? DeltaBasis { get; init; }  // tag 9128
        public string? PremiumCurrencyTag { get; init; }  // tag 9129
    }

    public sealed class MarketDataEntryDto
    {
        public string? MdEntryType { get; init; }     // tag 269
        public decimal? Price { get; init; }           // tag 270
        public decimal? Size { get; init; }            // tag 271
        public string? QuoteCondition { get; init; }  // tag 276
        public string? TradeCondition { get; init; }  // tag 277
        public int? PositionNo { get; init; }          // tag 290
        public string? Originator { get; init; }       // tag 282
        public string? TraderId { get; init; }         // tag 9536
        public string? ExecInst { get; init; }         // tag 18
        public string? Scope { get; init; }            // tag 546
        public string? EntryDate { get; init; }        // tag 272 (YYYYMMDD)
        public string? EntryTime { get; init; }        // tag 273 (HH:MM:SS.sss)
    }

    /// <summary>
    /// En entry ur ett TPICAP 35=X (MarketDataIncrementalRefresh).
    /// Varje entry innehåller full instrument-identitet.
    /// </summary>
    public sealed class TpicapIncrementalEntryDto
    {
        public string SecurityId { get; init; } = string.Empty;  // syntetisk
        public string? CurrencyPair { get; init; }
        public string? Tenor { get; init; }
        public string? Cut { get; init; }
        public string? Strategy { get; init; }
        public string? Delta { get; init; }                 // kanonisk: ATM/25D/10D (ur tag 6008)
        // Raw composite-taggar (för tpicap_instruments-dimensionen)
        public string? Symbol { get; init; }                // tag 55
        public string? SecurityType { get; init; }          // tag 167
        public string? SecurityExchange { get; init; }      // tag 207
        public string? TenorValue { get; init; }            // tag 6215
        public string? OptionStrategy { get; init; }        // tag 9126
        public string? DeltaType { get; init; }             // tag 6008
        public string MdUpdateAction { get; init; } = "0";   // tag 279: 0=New,1=Change,2=Delete
        public string? MdEntryType { get; init; }           // tag 269: 0=Bid,1=Ask,2=Trade,J=EmptyBook
        public string? MdEntryId { get; init; }             // tag 278 (per-entry-identitet)
        public string? TradeCondition { get; init; }        // tag 277 (för trades)
        public decimal? Price { get; init; }
        public decimal? Size { get; init; }
        public int? PositionNo { get; init; }               // härledd ur MDEntryID (278)
        public string? Originator { get; init; }            // tag 284 DeskID (eget pris)
    }

}