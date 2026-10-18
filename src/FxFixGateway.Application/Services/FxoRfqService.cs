using FxFixGateway.Domain.Interfaces;
using FxFixGateway.Domain.ValueObjects;
using Microsoft.Extensions.Logging;

namespace FxFixGateway.Application.Services
{
    /// <summary>
    /// Keeps TP ICAP FXOhub RFQs in fxvol: each QuoteRequest with its legs (status INCOMING), cancellations from
    /// QuoteRequestReject (status CANCELLED) and the current market depth of each request, plus an event per message.
    /// This is what the pricer's Incoming RFQ blotter reads; quoting comes later.
    /// </summary>
    public sealed class FxoRfqService : IFxoRfqService
    {
        private readonly IFxoRfqRepository _repository;
        private readonly ILogger<FxoRfqService> _logger;

        public FxoRfqService(IFxoRfqRepository repository, ILogger<FxoRfqService> logger)
        {
            _repository = repository ?? throw new ArgumentNullException(nameof(repository));
            _logger     = logger     ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task HandleQuoteRequestAsync(string sessionKey, FxoQuoteRequestDto dto)
        {
            var now = DateTime.UtcNow;
            if (string.IsNullOrEmpty(dto.QuoteReqId))
            {
                _logger.LogWarning("[{Session}] FXOhub 35=R without QuoteReqID (131) — not stored", sessionKey);
                await TryInsertEventAsync(sessionKey, null, "R", "INVALID", "QuoteReqID (131) missing", dto.RawPayload, now);
                return;
            }

            try
            {
                var id = await _repository.UpsertQuoteRequestAsync(sessionKey, dto, now);
                var detail = $"{dto.Symbol} {dto.StrategyName ?? dto.OptionStrategy} side={dto.Side} legs={dto.Legs.Count}"
                           + (dto.ExtraRelatedSymCount > 0 ? $" (+{dto.ExtraRelatedSymCount} NoRelatedSym entries not stored)" : "");
                await TryInsertEventAsync(sessionKey, dto.QuoteReqId, "R", "INCOMING", detail, dto.RawPayload, now);

                _logger.LogInformation(
                    "[{Session}] FXOhub RFQ stored: Id={Id} QuoteReqID={QuoteReqId} {Symbol} {Strategy} Side={Side} Legs={Legs} Broker={Broker}",
                    sessionKey, id, dto.QuoteReqId, dto.Symbol, dto.StrategyName ?? dto.OptionStrategy, dto.Side, dto.Legs.Count, dto.BrokerId);

                if (dto.ExtraRelatedSymCount > 0)
                    _logger.LogWarning(
                        "[{Session}] FXOhub RFQ {QuoteReqId} has {Extra} NoRelatedSym entries beyond the first — only the first is stored",
                        sessionKey, dto.QuoteReqId, dto.ExtraRelatedSymCount);

                foreach (var leg in dto.Legs.Where(l => l.TenorCode != null && l.Tenor == null))
                    _logger.LogWarning(
                        "[{Session}] FXOhub RFQ {QuoteReqId} {LegId}: unknown TenorValue code {Code} — kept as code",
                        sessionKey, dto.QuoteReqId, leg.LegId, leg.TenorCode);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[{Session}] Failed to store FXOhub RFQ {QuoteReqId}", sessionKey, dto.QuoteReqId);
            }
        }

        public async Task HandleQuoteRequestRejectAsync(string sessionKey, FxoQuoteRequestRejectDto dto)
        {
            var now = DateTime.UtcNow;
            if (string.IsNullOrEmpty(dto.QuoteReqId))
            {
                _logger.LogWarning("[{Session}] FXOhub 35=AG without QuoteReqID (131) — ignored", sessionKey);
                await TryInsertEventAsync(sessionKey, null, "AG", "INVALID", "QuoteReqID (131) missing", dto.RawPayload, now);
                return;
            }

            var reason = RejectReasonText(dto.RejectReason) + (string.IsNullOrEmpty(dto.Text) ? "" : $": {dto.Text}");
            try
            {
                var found = await _repository.MarkCancelledAsync(sessionKey, dto.QuoteReqId, reason, now);
                await TryInsertEventAsync(sessionKey, dto.QuoteReqId, "AG", found ? "CANCELLED" : "CANCELLED_UNKNOWN",
                                          reason, dto.RawPayload, now);

                if (found)
                    _logger.LogInformation("[{Session}] FXOhub RFQ {QuoteReqId} cancelled ({Reason})",
                        sessionKey, dto.QuoteReqId, reason);
                else
                    _logger.LogWarning("[{Session}] FXOhub 35=AG for unknown RFQ {QuoteReqId} ({Reason})",
                        sessionKey, dto.QuoteReqId, reason);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[{Session}] Failed to cancel FXOhub RFQ {QuoteReqId}", sessionKey, dto.QuoteReqId);
            }
        }

        public async Task HandleRfqDepthAsync(string sessionKey, FxoRfqDepthDto dto)
        {
            var now = DateTime.UtcNow;
            if (string.IsNullOrEmpty(dto.QuoteReqId))
            {
                _logger.LogWarning("[{Session}] FXOhub RFQ depth (35=W) without QuoteReqID (131) — ignored", sessionKey);
                return;
            }

            try
            {
                await _repository.ReplaceDepthAsync(sessionKey, dto, now);

                var own = dto.QuoteSets.FirstOrDefault(q => q.OwnQuote);
                var detail = dto.NoMarketData
                    ? "no market data"
                    : $"{dto.QuoteSets.Count} quote(s), best {Vol(dto.QuoteSets.FirstOrDefault()?.BidStrategyVol)}/"
                      + $"{Vol(dto.QuoteSets.FirstOrDefault()?.OfferStrategyVol)}"
                      + (own != null ? $", ours #{own.Position} {Vol(own.BidStrategyVol)}/{Vol(own.OfferStrategyVol)}" : "");
                detail += $" · MDSource={dto.MdSource}" + (dto.LastTradedPrice.HasValue ? $" · last traded {Vol(dto.LastTradedPrice)}" : "");
                await TryInsertEventAsync(sessionKey, dto.QuoteReqId, "W", dto.MdSource == "0" ? "DEPTH_EXECUTION" : "DEPTH",
                                          detail, dto.RawPayload, now);

                _logger.LogDebug("[{Session}] FXOhub RFQ {QuoteReqId} depth: {Detail}", sessionKey, dto.QuoteReqId, detail);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[{Session}] Failed to store FXOhub RFQ depth for {QuoteReqId}", sessionKey, dto.QuoteReqId);
            }
        }

        private async Task TryInsertEventAsync(string sessionKey, string? quoteReqId, string msgType, string eventType,
                                               string? detail, string rawPayload, DateTime receivedUtc)
        {
            try
            {
                await _repository.InsertEventAsync(sessionKey, quoteReqId, msgType, eventType, detail, rawPayload, receivedUtc);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[{Session}] Failed to store FXOhub RFQ event {Event} for {QuoteReqId}",
                    sessionKey, eventType, quoteReqId);
            }
        }

        private static string Vol(decimal? v) => v.HasValue ? v.Value.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture) : "-";

        /// <summary>QuoteRequestRejectReason (658).</summary>
        private static string RejectReasonText(string? code) => code switch
        {
            "1"  => "Unknown symbol",
            "2"  => "Exchange closed",
            "3"  => "Quote Request exceeds limit",
            "4"  => "Too late to enter",
            "5"  => "Invalid price",
            "6"  => "Not authorized to request quote",
            "7"  => "No match for inquiry",
            "8"  => "No market for instrument",
            "9"  => "No inventory",
            "10" => "Pass",
            "99" => "Other",
            null => "No reason given",
            _    => $"Reason {code}"
        };
    }
}
