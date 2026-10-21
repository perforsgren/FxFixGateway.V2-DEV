using System.Globalization;
using FxFixGateway.Domain.ValueObjects;
using QF = global::QuickFix;

namespace FxFixGateway.Infrastructure.QuickFix
{
    /// <summary>
    /// Builds our Volbroker Quote (35=S) and Quote Cancel (35=Z), and parses the Quote Response (35=AJ), per the Volbroker FIX
    /// Hub API (22 Aug 2024): 3.8 Quote, 3.9 Quote Cancel, 3.10 Quote Response, example 5.6.1 "Indicative (Response to RFQ)".
    /// A Quote answering an RFQ references its QuoteReqID (131); QuoteType (537) is required: 0 = indicative, as the RFQ asks
    /// (537=0 in its QuoteRequest). The legs carry only LegSymbol (600) and our vols, LegBidPx (681) / LegOfferPx (684) in
    /// vol points ("only tag 600 is required" of InstrumentLeg; with per-leg prices BidPx / OfferPx are ignored, so neither is
    /// sent). Symbol (55 = N/A) and SecurityID (48) as the example. BidSize / OfferSize (134 / 135) in millions of the request's
    /// amount currency. The time stipulation as ValidityPeriod (192, the nearest whole minute: Volbroker removes the price
    /// then; the gateway cancels it at the exact time), the spot stipulation as LowerSpotOutRate / UpperSpotOutRate (188 / 190)
    /// (not SpotLimitsOffset 191, which errors with both). TraderID (9536) only when the trader's Volbroker name is set:
    /// without it Volbroker shows the FIX account. PriceType (423, SEF) is not sent: the client default configured at
    /// Volbroker applies. To modify the price a new Quote is sent for the same QuoteReqID (no QuoteRespID 693).
    /// </summary>
    public static class VolbrokerQuoteMessages
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        /// <summary>The Quote's leg fields: LegSymbol (the group's delimiter), LegBidPx, LegOfferPx.</summary>
        private static readonly int[] LegOrder = { 600, 681, 684 };

        public static QF.Message BuildQuote(VolbrokerPriceToSend p, string quoteId, DateTime nowUtc)
        {
            var m = new QF.Message();
            m.Header.SetField(new QF.Fields.MsgType("S"));
            m.SetField(new QF.Fields.StringField(131, p.QuoteReqId));
            m.SetField(new QF.Fields.StringField(117, quoteId));
            m.SetField(new QF.Fields.StringField(537, "0"));                       // indicative
            if (!string.IsNullOrWhiteSpace(p.TraderName)) m.SetField(new QF.Fields.StringField(9536, p.TraderName));
            m.SetField(new QF.Fields.StringField(55, "N/A"));
            if (!string.IsNullOrEmpty(p.SecurityId)) m.SetField(new QF.Fields.StringField(48, p.SecurityId));

            foreach (var l in p.Legs.OrderBy(l => l.LegNo))
            {
                var g = new QF.Group(555, 600, LegOrder);
                g.SetField(new QF.Fields.StringField(600, l.LegSymbol ?? throw new InvalidOperationException($"Leg {l.LegNo} has no pair (600).")));
                g.SetField(new QF.Fields.StringField(681, l.BidVol.ToString("0.####", Inv)));
                g.SetField(new QF.Fields.StringField(684, l.OfferVol.ToString("0.####", Inv)));
                m.AddGroup(g);
            }

            if (p.SizeMillions.HasValue)
            {
                var size = p.SizeMillions.Value.ToString("0.####", Inv);
                m.SetField(new QF.Fields.StringField(134, size));
                m.SetField(new QF.Fields.StringField(135, size));
            }
            var minutes = VolbrokerPriceToSend.ValidityMinutes(p.ValidUntilUtc, nowUtc);
            if (minutes.HasValue) m.SetField(new QF.Fields.StringField(192, minutes.Value.ToString(Inv)));
            if (p.SpotLow.HasValue && p.SpotHigh.HasValue)
            {
                m.SetField(new QF.Fields.StringField(188, p.SpotLow.Value.ToString("0.########", Inv)));
                m.SetField(new QF.Fields.StringField(190, p.SpotHigh.Value.ToString("0.########", Inv)));
            }
            return m;
        }

        /// <summary>Quote Cancel: the QuoteID only (3.9, example 5.8).</summary>
        public static QF.Message BuildQuoteCancel(VolbrokerPriceToCancel c)
        {
            var m = new QF.Message();
            m.Header.SetField(new QF.Fields.MsgType("Z"));
            m.SetField(new QF.Fields.StringField(117, c.QuoteId));
            return m;
        }

        public static VolbrokerQuoteResponseDto ParseQuoteResponse(QF.Message message, string rawMessage)
            => new()
            {
                QuoteRespId   = Get(message, 693),
                QuoteId       = Get(message, 117),
                QuoteRespType = Get(message, 694),
                Text          = Get(message, 58),
                BidPx         = Get(message, 132),
                OfferPx       = Get(message, 133),
                BidSize       = Get(message, 134),
                OfferSize     = Get(message, 135),
                PartyId       = PartyId(message),
                RawPayload    = rawMessage
            };

        // The counterparty on a hit: the Parties group's first PartyID (448), else 448 on the message.
        private static string? PartyId(QF.Message message)
        {
            if (message.IsSetField(453))
            {
                var group = new QF.Group(453, 448);
                message.GetGroup(1, group);
                if (group.IsSetField(448)) return group.GetString(448);
            }
            return Get(message, 448);
        }

        private static string? Get(QF.FieldMap map, int tag)
            => map.IsSetField(tag) ? map.GetString(tag) : null;
    }

    /// <summary>Sends Volbroker Quote / Quote Cancel on the session the request came on (looked up by session key).</summary>
    public sealed class VolbrokerQuoteSender : FxFixGateway.Domain.Interfaces.IVolbrokerQuoteSender
    {
        private readonly Func<string, QF.SessionID?> _sessionId;

        /// <summary><paramref name="sessionId"/> resolves a session key to its QuickFIX session (null when unknown).</summary>
        public VolbrokerQuoteSender(Func<string, QF.SessionID?> sessionId)
        {
            _sessionId = sessionId ?? throw new ArgumentNullException(nameof(sessionId));
        }

        public bool IsLoggedOn(string sessionKey)
        {
            var id = _sessionId(sessionKey);
            var session = id != null ? QF.Session.LookupSession(id) : null;
            return session != null && session.IsLoggedOn;
        }

        public string SendQuote(VolbrokerPriceToSend price, string quoteId, DateTime nowUtc)
            => Send(price.SessionKey, VolbrokerQuoteMessages.BuildQuote(price, quoteId, nowUtc));

        public string SendQuoteCancel(VolbrokerPriceToCancel cancel)
            => Send(cancel.SessionKey, VolbrokerQuoteMessages.BuildQuoteCancel(cancel));

        private string Send(string sessionKey, QF.Message message)
        {
            var id = _sessionId(sessionKey) ?? throw new InvalidOperationException($"No FIX session '{sessionKey}'.");
            if (!QF.Session.SendToTarget(message, id))
                throw new InvalidOperationException($"Session '{sessionKey}' did not send the message (not logged on?).");
            return message.ToString();
        }
    }
}
