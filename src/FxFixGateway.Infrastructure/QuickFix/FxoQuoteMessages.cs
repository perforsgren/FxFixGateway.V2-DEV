using System.Globalization;
using FxFixGateway.Domain.ValueObjects;
using QF = global::QuickFix;

namespace FxFixGateway.Infrastructure.QuickFix
{
    /// <summary>
    /// Builds our FXOhub Quote (35=S) and Quote Cancel (35=Z), and parses the Quote Status Report (35=AI) and Business
    /// Message Reject (35=j), per the FXOhub FIX specification (RFQ section). The Quote repeats the request's legs (LegID,
    /// LegSide, LegType as in the QuoteRequest) with our leg vols (LegBidVolatility 5678 / LegOfferVolatility 9159) only,
    /// as voice quoting: FXOhub sets the spot reference itself and derives the strategy vol, the strike of a delta or ATM leg,
    /// the premium and the hedge from it and the leg vols. The pricer's strike, delta and spot ref (stored with the price,
    /// fxohub_rfq_price / _leg) are not sent: LegSpotRate (5235), LegDelta (9075) and Strike (6354) are optional in the
    /// specification. No ValidUntilTime (62, optional) either: the time stipulation is ours (the gateway cancels the quote
    /// at fxohub_rfq_price.valid_until_utc, FXOhub does not end it at 62), so the pricer can change it on a live price and
    /// FXOhub never holds another time. The legs are a real repeating group (NoLegs 555, delimiter LegSymbol 600) in the
    /// specification's order, so the message is built as a QuickFIX message, not a raw string.
    /// </summary>
    public static class FxoQuoteMessages
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        /// <summary>The Quote's leg fields in the specification's order (LegSymbol first: the group's delimiter).</summary>
        private static readonly int[] LegOrder =
            { 600, 7940, 687, 611, 612, 624, 6713, 6215, 556, 5678, 9125, 588, 9034, 9159 };

        /// <summary>TraderID (9536): FXOhub truncates after 15 characters, so we send at most 15.</summary>
        public static string TraderId(string trader) => trader.Length > 15 ? trader.Substring(0, 15) : trader;

        public static QF.Message BuildQuote(FxoPriceToSend p, string quoteId, DateTime nowUtc)
        {
            var m = new QF.Message();
            m.Header.SetField(new QF.Fields.MsgType("S"));
            m.SetField(new QF.Fields.StringField(131, p.QuoteReqId));
            m.SetField(new QF.Fields.StringField(117, quoteId));
            m.SetField(new QF.Fields.StringField(537, "1"));                       // Tradeable
            Set(m, 55, p.Symbol);
            Set(m, 167, p.SecurityType);
            Set(m, 9126, p.OptionStrategy);
            m.SetField(new QF.Fields.StringField(60, Utc(nowUtc)));
            Set(m, 5475, p.PremiumDel);
            m.SetField(new QF.Fields.StringField(9536, TraderId(p.Trader)));
            if (p.ExecDeltaHedge == "1")
            {
                m.SetField(new QF.Fields.StringField(9015, "1"));
                Set(m, 9016, p.HedgeTradeType);
            }

            foreach (var l in p.Legs.OrderBy(l => l.LegNo))
            {
                var g = new QF.Group(555, 600, LegOrder);
                g.SetField(new QF.Fields.StringField(600, l.LegSymbol ?? "N/A"));
                Set(g, 7940, l.LegId);
                if (l.QtyMillions.HasValue) g.SetField(new QF.Fields.StringField(687, l.QtyMillions.Value.ToString("0.######", Inv)));
                if (l.MaturityDate.HasValue) g.SetField(new QF.Fields.StringField(611, l.MaturityDate.Value.ToString("yyyyMMdd", Inv)));
                if (l.RequestStrike.HasValue) g.SetField(new QF.Fields.StringField(612, l.RequestStrike.Value.ToString("0.########", Inv)));
                Set(g, 624, l.LegSide);
                Set(g, 6713, l.LegType);
                Set(g, 6215, l.TenorCode);
                Set(g, 556, l.LegCurrency);
                g.SetField(new QF.Fields.StringField(5678, l.BidVol.ToString("0.####", Inv)));
                Set(g, 9125, l.Cutoff);
                if (l.SettlDate.HasValue) g.SetField(new QF.Fields.StringField(588, l.SettlDate.Value.ToString("yyyyMMdd", Inv)));
                Set(g, 9034, l.CallPutCurrency);
                g.SetField(new QF.Fields.StringField(9159, l.OfferVol.ToString("0.####", Inv)));
                m.AddGroup(g);
            }
            return m;
        }

