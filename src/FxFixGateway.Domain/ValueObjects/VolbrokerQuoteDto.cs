namespace FxFixGateway.Domain.ValueObjects
{
    /// <summary>
    /// A price version the pricer queued for a Volbroker RFQ (fxvol.volbroker_rfq_price, status QUEUED), with what the Quote
    /// (35=S) needs from the request (fxvol.quote_requests: the request's first leg row).
    /// </summary>
    public sealed class VolbrokerPriceToSend
    {
        public long PriceId { get; init; }
        public long RfqId { get; init; }                        // quote_requests.id of the request's first leg
        public int Version { get; init; }
        public string Trader { get; init; } = string.Empty;    // the pricer user (volbroker_rfq_price.trader)
        public string? TraderName { get; init; }                // TraderID (9536): pricer_trader.volbroker_name; null = not sent
        public DateTime? ValidUntilUtc { get; init; }           // our time stipulation: the gateway cancels the price at it; null = GTC
        public decimal? SpotLow { get; init; }                  // our spot stipulation, watched by the pricer (not sent)
        public decimal? SpotHigh { get; init; }
        public decimal? SizeMillions { get; init; }             // BidSize / OfferSize (134 / 135)

        public string SessionKey { get; init; } = string.Empty;
        public string QuoteReqId { get; init; } = string.Empty; // 131
        public string? SecurityId { get; init; }                // 48
        public DateTime RequestUtc { get; init; }               // the request's time (VolbrokerDay.RequestUtc): Volbroker clears it at 17:00 New York

        public IReadOnlyList<VolbrokerPriceLegToSend> Legs { get; init; } = Array.Empty<VolbrokerPriceLegToSend>();
    }

    /// <summary>One leg of a queued Volbroker price: the leg's pair (LegSymbol 600, all the Quote repeats of it) and our vols.</summary>
    public sealed class VolbrokerPriceLegToSend
    {
        public int LegNo { get; init; }
        public string? LegSymbol { get; init; }          // 600
        public decimal BidVol { get; init; }             // LegBidPx (681)
        public decimal OfferVol { get; init; }           // LegOfferPx (684)
    }

    /// <summary>A sent Volbroker price to withdraw (status WITHDRAW): the Quote Cancel (35=Z) to send.</summary>
    public sealed class VolbrokerPriceToCancel
    {
        /// <summary>The withdraw reason when the time stipulation ran out (its Quote Status Report then makes it EXPIRED).</summary>
        public const string TimeRanOut = "time ran out";

        public long PriceId { get; init; }
        public int Version { get; init; }
        public string SessionKey { get; init; } = string.Empty;
        public string QuoteReqId { get; init; } = string.Empty;
        public string QuoteId { get; init; } = string.Empty;
        public string Trader { get; init; } = string.Empty;
        public string? Reason { get; init; }
        public DateTime RequestUtc { get; init; }   // the request's time (VolbrokerDay.RequestUtc): no cancel after Volbroker cleared it
    }

    /// <summary>A version still live (queued, being sent, sent, accepted, being withdrawn), with its request's time.</summary>
    public sealed class VolbrokerLiveVersion
    {
        public long PriceId { get; init; }
        public int Version { get; init; }
        public string Status { get; init; } = string.Empty;
        public string SessionKey { get; init; } = string.Empty;
        public string QuoteReqId { get; init; } = string.Empty;
        public DateTime RequestUtc { get; init; }
    }

    /// <summary>A Quote Response (35=AJ): Volbroker's answer to our Quote, or news of our price (removed, hit).</summary>
    public sealed class VolbrokerQuoteResponseDto
    {
        public string? QuoteRespId { get; init; }    // 693: Volbroker's price id
        public string? QuoteId { get; init; }        // 117
        public string? QuoteRespType { get; init; }  // 694: 1 hit, 3 expired / removed, 6 refused, 10 entered, 11 / 12 / 13 failed, 14, 15, 99
        public string? Text { get; init; }           // 58
        public string? BidPx { get; init; }          // 132
        public string? OfferPx { get; init; }        // 133
        public string? BidSize { get; init; }        // 134
        public string? OfferSize { get; init; }      // 135
        public string? PartyId { get; init; }        // 448: the counterparty on a hit
        public string RawPayload { get; init; } = string.Empty;
    }

    /// <summary>The Volbroker price version a message was about, after it was applied.</summary>
    public sealed class VolbrokerPriceRef
    {
        public long PriceId { get; init; }
        public long RfqId { get; init; }
        public int Version { get; init; }
        public string Status { get; init; } = string.Empty;
        public string? WithdrawReason { get; init; }
        public string SessionKey { get; init; } = string.Empty;
        public string QuoteReqId { get; init; } = string.Empty;
    }
}
