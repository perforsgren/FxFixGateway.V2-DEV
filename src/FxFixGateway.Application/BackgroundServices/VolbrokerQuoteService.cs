using FxFixGateway.Domain.Interfaces;
using FxFixGateway.Domain.ValueObjects;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FxFixGateway.Application.BackgroundServices
{
    /// <summary>
    /// Sends the pricer's prices on Volbroker RFQs (Volbroker FIX Hub API 3.8–3.11), as FxoQuoteService does for FXOhub. Every
    /// 500 ms it reads fxvol.volbroker_rfq_price: versions whose time stipulation has passed → EXPIRED when never sent, else
    /// withdrawn with a Quote Cancel; queued versions on a request no longer the trader's → REJECTED; queued versions → a Quote
    /// (35=S, QuoteType 0 indicative, a vol per leg) on the request's session (only while it is logged on; QUEUED → SENDING →
    /// SENT); versions to withdraw → a Quote Cancel (35=Z). Volbroker's Quote Responses (35=AJ), Quote Status Reports (35=AI)
    /// and Business Message Rejects (35=j) are applied as they come. Each message is written to volbroker_rfq_event.
    /// Only on <see cref="Sessions"/>: a version queued on another session is rejected, not sent.
    /// Volbroker clears its requests at 17:00 New York (<see cref="VolbrokerDay"/>): from then a live version on the request is
    /// EXPIRED, and no Quote or Quote Cancel is sent on it. The same when Volbroker dropped the request because we logged out
    /// (it keeps no RFQs over a logout, <see cref="VolbrokerReconnect"/>): a request received before its session's latest logon.
    /// The request itself is left as it is (the pricer shows it, with Send off).
    /// </summary>
    public sealed class VolbrokerQuoteService : BackgroundService, IVolbrokerQuoteService
    {
        private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(500);

        /// <summary>
        /// The sessions our Volbroker quotes go out on: production, from Monday 26 Oct (agreed with Volbroker; tested on SUP
        /// 21–22 Oct), and Volbroker's SUP environment.
        /// </summary>
        private static readonly HashSet<string> Sessions = new(StringComparer.OrdinalIgnoreCase)
        {
            "VOLB_FIXHUB_PROD",
            "VOLB_FIXHUB_DEV"
        };

        private readonly IVolbrokerQuoteRepository _repository;
        private readonly IVolbrokerQuoteSender _sender;
        private readonly ILogger<VolbrokerQuoteService> _logger;
        private readonly ISessionLogonTimes? _logons;

        /// <param name="logons">When each session last logged on (null: requests from before a logon are not checked).</param>
        public VolbrokerQuoteService(IVolbrokerQuoteRepository repository, IVolbrokerQuoteSender sender, ILogger<VolbrokerQuoteService> logger,
                                     ISessionLogonTimes? logons = null)
        {
            _repository = repository ?? throw new ArgumentNullException(nameof(repository));
            _sender     = sender     ?? throw new ArgumentNullException(nameof(sender));
            _logger     = logger     ?? throw new ArgumentNullException(nameof(logger));
            _logons     = logons;
        }

        // The session's latest logon when the request (its latest arrival) came before it: Volbroker dropped it; else null.
        private DateTime? DroppedAt(string sessionKey, DateTime lastReceivedUtc)
        {
            var logon = _logons?.LastLogonUtc(sessionKey);
            return VolbrokerReconnect.IsDropped(lastReceivedUtc, logon) ? logon : null;
        }

        public bool HandlesSession(string sessionKey) => Sessions.Contains(sessionKey);

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Volbroker quote service started (sessions: {Sessions})", string.Join(", ", Sessions));
            using var timer = new PeriodicTimer(Interval);
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await RunOnceAsync();
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Volbroker quote service: a round failed");
                }
            }
            _logger.LogInformation("Volbroker quote service stopped");
        }

        /// <summary>
        /// One round: expire, end what Volbroker cleared at 17:00 New York or dropped at our logon, reject orphans, send the queued,
        /// send the cancels.
        /// </summary>
        public async Task RunOnceAsync()
        {
            var now = DateTime.UtcNow;
            var expired = await _repository.ExpireAsync(now);
            if (expired > 0) _logger.LogInformation("Volbroker quotes: {Count} version(s) past their time stipulation (expired, or cancelled at Volbroker)", expired);
            await ClearEndedDayAsync(now);
            var orphaned = await _repository.RejectOrphanedAsync(now);
            if (orphaned > 0) _logger.LogWarning("Volbroker quotes: {Count} queued version(s) not sent: the request is no longer the trader's", orphaned);

            foreach (var price in await _repository.GetQueuedAsync(now))
                await SendAsync(price);

            foreach (var cancel in await _repository.GetWithdrawRequestsAsync())
                await CancelAsync(cancel);
        }

        /// <summary>
        /// Every live version whose request Volbroker has cleared (17:00 New York on the request's day): EXPIRED, with an event.
        /// Nothing is sent on such a request (Volbroker no longer has it).
        /// </summary>
        private async Task ClearEndedDayAsync(DateTime nowUtc)
        {
            foreach (var live in await _repository.GetLiveVersionsAsync())
            {
                if (VolbrokerDay.IsCleared(live.RequestUtc, nowUtc))
                    await ClearAsync(live.PriceId, live.Version, live.Status, live.SessionKey, live.QuoteReqId, live.RequestUtc, nowUtc);
                else if (DroppedAt(live.SessionKey, live.LastReceivedUtc) is DateTime logon)
                    await DropAsync(live.PriceId, live.Version, live.Status, live.SessionKey, live.QuoteReqId, live.LastReceivedUtc, logon, nowUtc);
            }
        }

        /// <summary>
        /// One version over because Volbroker dropped its request when we logged out (received before the session's latest logon):
        /// EXPIRED, a DROPPED event and a log line (when it changed). Nothing is sent on such a request.
        /// </summary>
        private async Task DropAsync(long priceId, int version, string status, string sessionKey, string quoteReqId, DateTime lastReceivedUtc,
                                     DateTime logonUtc, DateTime nowUtc)
        {
            var reason = (status == "QUEUED" || status == "SENDING" ? "not sent: " : "") + VolbrokerReconnect.Reason(logonUtc);
            if (!await _repository.MarkClearedAsync(priceId, reason, nowUtc)) return;
            await TryEventAsync(sessionKey, quoteReqId, "-", "DROPPED",
                $"v{version} was {status} · {reason} (request received {lastReceivedUtc:yyyy-MM-dd HH:mm:ss.fff}Z, logon {logonUtc:yyyy-MM-dd HH:mm:ss.fff}Z)", string.Empty);
            _logger.LogInformation("[{Session}] Volbroker {QuoteReqId} v{Version} ({Status}) expired: {Reason}", sessionKey, quoteReqId, version, status, reason);
        }

        /// <summary>One version over because Volbroker cleared its request: EXPIRED, an event and a log line (when it changed).</summary>
        private async Task ClearAsync(long priceId, int version, string status, string sessionKey, string quoteReqId, DateTime requestUtc, DateTime nowUtc)
        {
            var reason = status == "QUEUED" || status == "SENDING" ? "not sent: " + VolbrokerDay.ClearedReason : VolbrokerDay.ClearedReason;
            if (!await _repository.MarkClearedAsync(priceId, reason, nowUtc)) return;
            var end = VolbrokerDay.EndUtc(requestUtc);
            await TryEventAsync(sessionKey, quoteReqId, "-", "CLEARED",
                $"v{version} was {status} · {reason} ({end:yyyy-MM-dd HH:mm}Z; request {requestUtc:yyyy-MM-dd HH:mm:ss}Z)", string.Empty);
            _logger.LogInformation("[{Session}] Volbroker {QuoteReqId} v{Version} ({Status}) expired: {Reason}", sessionKey, quoteReqId, version, status, reason);
        }

        private async Task SendAsync(VolbrokerPriceToSend price)
        {
            // Queued just before Volbroker's day ended: the request is gone there, so nothing is sent.
            if (VolbrokerDay.IsCleared(price.RequestUtc, DateTime.UtcNow))
            {
                await ClearAsync(price.PriceId, price.Version, "QUEUED", price.SessionKey, price.QuoteReqId, price.RequestUtc, DateTime.UtcNow);
                return;
            }
            // Received before our latest logon: Volbroker dropped the request, so nothing is sent (it would be refused).
            if (DroppedAt(price.SessionKey, price.LastReceivedUtc) is DateTime dropped)
            {
                await DropAsync(price.PriceId, price.Version, "QUEUED", price.SessionKey, price.QuoteReqId, price.LastReceivedUtc, dropped, DateTime.UtcNow);
                return;
            }
            if (!HandlesSession(price.SessionKey))
            {
                await _repository.MarkRejectedAsync(price.PriceId, $"not sent: Volbroker quotes are not enabled on {price.SessionKey}", DateTime.UtcNow);
                _logger.LogWarning("[{Session}] Volbroker Quote for {QuoteReqId} v{Version} not sent: the session does not send quotes",
                    price.SessionKey, price.QuoteReqId, price.Version);
                return;
            }
            // Not logged on: it stays queued (the pricer shows it so) until the session is up or its time runs out.
            if (!_sender.IsLoggedOn(price.SessionKey)) return;
            if (price.Legs.Count == 0 || price.Legs.Any(l => string.IsNullOrEmpty(l.LegSymbol)))
            {
                await _repository.MarkRejectedAsync(price.PriceId, "not sent: the version has no legs, or a leg without its pair", DateTime.UtcNow);
                return;
            }
            var now = DateTime.UtcNow;
            if (!await _repository.ClaimAsync(price.PriceId, now)) return;   // withdrawn meanwhile
            var quoteId = $"VB{price.PriceId}-{price.Version}";
            string raw;
            try
            {
                raw = _sender.SendQuote(price, quoteId, now);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[{Session}] Volbroker Quote for {QuoteReqId} v{Version} not sent", price.SessionKey, price.QuoteReqId, price.Version);
                if (_sender.IsLoggedOn(price.SessionKey))
                    await _repository.MarkRejectedAsync(price.PriceId, "not sent: " + ex.Message, DateTime.UtcNow);
                else
                    await _repository.UnclaimAsync(price.PriceId, DateTime.UtcNow);
                return;
            }
            await _repository.MarkSentAsync(price.PriceId, quoteId, now);
            var legs = string.Join(", ", price.Legs.OrderBy(l => l.LegNo).Select(l => $"{l.LegSymbol} {l.BidVol}/{l.OfferVol}"));
            await TryEventAsync(price.SessionKey, price.QuoteReqId, "S", "QUOTE_SENT",
                $"v{price.Version} QuoteID={quoteId} by {price.Trader}" + (price.TraderName != null ? $" as {price.TraderName}" : " (no 9536)")
                + $" · {legs}" + (price.SizeMillions.HasValue ? $" · {price.SizeMillions.Value:0.####}m" : "")
                + (price.ValidUntilUtc.HasValue ? $" · until {price.ValidUntilUtc.Value:HH:mm:ss.fff}Z (cancelled by us then)" : " · GTC")
                + (price.SpotLow.HasValue && price.SpotHigh.HasValue ? $" · spot range {price.SpotLow.Value} / {price.SpotHigh.Value} (watched by the pricer)" : ""), raw);
            _logger.LogInformation("[{Session}] Volbroker Quote sent: {QuoteReqId} v{Version} QuoteID={QuoteId} {Legs}",
                price.SessionKey, price.QuoteReqId, price.Version, quoteId, legs);
        }

        private async Task CancelAsync(VolbrokerPriceToCancel cancel)
        {
            // Volbroker's day ended: the request and our price are gone there, so no Quote Cancel goes out.
            if (VolbrokerDay.IsCleared(cancel.RequestUtc, DateTime.UtcNow))
            {
                await ClearAsync(cancel.PriceId, cancel.Version, "WITHDRAW", cancel.SessionKey, cancel.QuoteReqId, cancel.RequestUtc, DateTime.UtcNow);
                return;
            }
            // Dropped at our logon: our price went with the request, so no Quote Cancel goes out.
            if (DroppedAt(cancel.SessionKey, cancel.LastReceivedUtc) is DateTime dropped)
            {
                await DropAsync(cancel.PriceId, cancel.Version, "WITHDRAW", cancel.SessionKey, cancel.QuoteReqId, cancel.LastReceivedUtc, dropped, DateTime.UtcNow);
                return;
            }
            if (!HandlesSession(cancel.SessionKey) || !_sender.IsLoggedOn(cancel.SessionKey)) return;
            string raw;
            try
            {
                raw = _sender.SendQuoteCancel(cancel);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[{Session}] Volbroker Quote Cancel for {QuoteId} not sent", cancel.SessionKey, cancel.QuoteId);
                return;   // tried again next round
            }
            await _repository.MarkCancelSentAsync(cancel.PriceId, DateTime.UtcNow);
            await TryEventAsync(cancel.SessionKey, cancel.QuoteReqId, "Z", "QUOTE_CANCEL_SENT",
                $"v{cancel.Version} QuoteID={cancel.QuoteId} by {cancel.Trader}" + (cancel.Reason != null ? $" · {cancel.Reason}" : ""), raw);
            _logger.LogInformation("[{Session}] Volbroker Quote Cancel sent: {QuoteId} ({Reason})", cancel.SessionKey, cancel.QuoteId, cancel.Reason);
        }

        public async Task HandleQuoteResponseAsync(string sessionKey, VolbrokerQuoteResponseDto dto)
        {
            var now = DateTime.UtcNow;
            if (string.IsNullOrEmpty(dto.QuoteRespType) || (string.IsNullOrEmpty(dto.QuoteId) && string.IsNullOrEmpty(dto.QuoteRespId)))
            {
                _logger.LogWarning("[{Session}] Volbroker 35=AJ without QuoteRespType (694), or without QuoteID (117) and QuoteRespID (693) — ignored", sessionKey);
                await TryEventAsync(sessionKey, null, "AJ", "INVALID", "QuoteRespType, or QuoteID and QuoteRespID, missing", dto.RawPayload);
                return;
            }
            var reason = ResponseReason(dto);
            var applied = await _repository.ApplyQuoteResponseAsync(dto.QuoteId, dto.QuoteRespId, dto.QuoteRespType, reason, now);
            var detail = $"QuoteID={dto.QuoteId} QuoteRespID={dto.QuoteRespId} QuoteRespType={dto.QuoteRespType} · {reason}"
                       + (applied != null ? $" · v{applied.Version} now {applied.Status}" : " · no price with this QuoteID / QuoteRespID");
            await TryEventAsync(sessionKey, applied?.QuoteReqId, "AJ", "QUOTE_RESPONSE", detail, dto.RawPayload);
            if (applied == null) _logger.LogWarning("[{Session}] Volbroker 35=AJ for an unknown quote: {Detail}", sessionKey, detail);
            else _logger.LogInformation("[{Session}] Volbroker {Detail}", sessionKey, detail);
        }

        public async Task HandleQuoteStatusReportAsync(string sessionKey, FxoQuoteStatusReportDto dto)
        {
            var now = DateTime.UtcNow;
            if (string.IsNullOrEmpty(dto.QuoteId) || string.IsNullOrEmpty(dto.QuoteStatus))
            {
                _logger.LogWarning("[{Session}] Volbroker 35=AI without QuoteID (117) or QuoteStatus (297) — ignored", sessionKey);
                await TryEventAsync(sessionKey, dto.QuoteReqId, "AI", "INVALID", "QuoteID or QuoteStatus missing", dto.RawPayload);
                return;
            }
            var applied = await _repository.ApplyQuoteStatusAsync(dto.QuoteId, dto.QuoteStatus, dto.Text, now);
            var detail = $"QuoteID={dto.QuoteId} QuoteStatus={dto.QuoteStatus} ({StatusText(dto.QuoteStatus)})"
                       + (string.IsNullOrEmpty(dto.Text) ? "" : $" · {dto.Text}")
                       + (applied != null ? $" · v{applied.Version} now {applied.Status}" : " · no price with this QuoteID");
            await TryEventAsync(sessionKey, applied?.QuoteReqId, "AI", "QUOTE_STATUS", detail, dto.RawPayload);
            if (applied == null) _logger.LogWarning("[{Session}] Volbroker 35=AI for unknown QuoteID {QuoteId}", sessionKey, dto.QuoteId);
            else _logger.LogInformation("[{Session}] Volbroker {Detail}", sessionKey, detail);
        }

        public async Task HandleBusinessRejectAsync(string sessionKey, FxoBusinessRejectDto dto)
        {
            var now = DateTime.UtcNow;
            var reason = $"business reject of 35={dto.RefMsgType}" + (dto.Reason != null ? $" (reason {dto.Reason})" : "")
                       + (string.IsNullOrEmpty(dto.Text) ? "" : $": {dto.Text}");
            var applied = (dto.RefMsgType == "S" || dto.RefMsgType == "Z") && !string.IsNullOrEmpty(dto.RefId)
                ? await _repository.ApplyBusinessRejectAsync(dto.RefId, dto.RefMsgType, reason, now)
                : null;
            await TryEventAsync(sessionKey, applied?.QuoteReqId, "j", "BUSINESS_REJECT",
                $"RefID={dto.RefId} · {reason}" + (applied != null ? $" · v{applied.Version} now {applied.Status}" : ""), dto.RawPayload);
            _logger.LogWarning("[{Session}] Volbroker {Reason} (RefID {RefId})", sessionKey, reason, dto.RefId);
        }

        /// <summary>QuoteRespType (694) as text, Volbroker's QuoteResponseDescription (58) after it; a hit with its side, size and counterparty.</summary>
        private static string ResponseReason(VolbrokerQuoteResponseDto dto)
        {
            var text = string.IsNullOrEmpty(dto.Text) ? "" : ": " + dto.Text;
            if (dto.QuoteRespType == "1")
            {
                var side = !string.IsNullOrEmpty(dto.BidPx) ? $"our bid {dto.BidPx}" + (string.IsNullOrEmpty(dto.BidSize) ? "" : $" for {dto.BidSize}m")
                         : !string.IsNullOrEmpty(dto.OfferPx) ? $"our offer {dto.OfferPx}" + (string.IsNullOrEmpty(dto.OfferSize) ? "" : $" for {dto.OfferSize}m")
                         : "a side not given";
                return $"hit on Volbroker: {side}" + (string.IsNullOrEmpty(dto.PartyId) ? "" : $" by {dto.PartyId}") + text;
            }
            return dto.QuoteRespType switch
            {
                "3"  => "removed on Volbroker" + text,
                "6"  => "refused by Volbroker" + text,
                "10" => "entered on Volbroker" + text,
                "11" => "bid not entered on Volbroker" + text,
                "12" => "offer not entered on Volbroker" + text,
                "13" => "bid and offer not entered on Volbroker" + text,
                "14" => "no price in the quote" + text,
                "15" => "not authorised to submit quotes" + text,
                "99" => "Volbroker internal error" + text,
                _    => "QuoteRespType " + dto.QuoteRespType + text
            };
        }

        /// <summary>QuoteStatus (297) of a Quote Status Report.</summary>
        private static string StatusText(string code) => code switch
        {
            "0"  => "cancel accepted",
            "5"  => "cancel rejected",
            "9"  => "no price cancelled",
            "99" => "internal error",
            _    => "status " + code
        };

        private async Task TryEventAsync(string sessionKey, string? quoteReqId, string msgType, string eventType, string? detail, string raw)
        {
            try
            {
                await _repository.InsertEventAsync(sessionKey, quoteReqId, msgType, eventType, detail, raw, DateTime.UtcNow);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[{Session}] Failed to store Volbroker quote event {Event} for {QuoteReqId}", sessionKey, eventType, quoteReqId);
            }
        }
    }
}
