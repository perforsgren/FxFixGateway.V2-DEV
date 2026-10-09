using System.Globalization;
using FxFixGateway.Domain.Interfaces;
using FxTradeHub.Contracts.Dtos;
using FxTradeHub.Domain.Entities;
using FxTradeHub.Domain.Interfaces;
using FxTradeHub.Domain.Parsing;
using FxTradeHub.Domain.Repositories;
using FxTradeHub.Domain.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FxFixGateway.Infrastructure.PostMarker
{
    /// <summary>
    /// Receives FXO Hub deals from PostMarker and accepts booked ones — what PostMarkerStudio did,
    /// now always running in the gateway:
    /// <list type="bullet">
    /// <item>GetNext: each RECEIVED payload is stored in message_in (FILE / FXOHUB, PostMarkerSeqNo)
    /// and parsed by FXOhubXmlFileParser, then acknowledged. Acknowledge comes only after the payload
    /// is stored, since PostMarker does not deliver an acknowledged payload again.</item>
    /// <item>Every 10 s: POST_MARKER_ACCEPT links in READY_TO_ACK (set when MX3 booked the deal) get
    /// an Accept and become ACK_SENT.</item>
    /// <item>Heartbeat POSTMARKER_PROD through GatewayHeartbeatService, like the FIX sessions.</item>
    /// </list>
    /// A broken session is dropped and a new one connected, quickly at first and then backing off.
    /// Independent of the FIX engine: it shares only the heartbeat service and the database.
    /// </summary>
    public sealed class PostMarkerIngestService : BackgroundService
    {
        /// <summary>session_key in fxvol.session_heartbeat — the one the Blotter monitors.</summary>
        public const string SessionKey = "POSTMARKER_PROD";

        private const string SourceType = "FILE";
        private const string VenueCode = "FXOHUB";
        private const string AcceptSystemCode = "POST_MARKER_ACCEPT";
        private const string WorkflowUser = "FXFIXGATEWAY";

        // The SDK's defaults: CORE_SERVICE_TIMEOUT_SECONDS and SUBSCRIPTION_POLLING_INTERVAL_SECONDS.
        private const int GetNextTimeoutSeconds = 20;
        private static readonly TimeSpan EmptyPollDelay = TimeSpan.FromSeconds(5);

        private static readonly TimeSpan AcceptPollInterval = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan DisconnectTimeout = TimeSpan.FromSeconds(5);

        // First retry within the Blotter's 10 s alarm delay, so a short blip raises no alarm.
        private static readonly TimeSpan[] ReconnectDelays =
        {
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(15),
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(60)
        };

        private readonly PostMarkerSettings _settings;
        private readonly PostMarkerSoapClient _client;
        private readonly IMessageInService _messageInService;
        private readonly IMessageInRepository _messageInRepository;
        private readonly IMessageInParserOrchestrator _orchestrator;
        private readonly IStpRepositoryAsync _stpRepository;
        private readonly ISessionHeartbeatNotifier _heartbeat;
        private readonly ILogger<PostMarkerIngestService> _logger;

        public PostMarkerIngestService(
            PostMarkerSettings settings,
            PostMarkerSoapClient client,
            IMessageInService messageInService,
            IMessageInRepository messageInRepository,
            IMessageInParserOrchestrator orchestrator,
            IStpRepositoryAsync stpRepository,
            ISessionHeartbeatNotifier heartbeat,
            ILogger<PostMarkerIngestService> logger)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _messageInService = messageInService ?? throw new ArgumentNullException(nameof(messageInService));
            _messageInRepository = messageInRepository ?? throw new ArgumentNullException(nameof(messageInRepository));
            _orchestrator = orchestrator ?? throw new ArgumentNullException(nameof(orchestrator));
            _stpRepository = stpRepository ?? throw new ArgumentNullException(nameof(stpRepository));
            _heartbeat = heartbeat ?? throw new ArgumentNullException(nameof(heartbeat));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // Let the host finish starting (FIX engine, UI) before any network work.
            await Task.Yield();

            if (!_settings.Enabled)
            {
                _logger.LogInformation("[PostMarker] Disabled in appsettings.json — not connecting.");
                return;
            }

            if (!_settings.HasAccount)
            {
                _logger.LogError("[PostMarker] No account (UserName, Password, SystemId) — {Reason}. Not connecting.",
                    _settings.AccountError ?? "PostMarker section in fx_appsettings.json is incomplete");
                return;
            }

            var consecutiveFailures = 0;

            while (!stoppingToken.IsCancellationRequested)
            {
                string? sessionId = null;

                try
                {
                    _logger.LogInformation("[PostMarker] Connecting as {User} (System ID {SystemId}, test flag {TestFlag})...",
                        _settings.UserName, _settings.SystemId, _settings.TestFlag);

                    sessionId = await _client.ConnectAsync(
                        _settings.UserName, _settings.Password, _settings.SystemId, _settings.TestFlag, stoppingToken).ConfigureAwait(false);

                    consecutiveFailures = 0;
                    _logger.LogInformation("[PostMarker] Connected.");
                    _heartbeat.SessionOnline(SessionKey);

                    await RunSessionAsync(sessionId, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[PostMarker] Session failed.");
                }
                finally
                {
                    _heartbeat.SessionOffline(SessionKey);

                    if (sessionId != null)
                        await TryDisconnectAsync(sessionId).ConfigureAwait(false);
                }

                var delay = ReconnectDelays[Math.Min(consecutiveFailures, ReconnectDelays.Length - 1)];
                consecutiveFailures++;
                _logger.LogInformation("[PostMarker] Reconnecting in {Seconds} s.", delay.TotalSeconds);

                try
                {
                    await Task.Delay(delay, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            _logger.LogInformation("[PostMarker] Stopped.");
        }

        /// <summary>
        /// Runs the receive and accept loops until one of them fails or the gateway stops. The
        /// failure is rethrown so the caller drops the session and connects a new one.
        /// </summary>
        private async Task RunSessionAsync(string sessionId, CancellationToken stoppingToken)
        {
            using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);

            var receive = ReceiveLoopAsync(sessionId, sessionCts.Token);
            var accept = AcceptLoopAsync(sessionId, sessionCts.Token);

            var finished = await Task.WhenAny(receive, accept).ConfigureAwait(false);
            sessionCts.Cancel();

            try
            {
                await Task.WhenAll(receive, accept).ConfigureAwait(false);
            }
            catch
            {
                // The other loop's cancellation — the real failure is rethrown below.
            }

            await finished.ConfigureAwait(false);
        }

        // ────────────────────────────────────
        // Receiving deals
        // ────────────────────────────────────

        private async Task ReceiveLoopAsync(string sessionId, CancellationToken ct)
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();

                var payloads = await _client.GetNextAsync(sessionId, GetNextTimeoutSeconds, ct).ConfigureAwait(false);

                foreach (var payload in payloads.OrderBy(p => p.SequenceNumber))
                    await HandlePayloadAsync(sessionId, payload, ct).ConfigureAwait(false);

                if (payloads.Count == 0)
                    await Task.Delay(EmptyPollDelay, ct).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Stores a RECEIVED payload, then acknowledges every payload. If storing fails the payload
        /// is not acknowledged; the session restarts and PostMarker delivers it again.
        /// </summary>
        private async Task HandlePayloadAsync(string sessionId, PostMarkerPayload payload, CancellationToken ct)
        {
            if (!string.Equals(payload.Status, "RECEIVED", StringComparison.OrdinalIgnoreCase))
            {
                // ACCEPTED is PostMarker's echo of our own Accept — not a new deal.
                _logger.LogInformation("[PostMarker] SeqNo={Seq} status {Status} — not a new deal, not stored.",
                    payload.SequenceNumber, payload.Status);
            }
            else if (string.IsNullOrWhiteSpace(payload.Xml))
            {
                _logger.LogWarning("[PostMarker] SeqNo={Seq} RECEIVED without XML — not stored.", payload.SequenceNumber);
            }
            else
            {
                StoreAndParse(payload);
            }

            await _client.AcknowledgeAsync(sessionId, payload.SequenceNumber, ct).ConfigureAwait(false);
        }

        /// <summary>
        /// Stores the payload in message_in as PostMarkerStudio did, plus SourceMessageKey
        /// "POSTMARKER_{seq}" so a payload delivered again (the gateway stopped between storing and
        /// acknowledging) is not stored twice, and runs the FXOhub parser on it.
        /// </summary>
        private void StoreAndParse(PostMarkerPayload payload)
        {
            var messageKey = "POSTMARKER_" + payload.SequenceNumber.ToString(CultureInfo.InvariantCulture);

            if (_messageInRepository.ExistsBySourceMessageKey(SourceType, messageKey))
            {
                _logger.LogInformation("[PostMarker] SeqNo={Seq} already stored — skipped.", payload.SequenceNumber);
                return;
            }

            var nowUtc = DateTime.UtcNow;
            var messageInId = _messageInService.InsertMessage(new MessageIn
            {
                SourceType = SourceType,
                SourceVenueCode = VenueCode,
                ReceivedUtc = nowUtc,
                SourceTimestamp = nowUtc,
                RawPayload = payload.Xml,
                PostMarkerSeqNo = payload.SequenceNumber,
                SourceMessageKey = messageKey,
                IsAdmin = false,
                ParsedFlag = false
            });

            _logger.LogInformation("[PostMarker] SeqNo={Seq} stored as MessageInId {MessageInId}.", payload.SequenceNumber, messageInId);

            try
            {
                _orchestrator.ProcessMessage(messageInId);
            }
            catch (Exception ex)
            {
                // The payload is stored and can be reprocessed from the Blotter, so it is still acknowledged.
                _logger.LogError(ex, "[PostMarker] Parsing MessageInId {MessageInId} (SeqNo={Seq}) failed.",
                    messageInId, payload.SequenceNumber);
            }
        }

        // ────────────────────────────────────
        // Accepting booked deals
        // ────────────────────────────────────

        private async Task AcceptLoopAsync(string sessionId, CancellationToken ct)
        {
            using var timer = new PeriodicTimer(AcceptPollInterval);

            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                IReadOnlyList<PendingTradeSystemLink> pending;
                try
                {
                    pending = await _stpRepository.GetPendingTradeSystemLinksAsync(AcceptSystemCode, "READY_TO_ACK").ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[PostMarker] Reading READY_TO_ACK links failed.");
                    continue;
                }

                foreach (var link in pending)
                    await SendAcceptAsync(sessionId, link, ct).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Sends Accept for one booked deal. PostMarker refusing it (SOAP fault) marks the link
        /// ACK_ERROR. A transport failure is rethrown: the session reconnects and the link, still
        /// READY_TO_ACK, is accepted in the next session.
        /// </summary>
        private async Task SendAcceptAsync(string sessionId, PendingTradeSystemLink link, CancellationToken ct)
        {
            if (!int.TryParse(link.ExternalTradeId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seq) || seq <= 0)
            {
                _logger.LogDebug("[PostMarker] StpTradeId {StpTradeId}: ExternalTradeId '{ExternalTradeId}' is not a sequence number — skipped.",
                    link.StpTradeId, link.ExternalTradeId);
                return;
            }

            try
            {
                await _client.AcceptAsync(sessionId, seq, string.Empty, ct).ConfigureAwait(false);
            }
            catch (PostMarkerFaultException ex)
            {
                _logger.LogError("[PostMarker] Accept refused for SeqNo={Seq} (StpTradeId {StpTradeId}): {Error}",
                    seq, link.StpTradeId, ex.Message);
                await TryUpdateLinkAsync(link.StpTradeId, "ACK_ERROR", ex.Message).ConfigureAwait(false);
                return;
            }

            await TryUpdateLinkAsync(link.StpTradeId, "ACK_SENT", null).ConfigureAwait(false);

            try
            {
                await _stpRepository.InsertTradeWorkflowEventAsync(
                    link.StpTradeId, "POST_MARKER_ACCEPT_SENT", AcceptSystemCode, WorkflowUser, $"SeqNo={seq}").ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[PostMarker] Workflow event for StpTradeId {StpTradeId} failed.", link.StpTradeId);
            }

            _logger.LogInformation("[PostMarker] Accept sent: SeqNo={Seq}, StpTradeId={StpTradeId}.", seq, link.StpTradeId);
        }

        private async Task TryUpdateLinkAsync(long stpTradeId, string status, string? lastError)
        {
            try
            {
                await _stpRepository.UpdateTradeSystemLinkStatusAsync(stpTradeId, AcceptSystemCode, status, lastError).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[PostMarker] Setting POST_MARKER_ACCEPT to {Status} for StpTradeId {StpTradeId} failed.",
                    status, stpTradeId);
            }
        }

        private async Task TryDisconnectAsync(string sessionId)
        {
            try
            {
                using var timeout = new CancellationTokenSource(DisconnectTimeout);
                await _client.DisconnectAsync(sessionId, timeout.Token).ConfigureAwait(false);
                _logger.LogInformation("[PostMarker] Disconnected.");
            }
            catch (Exception ex)
            {
                _logger.LogWarning("[PostMarker] Disconnect failed: {Error}", ex.Message);
            }
        }
    }
}