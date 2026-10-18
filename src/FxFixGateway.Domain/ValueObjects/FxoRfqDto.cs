namespace FxFixGateway.Domain.ValueObjects
{
    /// <summary>
    /// A QuoteRequest (35=R) from TP ICAP FXOhub, parsed per the FXOhub FIX specification (RFQ section).
    /// FXOhub has no SecurityID (48): the request is identified by QuoteReqID (131) and described by its legs.
    /// </summary>
    public sealed class FxoQuoteRequestDto
    {
        public string? QuoteReqId { get; init; }          // 131
        public string? Text { get; init; }                // 58
        public string? BrokerId { get; init; }            // 7933 — set when a broker created the request
        public string? TraderId { get; init; }            // 9536
        public string? TradeDate { get; init; }           // 75 (as sent)
        public string? ExecDeltaHedge { get; init; }      // 9015 — 0 = NO_HEDGE, 1 = HEDGE
        public string? HedgeTradeType { get; init; }      // 9016 — 0 = none, 1 = SPOT, 2 = FORWARD
        public string? PremiumDel { get; init; }          // 5475 — S = SPOT, F = FORWARD

        // NoRelatedSym (146), first entry
        public string? Symbol { get; init; }              // 55 — CCY1/CCY2 (two pairs for a currency spread)
        public string? SecurityType { get; init; }        // 167
        public string? OptionStrategy { get; init; }      // 9126 — raw code
        public string? StrategyName { get; init; }        // 9126 — readable name
        public string? QuoteRequestType { get; init; }    // 303 — 1 = Manual, 2 = Automatic
        public string? QuoteType { get; init; }           // 537 — 1 = Tradeable
        public string? Side { get; init; }                // 54 — 0 = TWO_WAY, 1 = Buy, 2 = Sell
        public DateTime? TransactTimeUtc { get; init; }   // 60
        public DateTime? ValidUntilUtc { get; init; }     // 62
        public DateTime? ExpireTimeUtc { get; init; }     // 126
        public string? NetDelta { get; init; }            // 9083
        public string? NetVega { get; init; }             // 9086
        public bool IsOwnRequest { get; init; }           // NoPartyIDs (453) present: the request is our firm's

        public IReadOnlyList<FxoQuoteRequestLegDto> Legs { get; init; } = Array.Empty<FxoQuoteRequestLegDto>();

        /// <summary>NoRelatedSym entries beyond the first (not expected from FXOhub; logged when present).</summary>
        public int ExtraRelatedSymCount { get; init; }

        public string RawPayload { get; init; } = string.Empty;
    }

    /// <summary>One leg (NoLegs 555) of an FXOhub QuoteRequest.</summary>
    public sealed class FxoQuoteRequestLegDto
    {
        public string? LegSymbol { get; init; }           // 600
        public string? LegId { get; init; }               // 7940 — Leg1, Leg2 …
        public decimal? LegQtyMillions { get; init; }     // 687 — in millions
        public DateTime? MaturityDate { get; init; }      // 611
        public decimal? StrikePrice { get; init; }        // 612 — strike-specific requests
        public string? LegSide { get; init; }             // 624 — B = as defined, C = opposite
        public string? TenorCode { get; init; }           // 6215 — raw code
        public string? Tenor { get; init; }               // 6215 — 1W, 1M … (null when the code is unknown)
        public DateTime? SettlDate { get; init; }         // 588
        public string? LegType { get; init; }             // 6713 — 1 = Call, 2 = Put
        public string? OptionStyle { get; init; }         // 9019 — 2 = European
        public string? Cutoff { get; init; }              // 9125
        public string? PitType { get; init; }             // 6007
        public DateTime? DeliveryDate { get; init; }      // 743
        public decimal? LegDelta { get; init; }           // 9075 — delta-specific requests, 1–100
        public string? AtmType { get; init; }             // 6005 — 0 = NOATM, 1 = ATM, 2 = ATMF
        public string? LegCurrency { get; init; }         // 556 — currency of the leg quantity
        public string? Currency2 { get; init; }           // 9074 — the other currency of the cross
        public string? CallPutCurrency { get; init; }     // 9034 — the currency the call/put is on
        public string? SettlCurrency { get; init; }       // 120 — premium currency
    }

    /// <summary>A QuoteRequestReject (35=AG): the request was cancelled (by its owner or by TP ICAP).</summary>
    public sealed class FxoQuoteRequestRejectDto
    {
        public string? QuoteReqId { get; init; }          // 131
        public string? RejectReason { get; init; }        // 658
        public string? Text { get; init; }                // 58
        public string? TraderId { get; init; }            // 9536
        public string RawPayload { get; init; } = string.Empty;
    }

    /// <summary>
    /// Market depth for one RFQ (35=W in the RFQ session): every quote in the market from best to worst, ours marked.
    /// A full refresh: it replaces the previous depth of the request. NoMDData = no quotes in the market.
    /// </summary>
    public sealed class FxoRfqDepthDto
    {
        public string? QuoteReqId { get; init; }          // 131
        public string? MdReqId { get; init; }             // 262
        public string? Symbol { get; init; }              // 55
        public string? MdSource { get; init; }            // 5177 — 0 = Execution, 1 = Withdrawal/Entry
        public bool NoMarketData { get; init; }           // 6003 = 1
        public decimal? LastTradedPrice { get; init; }    // 6004
        public DateTime? TransactTimeUtc { get; init; }   // 60
        public IReadOnlyList<FxoRfqQuoteSetDto> QuoteSets { get; init; } = Array.Empty<FxoRfqQuoteSetDto>();
        public string RawPayload { get; init; } = string.Empty;
    }

    /// <summary>One quote in an RFQ's depth (NoQuoteSets 296).</summary>
    public sealed class FxoRfqQuoteSetDto
    {
        public string? QuoteSetId { get; init; }          // 302
        public string? QuoteId { get; init; }             // 117
        public decimal? BidStrategyVol { get; init; }     // 6001
        public decimal? OfferStrategyVol { get; init; }   // 6002
        public bool OwnQuote { get; init; }               // 5331 = Y
        public bool OwnQuoteRequestor { get; init; }      // 6012 = Y
        public int Position { get; init; }                // order in the message (1 = best)
        public IReadOnlyList<FxoRfqQuoteEntryDto> Entries { get; init; } = Array.Empty<FxoRfqQuoteEntryDto>();
    }

    /// <summary>A leg-level price of a quote in the depth (NoMDEntries 268 inside NoQuoteSets).</summary>
    public sealed class FxoRfqQuoteEntryDto
    {
        public string? MdEntryType { get; init; }         // 269 — 0 = Bid, 1 = Offer
        public string? LegId { get; init; }               // 7940
        public decimal? Price { get; init; }              // 270 — leg vol
        public decimal? Size { get; init; }               // 271
        public int? PositionNo { get; init; }             // 290
        public string? PxRelatedTo { get; init; }         // 10502 — 6001 / 6002
        public string? LegSide { get; init; }             // 624
        public string? LegType { get; init; }             // 6713
        public string? Text { get; init; }                // 58
    }
}
