using FxFixGateway.Domain.Interfaces;
using FxFixGateway.Domain.ValueObjects;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FxFixGateway.Application.BackgroundServices
{
    /// <summary>
    /// Sends the pricer's prices on TP ICAP FXOhub RFQs (gateway step 4). Every 500 ms it reads fxvol.fxohub_rfq_price:
    /// versions whose time stipulation has passed → EXPIRED; queued versions on a request no longer the trader's →
    /// REJECTED; queued versions → a Quote (35=S) on the request's session (only while it is logged on; QUEUED → SENDING
    /// → SENT); versions the pricer asked to withdraw → a Quote Cancel (35=Z). FXOhub's Quote Status Reports (35=AI) and
    /// Business Message Rejects (35=j) are applied as they come. Each message is written to fxohub_rfq_event.
    /// The spot stipulation is not in the Quote (FXOhub has no field for it): the pricer withdraws when spot leaves it.
    /// </summary>
    public sealed class FxoQuoteService : BackgroundService, IFxoQuoteService
    {
        private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(500);

        private readonly IFxoQuoteRepository _repository;
        private readonly IFxoRfqRepository _events;
        private readonly IFxoQuoteSender _sender;
        private readonly ILogger<FxoQuoteService> _logger;

        public FxoQuoteService(IFxoQuoteRepository repository, IFxoRfqRepository events, IFxoQuoteSender sender, ILogger<FxoQuoteService> logger)
        {
            _repository = repository ?? throw new ArgumentNullException(nameof(repository));
            _events     = events     ?? throw new ArgumentNullException(nameof(events));
            _sender     = sender     ?? throw new ArgumentNullException(nameof(sender));
            _logger     = logger     ?? throw new ArgumentNullException(nameof(logger));
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("FXOhub quote service started");
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
                    _logger.LogError(ex, "FXOhub quote service: a round failed");
                }
            }
            _logger.LogInformation("FXOhub quote service stopped");
        }

        /// <summary>One round: expire, reject orphans, send the queued, send the cancels.</summary>
        public async Task RunOnceAsync()
        {
            var now = DateTime.UtcNow;
            var expired = await _repository.ExpireAsync(now);
            if (expired > 0) _logger.LogInformation("FXOhub quotes: {Count} version(s) expired (time stipulation passed)", expired);
            var orphaned = await _repository.RejectOrphanedAsync(now);
            if (orphaned > 0) _logger.LogWarning("FXOhub quotes: {Count} queued version(s) not sent: the request is no longer the trader's", orphaned);

            foreach (var price in await _repository.GetQueuedAsync(now))
                await SendAsync(price);

            foreach (var cancel in await _repository.GetWithdrawRequestsAsync())
                await CancelAsync(cancel);
        }

        private async Task SendAsync(FxoPriceToSend price)
        {
            // Not logged on: it stays queued (the pricer shows it so) until the session is up or its time runs out.
            if (!_sender.IsLoggedOn(price.SessionKey)) return;
            if (price.Legs.Count == 0)
            {
                await _repository.MarkRejectedAsync(price.PriceId, "not sent: the version has no legs", DateTime.UtcNow);
                return;
            }
            var now = DateTime.UtcNow;
            if (!await _repository.ClaimAsync(price.PriceId, now)) return;   // withdrawn meanwhile
            var quoteId = $"OS{price.PriceId}-{price.Version}";
            string raw;
            try
            {
                raw = _sender.SendQuote(price, quoteId, now);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[{Session}] FXOhub Quote for {QuoteReqId} v{Version} not sent", price.SessionKey, price.QuoteReqId, price.Version);
                if (_sender.IsLoggedOn(price.SessionKey))
                    await _repository.MarkRejectedAsync(price.PriceId, "not sent: " + ex.Message, DateTime.UtcNow);
                else
                    await _repository.UnclaimAsync(price.PriceId, DateTime.UtcNow);
                return;
            }
            await _repository.MarkSentAsync(price.PriceId, quoteId, now);
            var legs = string.Join(", ", price.Legs.OrderBy(l => l.LegNo).Select(l => $"{l.LegId} {l.BidVol}/{l.OfferVol}"));
            await TryEventAsync(price.SessionKey, price.QuoteReqId, "S", "QUOTE_SENT",
                                $"v{price.Version} QuoteID={quoteId} by {price.Trader} · {legs} · our spot {price.SpotRef} (not sent) · valid until {price.ValidUntilUtc:HH:mm:ss.fff}Z", raw);
            _logger.LogInformation("[{Session}] FXOhub Quote sent: {QuoteReqId} v{Version} QuoteID={QuoteId} {Legs}",
                price.SessionKey, price.QuoteReqId, price.Version, quoteId, legs);
        }

        private async Task CancelAsync(FxoPriceToCancel cancel)
        {
            if (!_sender.IsLoggedOn(cancel.SessionKey)) return;
            string raw;
            try
            {
                raw = _sender.SendQuoteCancel(cancel);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[{Session}] FXOhub Quote Cancel for {QuoteId} not sent", cancel.SessionKey, cancel.QuoteId);
                return;   // tried again next round
            }
            await _repository.MarkCancelSentAsync(cancel.PriceId, DateTime.UtcNow);
            await TryEventAsync(cancel.SessionKey, cancel.QuoteReqId, "Z", "QUOTE_CANCEL_SENT",
                                $"QuoteID={cancel.QuoteId} by {cancel.Trader}" + (cancel.Reason != null ? $" · {cancel.Reason}" : ""), raw);
            _logger.LogInformation("[{Session}] FXOhub Quote Cancel sent: {QuoteId} ({Reason})", cancel.SessionKey, cancel.QuoteId, cancel.Reason);
        }

        public async Task HandleQuoteStatusReportAsync(string sessionKey, FxoQuoteStatusReportDto dto)
        {
            var now = DateTime.UtcNow;
            if (string.IsNullOrEmpty(dto.QuoteId) || string.IsNullOrEmpty(dto.QuoteStatus))
            {
                _logger.LogWarning("[{Session}] FXOhub 35=AI without QuoteID (117) or QuoteStatus (297) — ignored", sessionKey);
                await TryEventAsync(sessionKey, dto.QuoteReqId, "AI", "INVALID", "QuoteID or QuoteStatus missing", dto.RawPayload);
                return;
            }
            var applied = await _repository.ApplyQuoteStatusAsync(dto.QuoteId, dto.QuoteStatus, dto.Text, now);
            var detail = $"QuoteID={dto.QuoteId} QuoteStatus={dto.QuoteStatus} ({StatusText(dto.QuoteStatus)})"
                       + (string.IsNullOrEmpty(dto.Text) ? "" : $" · {dto.Text}")
                       + (applied != null ? $" · v{applied.Version} now {applied.Status}" : " · no price with this QuoteID");
            await TryEventAsync(sessionKey, dto.QuoteReqId ?? applied?.QuoteReqId, "AI", "QUOTE_STATUS", detail, dto.RawPayload);
            if (applied == null) _logger.LogWarning("[{Session}] FXOhub 35=AI for unknown QuoteID {QuoteId}", sessionKey, dto.QuoteId);
            else _logger.LogInformation("[{Session}] FXOhub {Detail}", sessionKey, detail);
        }

        public async Task HandleBusinessRejectAsync(string sessionKey, FxoBusinessRejectDto dto)
        {
            var now = DateTime.UtcNow;
            var reason = $"business reject of 35={dto.RefMsgType}" + (dto.Reason != null ? $" (reason {dto.Reason})" : "")
                       + (string.IsNullOrEmpty(dto.Text) ? "" : $": {dto.Text}");
            var applied = dto.RefMsgType == "S" && !string.IsNullOrEmpty(dto.RefId)
                ? await _repository.ApplyBusinessRejectAsync(dto.RefId, reason, now)
                : null;
            await TryEventAsync(sessionKey, applied?.QuoteReqId, "j", "BUSINESS_REJECT",
                                $"RefID={dto.RefId} · {reason}" + (applied != null ? $" · v{applied.Version} now {applied.Status}" : ""), dto.RawPayload);
            _logger.LogWarning("[{Session}] FXOhub {Reason} (RefID {RefId})", sessionKey, reason, dto.RefId);
        }

        private async Task TryEventAsync(string sessionKey, string? quoteReqId, string msgType, string eventType, string? detail, string raw)
        {
            try
            {
                await _events.InsertEventAsync(sessionKey, quoteReqId, msgType, eventType, detail, raw, DateTime.UtcNow);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[{Session}] Failed to store FXOhub quote event {Event} for {QuoteReqId}", sessionKey, eventType, quoteReqId);
            }
        }

        /// <summary>QuoteStatus (297).</summary>
        private static string StatusText(string code) => code switch
        {
            "0"  => "accepted",
            "3"  => "cancelled",
            "5"  => "rejected",
            "7"  => "expired",
            "9"  => "quote not found",
            "10" => "pending",
            _    => "status " + code
        };
    }
}
