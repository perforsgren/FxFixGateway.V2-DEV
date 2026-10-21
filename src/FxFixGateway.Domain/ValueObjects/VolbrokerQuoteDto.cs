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
        public DateTime? ValidUntilUtc { get; init; }           // our time stipulation: ValidityPeriod (192) and cancelled at it; null = GTC
        public decimal? SpotLow { get; init; }                  // LowerSpotOutRate (188)
        public decimal? SpotHigh { get; init; }                 // UpperSpotOutRate (190)
        public decimal? SizeMillions { get; init; }             // BidSize / OfferSize (134 / 135)

        public string SessionKey { get; init; } = string.Empty;
        public string QuoteReqId { get; init; } = string.Empty; // 131
        public string? SecurityId { get; init; }                // 48

        public IReadOnlyList<VolbrokerPriceLegToSend> Legs { get; init; } = Array.Empty<VolbrokerPriceLegToSend>();

        /// <summary>
        /// ValidityPeriod (192, whole minutes, no fractions): the minutes left of the time stipulation, to the nearest whole
        /// minute (a 2 min price sent 0.4 s after it was queued has 119.6 s left: 2, not 1). The gateway cancels the price at the
        /// exact time, so Volbroker's own removal is the backstop, at most 30 s off. Null for GTC or under 30 s left (then only
        /// the gateway's cancel ends it).
        /// </summary>
        public static int? ValidityMinutes(DateTime? validUntilUtc, DateTime nowUtc)
        {
            if (!validUntilUtc.HasValue) return null;
            var minutes = (int)Math.Round((validUntilUtc.Value - nowUtc).TotalMinutes, MidpointRounding.AwayFromZero);
            return minutes >= 1 ? minutes : null;
        }
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
