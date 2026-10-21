namespace FxFixGateway.Domain.ValueObjects
{
    /// <summary>
    /// A price version the pricer queued for an FXOhub RFQ (fxvol.fxohub_rfq_price, status QUEUED), with what the Quote
    /// (35=S) needs from the request (fxohub_rfq, fxohub_rfq_leg).
    /// </summary>
    public sealed class FxoPriceToSend
    {
        public long PriceId { get; init; }
        public long RfqId { get; init; }
        public int Version { get; init; }
        public string Trader { get; init; } = string.Empty;   // TraderID (9536), first 15 characters
        public DateTime? ValidUntilUtc { get; init; }          // our time stipulation (cancelled at it; 62 is not sent); null = GTC
        public decimal SpotRef { get; init; }                  // LegSpotRate (5235)

        public string SessionKey { get; init; } = string.Empty;
        public string QuoteReqId { get; init; } = string.Empty; // 131
        public string? Symbol { get; init; }                    // 55
        public string? SecurityType { get; init; }              // 167
        public string? OptionStrategy { get; init; }            // 9126
        public string? PremiumDel { get; init; }                // 5475
        public string? ExecDeltaHedge { get; init; }            // 9015
        public string? HedgeTradeType { get; init; }            // 9016

        public IReadOnlyList<FxoPriceLegToSend> Legs { get; init; } = Array.Empty<FxoPriceLegToSend>();
    }

    /// <summary>One leg of a queued price: the request's leg (as the Quote must repeat it) and our vols, strike and delta.</summary>
    public sealed class FxoPriceLegToSend
    {
        public int LegNo { get; init; }
        public string? LegSymbol { get; init; }          // 600
        public string? LegId { get; init; }              // 7940
        public decimal? QtyMillions { get; init; }       // 687
        public DateTime? MaturityDate { get; init; }     // 611
        public decimal? RequestStrike { get; init; }     // 612 (strike-specific requests)
        public string? LegSide { get; init; }            // 624
        public string? LegType { get; init; }            // 6713
        public string? TenorCode { get; init; }          // 6215
        public string? LegCurrency { get; init; }        // 556
        public string? Cutoff { get; init; }             // 9125
        public DateTime? SettlDate { get; init; }        // 588
        public string? CallPutCurrency { get; init; }    // 9034
        public decimal BidVol { get; init; }             // 5678
        public decimal OfferVol { get; init; }           // 9159
        public decimal Strike { get; init; }             // 6354
        public decimal DeltaPct { get; init; }           // 9075
    }

    /// <summary>A sent price the pricer asked to withdraw (status WITHDRAW): the Quote Cancel (35=Z) to send.</summary>
    public sealed class FxoPriceToCancel
    {
        /// <summary>The withdraw reason when the time stipulation ran out: sent with QuoteCancelReason 1 (TIMEOUT).</summary>
        public const string TimeRanOut = "time ran out";

        public long PriceId { get; init; }
        public string SessionKey { get; init; } = string.Empty;
        public string QuoteReqId { get; init; } = string.Empty;
        public string QuoteId { get; init; } = string.Empty;
        public string Trader { get; init; } = string.Empty;
        public string? Reason { get; init; }
    }

    /// <summary>A Quote Status Report (35=AI): FXOhub's answer to our Quote or Quote Cancel.</summary>
    public sealed class FxoQuoteStatusReportDto
    {
        public string? QuoteId { get; init; }        // 117
        public string? QuoteReqId { get; init; }     // 131
        public string? QuoteStatus { get; init; }    // 297: 0 accepted, 3 cancelled, 5 rejected, 7 expired, 9 not found, 10 pending
        public string? Text { get; init; }           // 58
        public string RawPayload { get; init; } = string.Empty;
    }

    /// <summary>A Business Message Reject (35=j) of one of our messages.</summary>
    public sealed class FxoBusinessRejectDto
    {
        public string? RefMsgType { get; init; }     // 372
        public string? RefId { get; init; }          // 379 BusinessRejectRefID (our QuoteID)
        public string? Reason { get; init; }         // 380
        public string? Text { get; init; }           // 58
        public string RawPayload { get; init; } = string.Empty;
    }

    /// <summary>The price version a Quote Status Report or reject was about, after it was applied.</summary>
    public sealed class FxoPriceRef
    {
        public long PriceId { get; init; }
        public long RfqId { get; init; }
        public int Version { get; init; }
        public string Status { get; init; } = string.Empty;
        public string SessionKey { get; init; } = string.Empty;
        public string QuoteReqId { get; init; } = string.Empty;
    }
}
