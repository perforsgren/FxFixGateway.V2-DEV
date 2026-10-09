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
    /// The gateway UI follows the service through State and the events below, and can ask for a
    /// reconnect or a manual Accept. Independent of the FIX engine: it shares only the heartbeat
    /// service and the database.
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

        // How many activity lines are kept for a UI that opens after they were written.
        private const int MaxBufferedActivity = 300;

        private readonly PostMarkerSettings _settings;
        private readonly PostMarkerSoapClient _client;
        private readonly IMessageInService _messageInService;
        private readonly IMessageInRepository _messageInRepository;
        private readonly IMessageInParserOrchestrator _orchestrator;
        private readonly IStpRepositoryAsync _stpRepository;
        private readonly ISessionHeartbeatNotifier _heartbeat;
        private readonly ILogger<PostMarkerIngestService> _logger;

        private readonly object _sync = new();
        private readonly Queue<PostMarkerActivity> _recentActivity = new();

        // Guarded by _sync. _sessionCts ends the current session, _waitCts the wait before the next.
        private CancellationTokenSource? _sessionCts;
        private CancellationTokenSource? _waitCts;
        private bool _reconnectRequested;
        private PostMarkerConnectionState _state = PostMarkerConnectionState.Starting;
        private string? _stateDetail;
        private DateTime? _nextConnectUtc;

        // Set while a session is connected; used by a manual Accept from the UI.
        private volatile string? _sessionId;

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

        // ────────────────────────────────────
        // For the gateway UI
        // ────────────────────────────────────

        /// <summary>Raised (on a background thread) whenever State, StateDetail or NextConnectUtc changes.</summary>
        public event EventHandler? StateChanged;

        /// <summary>A RECEIVED deal was stored (or delivered again). Raised on a background thread.</summary>
        public event EventHandler<PostMarkerPayloadInfo>? PayloadReceived;

        /// <summary>The deal with this sequence number was acknowledged. Raised on a background thread.</summary>
        public event EventHandler<int>? PayloadAcknowledged;

        /// <summary>Accept was sent for this sequence number (automatically or by hand). Raised on a background thread.</summary>
        public event EventHandler<int>? PayloadAccepted;

        /// <summary>A line was written to the PostMarker activity log. Raised on a background thread.</summary>
        public event EventHandler<PostMarkerActivity>? ActivityLogged;

        public PostMarkerConnectionState State
        {
            get { lock (_sync) return _state; }
        }

        /// <summary>Why the service is not connected (error, missing account), or null.</summary>
        public string? StateDetail
        {
            get { lock (_sync) return _stateDetail; }
        }

        /// <summary>When the next connect attempt is due while Reconnecting, otherwise null.</summary>
        public DateTime? NextConnectUtc
        {
            get { lock (_sync) return _nextConnectUtc; }
        }

        public bool IsConnected => _sessionId != null;

        /// <summary>The latest activity lines, oldest first — for a UI opened after they were written.</summary>
        public IReadOnlyList<PostMarkerActivity> GetRecentActivity()
        {
            lock (_sync) return _recentActivity.ToList();
        }

        /// <summary>
        /// Drops the current session and connects a new one right away; while waiting to reconnect,
        /// skips the rest of the wait. Does nothing if the service is disabled or not configured.
        /// </summary>
        public void RequestReconnect()
        {
            CancellationTokenSource? session;
            CancellationTokenSource? wait;

            lock (_sync)
            {
                if (_state is PostMarkerConnectionState.Disabled
                           or PostMarkerConnectionState.NotConfigured
                           or PostMarkerConnectionState.Stopped)
                {
                    return;
                }

                _reconnectRequested = true;
                session = _sessionCts;
                wait = _waitCts;
            }

            Report(LogLevel.Information, "Reconnect requested from the gateway UI.");

            // Off the caller's (UI) thread: cancelling runs the session's continuations.
            _ = Task.Run(() =>
            {
                TryCancel(session);
                TryCancel(wait);
            });
        }

        private static void TryCancel(CancellationTokenSource? cts)
        {
            try
            {
                cts?.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // That session or wait has already ended.
            }
        }

        /// <summary>
        /// Sends Accept for <paramref name="sequenceNumber"/> now, from the gateway UI — for a deal
        /// whose automatic Accept failed (ACK_ERROR) or that has to be accepted by hand. Marks the
        /// POST_MARKER_ACCEPT link ACK_SENT. Throws if PostMarker is not connected or refuses it.
        /// </summary>
        public async Task AcceptManuallyAsync(int sequenceNumber, CancellationToken ct = default)
        {
            var sessionId = _sessionId ?? throw new InvalidOperationException("PostMarker is not connected.");
            var seqText = sequenceNumber.ToString(CultureInfo.InvariantCulture);

            try
            {
                await _client.AcceptAsync(sessionId, sequenceNumber, string.Empty, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Report(LogLevel.Error, $"Manual Accept failed for SeqNo={seqText}: {ex.Message}", ex);
                throw;
            }

            try
            {
                await _stpRepository.UpdateTradeSystemLinkStatusByExternalTradeIdAsync(AcceptSystemCode, seqText, "ACK_SENT").ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Report(LogLevel.Warning, $"Accept sent for SeqNo={seqText}, but setting POST_MARKER_ACCEPT to ACK_SENT failed: {ex.Message}", ex);
            }

            Report(LogLevel.Information, $"Accept sent by hand: SeqNo={seqText}.");
            Raise(PayloadAccepted, sequenceNumber);
        }

        /// <summary>
        /// The latest stored PostMarker deals (message_in, venue FXOHUB) for the UI's payload list,
        /// marked Accepted where the POST_MARKER_ACCEPT link is ACK_SENT — as PostMarkerStudio
        /// loaded them. Deals injected from file (no sequence number) are left out.
        /// </summary>
        public async Task<IReadOnlyList<PostMarkerPayloadInfo>> LoadRecentPayloadsAsync(int count)
        {
            var messages = await _stpRepository.GetRecentMessageInsAsync(VenueCode, count).ConfigureAwait(false);

            IReadOnlyList<PendingTradeSystemLink> acceptedLinks;
            try
            {
                acceptedLinks = await _stpRepository.GetPendingTradeSystemLinksAsync(AcceptSystemCode, "ACK_SENT").ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[PostMarker] Reading ACK_SENT links failed — Accepted not shown.");
                acceptedLinks = Array.Empty<PendingTradeSystemLink>();
            }

            var acceptedSeqNos = new HashSet<int>();
            foreach (var link in acceptedLinks)
            {
                if (int.TryParse(link.ExternalTradeId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seq))
                    acceptedSeqNos.Add(seq);
            }

            return messages
                .Where(m => m.PostMarkerSeqNo is > 0)
                .Select(m =>
                {
                    var seq = m.PostMarkerSeqNo!.Value;
                    var accepted = acceptedSeqNos.Contains(seq);
                    return new PostMarkerPayloadInfo(
                        seq,
                        accepted ? "ACCEPTED" : "RECEIVED",
                        DateTime.SpecifyKind(m.ReceivedUtc, DateTimeKind.Utc),
                        m.RawPayload ?? string.Empty,
                        Acknowledged: true,
                        Accepted: accepted);
                })
                .ToList();
        }

        // ────────────────────────────────────
        // Session lifecycle
        // ────────────────────────────────────

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // Let the host finish starting (FIX engine, UI) before any network work.
            await Task.Yield();

            if (!_settings.Enabled)
            {
                SetState(PostMarkerConnectionState.Disabled, "\"Enabled\": false in appsettings.json");
                Report(LogLevel.Information, "Disabled in appsettings.json — not connecting.");
                return;
            }

            if (!_settings.HasAccount)
            {
                var reason = _settings.AccountError ?? "PostMarker section in fx_appsettings.json is incomplete";
                SetState(PostMarkerConnectionState.NotConfigured, reason);
                Report(LogLevel.Error, $"No account (UserName, Password, SystemId) — {reason}. Not connecting.");
                return;
            }

            var consecutiveFailures = 0;

            while (!stoppingToken.IsCancellationRequested)
            {
                string? sessionId = null;
                string? failure = null;

                using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                lock (_sync)
                {
                    _sessionCts = sessionCts;
                    _reconnectRequested = false;
                }

                try
                {
                    SetState(PostMarkerConnectionState.Connecting, null);
                    Report(LogLevel.Information,
                        $"Connecting as {_settings.UserName} (System ID {_settings.SystemId}, test flag {_settings.TestFlag})...");

                    sessionId = await _client.ConnectAsync(
                        _settings.UserName, _settings.Password, _settings.SystemId, _settings.TestFlag, sessionCts.Token).ConfigureAwait(false);

                    consecutiveFailures = 0;
                    _sessionId = sessionId;
                    SetState(PostMarkerConnectionState.Connected, null);
                    Report(LogLevel.Information, "Connected.");
                    _heartbeat.SessionOnline(SessionKey);

                    await RunSessionAsync(sessionId, sessionCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (OperationCanceledException) when (IsReconnectRequested())
                {
                    // Reconnect clicked in the UI — reported by RequestReconnect.
                }
                catch (Exception ex)
                {
                    failure = ex.Message;
                    Report(LogLevel.Error, $"Session failed: {ex.Message}", ex);
                }
                finally
                {
                    _sessionId = null;
                    lock (_sync) _sessionCts = null;

                    _heartbeat.SessionOffline(SessionKey);

                    if (sessionId != null)
                        await TryDisconnectAsync(sessionId).ConfigureAwait(false);
                }

                TimeSpan delay;
                if (TakeReconnectRequest())
                {
                    delay = TimeSpan.Zero;
                    consecutiveFailures = 0;
                }
                else
                {
                    delay = ReconnectDelays[Math.Min(consecutiveFailures, ReconnectDelays.Length - 1)];
                    consecutiveFailures++;
                }

                if (delay > TimeSpan.Zero)
                {
                    SetState(PostMarkerConnectionState.Reconnecting, failure, DateTime.UtcNow + delay);
                    Report(LogLevel.Information, $"Reconnecting in {delay.TotalSeconds:0} s.");

                    using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    lock (_sync) _waitCts = waitCts;

                    try
                    {
                        await Task.Delay(delay, waitCts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (OperationCanceledException)
                    {
                        // Reconnect clicked while waiting — connect now.
                    }
                    finally
                    {
                        lock (_sync) _waitCts = null;
                    }
                }
            }

            SetState(PostMarkerConnectionState.Stopped, null);
            Report(LogLevel.Information, "Stopped.");
        }

        /// <summary>
        /// Runs the receive and accept loops until one of them fails or the session is cancelled.
        /// The failure is rethrown so the caller drops the session and connects a new one.
        /// </summary>
        private async Task RunSessionAsync(string sessionId, CancellationToken sessionToken)
        {
            using var loopsCts = CancellationTokenSource.CreateLinkedTokenSource(sessionToken);

            var receive = ReceiveLoopAsync(sessionId, loopsCts.Token);
            var accept = AcceptLoopAsync(sessionId, loopsCts.Token);

            var finished = await Task.WhenAny(receive, accept).ConfigureAwait(false);
            loopsCts.Cancel();

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

        private bool IsReconnectRequested()
        {
            lock (_sync) return _reconnectRequested;
        }

        private bool TakeReconnectRequest()
        {
            lock (_sync)
            {
                var requested = _reconnectRequested;
                _reconnectRequested = false;
                return requested;
            }
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
            var isNewDeal = false;

            if (!string.Equals(payload.Status, "RECEIVED", StringComparison.OrdinalIgnoreCase))
            {
                // ACCEPTED is PostMarker's echo of our own Accept — not a new deal.
                Report(LogLevel.Information, $"SeqNo={payload.SequenceNumber} status {payload.Status} — not a new deal, not stored.");
            }
            else if (string.IsNullOrWhiteSpace(payload.Xml))
            {
                Report(LogLevel.Warning, $"SeqNo={payload.SequenceNumber} RECEIVED without XML — not stored.");
            }
            else
            {
                StoreAndParse(payload);
                isNewDeal = true;
            }

            if (isNewDeal)
            {
                Raise(PayloadReceived, new PostMarkerPayloadInfo(
                    payload.SequenceNumber, "RECEIVED", DateTime.UtcNow, payload.Xml, Acknowledged: false, Accepted: false));
            }

            await _client.AcknowledgeAsync(sessionId, payload.SequenceNumber, ct).ConfigureAwait(false);

            if (isNewDeal)
                Raise(PayloadAcknowledged, payload.SequenceNumber);
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
                Report(LogLevel.Information, $"SeqNo={payload.SequenceNumber} already stored — skipped.");
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

            Report(LogLevel.Information, $"SeqNo={payload.SequenceNumber} stored as MessageInId {messageInId}.");

            try
            {
                _orchestrator.ProcessMessage(messageInId);
            }
            catch (Exception ex)
            {
                // The payload is stored and can be reprocessed from the Blotter, so it is still acknowledged.
                Report(LogLevel.Error, $"Parsing MessageInId {messageInId} (SeqNo={payload.SequenceNumber}) failed: {ex.Message}", ex);
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
                Report(LogLevel.Error, $"Accept refused for SeqNo={seq} (StpTradeId {link.StpTradeId}): {ex.Message}");
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

            Report(LogLevel.Information, $"Accept sent: SeqNo={seq}, StpTradeId={link.StpTradeId}.");
            Raise(PayloadAccepted, seq);
        }

        private async Task TryUpdateLinkAsync(long stpTradeId, string status, string? lastError)
        {
            try
            {
                await _stpRepository.UpdateTradeSystemLinkStatusAsync(stpTradeId, AcceptSystemCode, status, lastError).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Report(LogLevel.Error, $"Setting POST_MARKER_ACCEPT to {status} for StpTradeId {stpTradeId} failed: {ex.Message}", ex);
            }
        }

        private async Task TryDisconnectAsync(string sessionId)
        {
            try
            {
                using var timeout = new CancellationTokenSource(DisconnectTimeout);
                await _client.DisconnectAsync(sessionId, timeout.Token).ConfigureAwait(false);
                Report(LogLevel.Information, "Disconnected.");
            }
            catch (Exception ex)
            {
                Report(LogLevel.Warning, $"Disconnect failed: {ex.Message}");
            }
        }

        // ────────────────────────────────────
        // State, activity log and events
        // ────────────────────────────────────

        private void SetState(PostMarkerConnectionState state, string? detail, DateTime? nextConnectUtc = null)
        {
            lock (_sync)
            {
                _state = state;
                _stateDetail = detail;
                _nextConnectUtc = nextConnectUtc;
            }

            Raise(StateChanged);
        }

        /// <summary>Writes the line to the gateway log ("[PostMarker] ...") and to the UI's activity log.</summary>
        private void Report(LogLevel level, string message, Exception? exception = null)
        {
            _logger.Log(level, exception, "[PostMarker] {Message}", message);

            var activity = new PostMarkerActivity(DateTime.UtcNow, level, message);
            lock (_sync)
            {
                _recentActivity.Enqueue(activity);
                while (_recentActivity.Count > MaxBufferedActivity)
                    _recentActivity.Dequeue();
            }

            Raise(ActivityLogged, activity);
        }

        // A failing UI handler must never stop the service.
        private void Raise(EventHandler? handler)
        {
            try { handler?.Invoke(this, EventArgs.Empty); }
            catch (Exception ex) { _logger.LogDebug(ex, "[PostMarker] UI event handler failed."); }
        }

        private void Raise<T>(EventHandler<T>? handler, T args)
        {
            try { handler?.Invoke(this, args); }
            catch (Exception ex) { _logger.LogDebug(ex, "[PostMarker] UI event handler failed."); }
        }
    }
}
