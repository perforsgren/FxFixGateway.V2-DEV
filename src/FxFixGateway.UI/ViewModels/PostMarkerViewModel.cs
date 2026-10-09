using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FxFixGateway.Infrastructure.PostMarker;
using Microsoft.Extensions.Logging;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Threading;

namespace FxFixGateway.UI.ViewModels
{
    /// <summary>
    /// The PostMarker panel: connection state, the latest deals (from the database at start, then
    /// live), statistics and the activity log — what PostMarkerStudio showed — plus manual Accept
    /// and Reconnect. Follows PostMarkerIngestService through its events.
    /// </summary>
    public partial class PostMarkerViewModel : ObservableObject, IDisposable
    {
        // As many deals as PostMarkerStudio loaded at start.
        private const int RecentPayloadCount = 100;
        private const int MaxActivityEntries = 500;

        private readonly PostMarkerIngestService _service;
        private readonly Dispatcher _dispatcher;
        private bool _disposed;

        [ObservableProperty]
        private PostMarkerPayloadViewModel? _selectedPayload;

        [ObservableProperty]
        private string _stateText = "Starting";

        [ObservableProperty]
        private string _stateDetail = string.Empty;

        [ObservableProperty]
        private string _stateColor = "#64748B";

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(AcceptCommand))]
        private bool _isConnected;

        [ObservableProperty]
        private bool _isLoading;

        public ObservableCollection<PostMarkerPayloadViewModel> Payloads { get; } = new();

        /// <summary>Newest first.</summary>
        public ObservableCollection<PostMarkerActivityViewModel> Activity { get; } = new();

        public int ReceivedCount => Payloads.Count;
        public int AcknowledgedCount => Payloads.Count(p => p.Acknowledged);
        public int AcceptedCount => Payloads.Count(p => p.Accepted);
        public int LastSequence => Payloads.Count == 0 ? 0 : Payloads.Max(p => p.SequenceNumber);
        public string LastSequenceText => LastSequence > 0 ? LastSequence.ToString(CultureInfo.InvariantCulture) : "–";

        /// <summary>One line for the connection list and summary card, e.g. "Last seq 6 · 3 deals".</summary>
        public string SummaryText => Payloads.Count == 0
            ? "No deals yet"
            : $"Last seq {LastSequenceText} · {ReceivedCount} deals";

        public string SelectedXml => SelectedPayload?.Xml ?? string.Empty;

        public PostMarkerViewModel(PostMarkerIngestService service)
        {
            _service = service ?? throw new ArgumentNullException(nameof(service));
            _dispatcher = System.Windows.Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;

            _service.StateChanged += OnStateChanged;
            _service.PayloadReceived += OnPayloadReceived;
            _service.PayloadAcknowledged += OnPayloadAcknowledged;
            _service.PayloadAccepted += OnPayloadAccepted;
            _service.ActivityLogged += OnActivityLogged;

            // Lines written before this panel existed (the service starts before the window).
            foreach (var activity in _service.GetRecentActivity())
                Activity.Insert(0, new PostMarkerActivityViewModel(activity));

            UpdateState();
            _ = LoadRecentPayloadsAsync();
        }

        // ────────────────────────────────────
        // Service events (background threads → UI thread)
        // ────────────────────────────────────

        private void OnStateChanged(object? sender, EventArgs e) => OnUi(UpdateState);

        private void OnPayloadReceived(object? sender, PostMarkerPayloadInfo info) => OnUi(() =>
        {
            var existing = FindPayload(info.SequenceNumber);
            if (existing == null)
            {
                Payloads.Insert(0, new PostMarkerPayloadViewModel(info));
                SelectedPayload ??= Payloads[0];
            }

            RaiseStatistics();
        });

        private void OnPayloadAcknowledged(object? sender, int sequenceNumber) => OnUi(() =>
        {
            var payload = FindPayload(sequenceNumber);
            if (payload != null)
                payload.Acknowledged = true;

            RaiseStatistics();
        });

        private void OnPayloadAccepted(object? sender, int sequenceNumber) => OnUi(() =>
        {
            var payload = FindPayload(sequenceNumber);
            if (payload != null)
                payload.MarkAccepted();

            AcceptCommand.NotifyCanExecuteChanged();
            RaiseStatistics();
        });

        private void OnActivityLogged(object? sender, PostMarkerActivity activity) => OnUi(() =>
        {
            // Already shown if it was in GetRecentActivity when the panel was created.
            if (Activity.Take(20).Any(a => ReferenceEquals(a.Source, activity)))
                return;

            Activity.Insert(0, new PostMarkerActivityViewModel(activity));
            while (Activity.Count > MaxActivityEntries)
                Activity.RemoveAt(Activity.Count - 1);
        });

        private void OnUi(Action action)
        {
            if (_disposed) return;

            _dispatcher.BeginInvoke(() =>
            {
                if (!_disposed)
                    action();
            });
        }

        private void UpdateState()
        {
            var state = _service.State;
            var detail = _service.StateDetail;

            IsConnected = _service.IsConnected;

            (StateText, StateColor) = state switch
            {
                PostMarkerConnectionState.Connected => ("Connected · Listening", "#22C55E"),
                PostMarkerConnectionState.Connecting => ("Connecting…", "#F59E0B"),
                PostMarkerConnectionState.Reconnecting => ("Reconnecting", "#F59E0B"),
                PostMarkerConnectionState.NotConfigured => ("Not configured", "#EF4444"),
                PostMarkerConnectionState.Disabled => ("Disabled", "#64748B"),
                PostMarkerConnectionState.Stopped => ("Stopped", "#64748B"),
                _ => ("Starting", "#64748B")
            };

            if (state == PostMarkerConnectionState.Reconnecting && _service.NextConnectUtc is { } nextUtc)
            {
                var next = $"next attempt {nextUtc.ToLocalTime():HH:mm:ss}";
                StateDetail = string.IsNullOrWhiteSpace(detail) ? next : $"{next} · {detail}";
            }
            else
            {
                StateDetail = detail ?? string.Empty;
            }
        }

        // ────────────────────────────────────
        // Payloads
        // ────────────────────────────────────

        /// <summary>Loads the latest stored deals and merges them with any that arrived live meanwhile.</summary>
        private async Task LoadRecentPayloadsAsync()
        {
            try
            {
                IsLoading = true;

                var recent = await Task.Run(() => _service.LoadRecentPayloadsAsync(RecentPayloadCount));

                foreach (var info in recent.OrderByDescending(p => p.SequenceNumber))
                {
                    if (FindPayload(info.SequenceNumber) == null)
                        Payloads.Add(new PostMarkerPayloadViewModel(info));
                }

                // Newest first, live and loaded mixed.
                var ordered = Payloads.OrderByDescending(p => p.SequenceNumber).ToList();
                Payloads.Clear();
                foreach (var payload in ordered)
                    Payloads.Add(payload);

                SelectedPayload ??= Payloads.FirstOrDefault();
                RaiseStatistics();
            }
            catch (Exception ex)
            {
                Activity.Insert(0, new PostMarkerActivityViewModel(
                    new PostMarkerActivity(DateTime.UtcNow, LogLevel.Warning, $"Loading recent deals failed: {ex.Message}")));
            }
            finally
            {
                IsLoading = false;
            }
        }

        private PostMarkerPayloadViewModel? FindPayload(int sequenceNumber) =>
            Payloads.FirstOrDefault(p => p.SequenceNumber == sequenceNumber);

        private void RaiseStatistics()
        {
            OnPropertyChanged(nameof(ReceivedCount));
            OnPropertyChanged(nameof(AcknowledgedCount));
            OnPropertyChanged(nameof(AcceptedCount));
            OnPropertyChanged(nameof(LastSequence));
            OnPropertyChanged(nameof(LastSequenceText));
            OnPropertyChanged(nameof(SummaryText));
        }

        partial void OnSelectedPayloadChanged(PostMarkerPayloadViewModel? value)
        {
            OnPropertyChanged(nameof(SelectedXml));
            AcceptCommand.NotifyCanExecuteChanged();
        }

        // ────────────────────────────────────
        // Commands
        // ────────────────────────────────────

        private bool CanAccept() => IsConnected && SelectedPayload is { Accepted: false };

        /// <summary>Sends Accept for the selected deal — e.g. when the automatic Accept got ACK_ERROR.</summary>
        [RelayCommand(CanExecute = nameof(CanAccept))]
        private async Task AcceptAsync()
        {
            var payload = SelectedPayload;
            if (payload == null) return;

            var confirm = MessageBox.Show(
                $"Send Accept for SeqNo {payload.SequenceNumber} to PostMarker?\n\n" +
                "This confirms the deal to the broker. Normally the gateway does this by itself once the deal is booked in MX3.",
                "Accept deal",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (confirm != MessageBoxResult.Yes) return;

            try
            {
                await _service.AcceptManuallyAsync(payload.SequenceNumber);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Accept failed: {ex.Message}", "PostMarker", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        [RelayCommand]
        private void Reconnect()
        {
            _service.RequestReconnect();
        }

        [RelayCommand]
        private async Task RefreshAsync()
        {
            Payloads.Clear();
            SelectedPayload = null;
            await LoadRecentPayloadsAsync();
        }

        [RelayCommand]
        private void CopyXml()
        {
            if (string.IsNullOrEmpty(SelectedXml)) return;

            try
            {
                Clipboard.SetText(SelectedXml);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to copy to clipboard: {ex.Message}");
            }
        }

        public void Dispose()
        {
            _disposed = true;
            _service.StateChanged -= OnStateChanged;
            _service.PayloadReceived -= OnPayloadReceived;
            _service.PayloadAcknowledged -= OnPayloadAcknowledged;
            _service.PayloadAccepted -= OnPayloadAccepted;
            _service.ActivityLogged -= OnActivityLogged;
        }
    }

    /// <summary>One deal in the PostMarker payload list.</summary>
    public partial class PostMarkerPayloadViewModel : ObservableObject
    {
        [ObservableProperty]
        private string _status;

        [ObservableProperty]
        private bool _acknowledged;

        [ObservableProperty]
        private bool _accepted;

        public PostMarkerPayloadViewModel(PostMarkerPayloadInfo info)
        {
            SequenceNumber = info.SequenceNumber;
            ReceivedUtc = info.ReceivedUtc;
            Xml = info.Xml;
            _status = info.Status;
            _acknowledged = info.Acknowledged;
            _accepted = info.Accepted;
        }

        public int SequenceNumber { get; }
        public DateTime ReceivedUtc { get; }
        public string Xml { get; }

        /// <summary>"09:23:14" today, "Fri 09:23" otherwise.</summary>
        public string TimeText
        {
            get
            {
                var local = ReceivedUtc.ToLocalTime();
                return local.Date == DateTime.Today
                    ? local.ToString("HH:mm:ss", CultureInfo.InvariantCulture)
                    : local.ToString("ddd HH:mm", CultureInfo.InvariantCulture);
            }
        }

        public void MarkAccepted()
        {
            Accepted = true;
            Status = "ACCEPTED";
        }
    }

    /// <summary>One line in the PostMarker activity log.</summary>
    public sealed class PostMarkerActivityViewModel
    {
        public PostMarkerActivityViewModel(PostMarkerActivity source)
        {
            Source = source;
        }

        public PostMarkerActivity Source { get; }

        public string TimeText => Source.TimeUtc.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        public string Message => Source.Message;

        public string DotColor => Source.Level switch
        {
            LogLevel.Error or LogLevel.Critical => "#EF4444",
            LogLevel.Warning => "#F59E0B",
            LogLevel.Information => "#22C55E",
            _ => "#64748B"
        };
    }
}
