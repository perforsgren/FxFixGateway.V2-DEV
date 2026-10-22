namespace FxFixGateway.Domain.ValueObjects
{
    /// <summary>
    /// A QuoteRequest (35=R) from Bloomberg FXGO Options, parsed per the FX FIXBook for Options RFS 4.1.5 (section 3.0,
    /// Quote Request). Bloomberg fixes the strike, the hedge amount, the hedge rate and the hedge direction; the quote must be
    /// a premium. Values that could not be read as the FIXBook's type are kept out and listed in <see cref="Problems"/>.
    /// </summary>
    public sealed class BloombergQuoteRequestDto
    {
        // Header
        public string? SenderSubId { get; init; }            // 50 — Bloomberg identifier of the client (FXPV)
        public string? OnBehalfOfSubId { get; init; }        // 116 — taker side firm (Bloomberg deal code)
        public string? DeliverToCompId { get; init; }        // 128 — maker side firm (Bloomberg deal code)

        public string? QuoteReqId { get; init; }             // 131
        public string? MarketSegmentId { get; init; }        // 1300 — XOFF, BSEF, BMTF, BTFE, BTBS
        public string? QuoteType { get; init; }              // 537 — 1 = tradable

        // NoRelatedSym (146), first entry
        public string? Symbol { get; init; }                 // 55 — CCY1CCY2, e.g. EURSEK
        public string? SymbolSfx { get; init; }              // 65 — FX
        public string? SecurityId { get; init; }             // 48 — CCY1CCY2
        public string? SecurityIdSource { get; init; }       // 22 — 6 = CCY
        public int ExtraRelatedSymCount { get; init; }       // NoRelatedSym entries beyond the first

        public string? Account { get; init; }                // 1 — Bloomberg identifier of the client account (FXPV)
        public string? InCompetition { get; init; }          // 6702 — Y = sent to several LPs, N = only to us
        public string? Text { get; init; }                   // 58
        public DateTime? TradeDate { get; init; }            // 75 — rolls at 17:00 New York
        public string? Side { get; init; }                   // 54 — 0 = two-way, 1 = buy, 2 = sell (CCY1, taker's view)
        public string? OptionStrategy { get; init; }         // 9126, raw code
        public string? StrategyName { get; init; }           // 9126, as section 4.0 names it

        // Parties (453)
        public string? VenueMic { get; init; }               // 448 with 447 = G: XOFF, BBSF, BMTF, BTFE
        public string? VenueRole { get; init; }              // 452: 16 off facility, 64 venue (MTF), 73 executing venue (SEF)
        public string? VenueLei { get; init; }               // 523 with 803 = 4025
        public string? TakerDealCode { get; init; }          // 448 with 452 = 13
        public string? TakerFirm { get; init; }              // 523 with 803 = 1
        public string? TakerUuid { get; init; }              // 523 with 803 = 2
        public string? TakerTrader { get; init; }            // 523 with 803 = 9
        public string? TakerLei { get; init; }               // 523 with 803 = 4025
        public string? MakerDealCode { get; init; }          // 448 with 452 = 1
        public string? MakerFirm { get; init; }              // 523 with 803 = 1
        public string? MakerLei { get; init; }               // 523 with 803 = 4025
        public int OtherPartyCount { get; init; }            // parties with another PartyRole (not stored)

        public IReadOnlyList<BloombergQuoteRequestLegDto> Legs { get; init; } = Array.Empty<BloombergQuoteRequestLegDto>();

        public decimal? SpotRate { get; init; }              // 5235
        public string? PremiumCurrency { get; init; }        // 5830
        public DateTime? PremiumDeliveryDate { get; init; }  // 5020
        public string? PremiumDeliveryType { get; init; }    // 22195 — 1 spot, 2 forward, 3 custom
        public string? ExecDeltaHedge { get; init; }         // 9015 — Y / N
        public string? HedgeTradeType { get; init; }         // 9016 — 1 spot, 2 forward, 3 custom
        public string? HedgeDirection { get; init; }         // 6666 — 0 two-way, 1 buy, 2 sell (CCY1, taker's view)
        public string? HedgeSide { get; init; }              // 9679 — 1 same, 2 opposite
        public decimal? HedgeAmount { get; init; }           // 9123, in CCY1
        public decimal? HedgeRate { get; init; }             // 9657
        public DateTime? HedgeDate { get; init; }            // 9112
        public DateTime? HedgeFixingDate { get; init; }      // 541 (NDF)
        public string? HedgeSettlMethod { get; init; }       // 22514 — P / C
        public string? HedgeSettlCurrency { get; init; }     // 22656
        public string? HedgeSecurityId { get; init; }        // 22292 — ISIN
        public string? HedgeUpiCode { get; init; }           // 22898
        public string? SettlType { get; init; }              // 63 — 0 physical, 1 cash
        public string? SettlCurrency { get; init; }          // 120
        public string? MarketType { get; init; }             // 9102 — R regular, N onshore, O offshore (NDF)
        public string? QuotePriceType { get; init; }         // 692 — 1 percentage, 2 per unit (pips)
        public string? SolveForType { get; init; }           // 22720 — 14 strike zero premium, 15 barrier zero premium
        public string? SolveForLegRefId { get; init; }       // 22723 — the leg (654) solved for
        public string? UpiCode { get; init; }                // 2891
        public int AllocationCount { get; init; }            // NoAllocs (78), not stored

        /// <summary>"9123 HedgeAmount 'x' is not a number", one line per value that could not be read (none: empty).</summary>
        public IReadOnlyList<string> Problems { get; init; } = Array.Empty<string>();

        public string RawPayload { get; init; } = string.Empty;
    }

    /// <summary>One leg (NoLegs 555) of a Bloomberg QuoteRequest.</summary>
    public sealed class BloombergQuoteRequestLegDto
    {
        public string? LegRefId { get; init; }               // 654 — Bloomberg's leg reference
        public string? LegSecurityId { get; init; }          // 602 — ISIN
        public string? LegUpiCode { get; init; }             // 2893
        public string? LegCurrency { get; init; }            // 556 — notional currency
        public string? LegIsinProduct { get; init; }         // 22410 — Vanilla_Option, Barrier_Option, Digital_Option, NDO
        public string? CutOff { get; init; }                 // 9124 — Bloomberg's code (NYK = NY 10:00, NYC = NY 16:59 …)
        public DateTime? ExpiryDate { get; init; }           // 432
        public DateTime? DeliveryDate { get; init; }         // 743
        public string? CallPutCurrency { get; init; }        // 9034
        public string? CallOrPut { get; init; }              // 9072 — 1 call, 2 put
        public decimal? Delta { get; init; }                 // 9075, in percent (52.4201)
        public decimal? StrikePrice { get; init; }           // 612 — absent when the strike is solved for
        public decimal? NotionalAmount { get; init; }        // 9018
        public string? OptionStyle { get; init; }            // 9019 — 1 American, 2 European
        public string? Position { get; init; }               // 6351 — 0 same as the strategy, 1 opposite
        public decimal? ForwardRate { get; init; }           // 9099 — forward hedge only
        public decimal? Ccy1DepositRate { get; init; }       // 9115
        public decimal? Ccy2DepositRate { get; init; }       // 9118
        public string? BarrierStyle { get; init; }           // 9130 — 1 in, 2 out
        public decimal? BarrierLevel { get; init; }          // 9131
        public decimal? BarrierLevel2 { get; init; }         // 9137
        public string? BarrierDirection { get; init; }       // 9132 — 1 up, 2 down
        public string? TouchType { get; init; }              // 9187 — 1 pay when hit, 2 pay at expiry
        public decimal? LegHedgeAmount { get; init; }        // 22562 (multi-hedge)
        public string? LegHedgeCurrency { get; init; }       // 22563
        public string? LegExecDeltaHedge { get; init; }      // 22677
        public string? LegDeltaHedgeSide { get; init; }      // 22678 — 1 buy, 2 sell, B as defined, C opposite
        public decimal? LegHedgeRate { get; init; }          // 22679
        public string? LegHedgeTradeType { get; init; }      // 22680 — 1 spot, 2 forward, 3 custom
        public DateTime? LegHedgeDate { get; init; }         // 22681
        public DateTime? LegHedgeFixingDate { get; init; }   // 22861
        public string? LegHedgeSettlMethod { get; init; }    // 22950
        public string? LegHedgeSettlCurrency { get; init; }  // 22968
        public int AllocationCount { get; init; }            // NoLegAllocs (670), not stored
    }

    /// <summary>A QuoteCancel (35=Z) from Bloomberg: 298 = 2 cancels the request (the client cancelled, or rejected the quote).</summary>
    public sealed class BloombergQuoteCancelDto
    {
        public string? QuoteReqId { get; init; }             // 131
        public string? QuoteId { get; init; }                // 117 — '*' = all quotes of the request
        public string? QuoteCancelType { get; init; }        // 298 — 2 = quote request cancel (Bloomberg)
        public string? Text { get; init; }                   // 58
        public string RawPayload { get; init; } = string.Empty;
    }

    /// <summary>A stored Bloomberg request: its id, status and when we acknowledged it (null: not yet).</summary>
    public sealed record BloombergRfqStored(long Id, string Status, DateTime? AckSentUtc);
}