        /// <summary>
        /// Quote Cancel of one quote (QuoteCancelType 1). A withdraw because spot left the stipulation's range gives
        /// QuoteCancelReason 2 (market conditions); the reason text goes in Text (58).
        /// </summary>
        public static QF.Message BuildQuoteCancel(FxoPriceToCancel c)
        {
            var m = new QF.Message();
            m.Header.SetField(new QF.Fields.MsgType("Z"));
            m.SetField(new QF.Fields.StringField(131, c.QuoteReqId));
            m.SetField(new QF.Fields.StringField(117, c.QuoteId));
            m.SetField(new QF.Fields.StringField(298, "1"));
            m.SetField(new QF.Fields.StringField(9536, TraderId(c.Trader)));
            if (c.Reason != null && c.Reason.StartsWith("spot left", StringComparison.Ordinal))
                m.SetField(new QF.Fields.StringField(10127, "2"));        // MARKET CONDITIONS
            else if (c.Reason == FxoPriceToCancel.TimeRanOut)
                m.SetField(new QF.Fields.StringField(10127, "1"));        // TIMEOUT
            var text = Ascii(c.Reason);
            if (!string.IsNullOrEmpty(text))
                m.SetField(new QF.Fields.StringField(58, text.Length > 255 ? text.Substring(0, 255) : text));
            return m;
        }

        /// <summary>Text (58) is plain ASCII: "±" as "+/-", any other non-ASCII character dropped.</summary>
        public static string? Ascii(string? text)
        {
            if (text == null) return null;
            var sb = new System.Text.StringBuilder(text.Length);
            foreach (var ch in text.Replace("±", "+/-"))
                if (ch >= 32 && ch < 127) sb.Append(ch);
            return sb.ToString();
        }

        public static FxoQuoteStatusReportDto ParseQuoteStatusReport(QF.Message message, string rawMessage)
            => new()
            {
                QuoteId     = Get(message, 117),
                QuoteReqId  = Get(message, 131),
                QuoteStatus = Get(message, 297),
                Text        = Get(message, 58),
                RawPayload  = rawMessage
            };

        public static FxoBusinessRejectDto ParseBusinessReject(QF.Message message, string rawMessage)
            => new()
            {
                RefMsgType = Get(message, 372),
                RefId      = Get(message, 379),
                Reason     = Get(message, 380),
                Text       = Get(message, 58),
                RawPayload = rawMessage
            };

        /// <summary>A Business Message Reject of a Quote or Quote Cancel (ours).</summary>
        public static bool IsQuoteReject(QF.Message message)
        {
            var refType = Get(message, 372);
            return refType == "S" || refType == "Z";
        }

        private static string Utc(DateTime utc) => DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToString("yyyyMMdd-HH:mm:ss.fff", Inv);

        private static void Set(QF.FieldMap map, int tag, string? value)
        {
            if (!string.IsNullOrEmpty(value)) map.SetField(new QF.Fields.StringField(tag, value));
        }

        private static string? Get(QF.FieldMap map, int tag)
            => map.IsSetField(tag) ? map.GetString(tag) : null;
    }

    /// <summary>Sends FXOhub Quote / Quote Cancel on the session the request came on (looked up by session key).</summary>
    public sealed class FxoQuoteSender : FxFixGateway.Domain.Interfaces.IFxoQuoteSender
    {
        private readonly Func<string, QF.SessionID?> _sessionId;

        /// <summary><paramref name="sessionId"/> resolves a session key to its QuickFIX session (null when unknown).</summary>
        public FxoQuoteSender(Func<string, QF.SessionID?> sessionId)
        {
            _sessionId = sessionId ?? throw new ArgumentNullException(nameof(sessionId));
        }

        public bool IsLoggedOn(string sessionKey)
        {
            var id = _sessionId(sessionKey);
            var session = id != null ? QF.Session.LookupSession(id) : null;
            return session != null && session.IsLoggedOn;
        }

        public string SendQuote(FxoPriceToSend price, string quoteId, DateTime nowUtc)
            => Send(price.SessionKey, FxoQuoteMessages.BuildQuote(price, quoteId, nowUtc));

        public string SendQuoteCancel(FxoPriceToCancel cancel)
            => Send(cancel.SessionKey, FxoQuoteMessages.BuildQuoteCancel(cancel));

        private string Send(string sessionKey, QF.Message message)
        {
            var id = _sessionId(sessionKey) ?? throw new InvalidOperationException($"No FIX session '{sessionKey}'.");
            if (!QF.Session.SendToTarget(message, id))
                throw new InvalidOperationException($"Session '{sessionKey}' did not send the message (not logged on?).");
            return message.ToString();
        }
    }
}
