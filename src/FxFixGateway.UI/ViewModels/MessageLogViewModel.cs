using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FxFixGateway.Domain.Enums;
using FxFixGateway.Domain.Events;
using FxFixGateway.Domain.Interfaces;
using FxFixGateway.Domain.ValueObjects;

namespace FxFixGateway.UI.ViewModels
{
    public partial class MessageLogViewModel : ObservableObject, IDisposable
    {
        // Every message type the gateway sends or receives, plus the session events that
        // MessageLogRepository logs — in the order the Type filter lists them.
        private static readonly (string Code, string Name)[] KnownMessageTypes =
        {
            ("AE", "TradeCaptureReport"),
            ("AR", "TradeCaptureReportAck"),
            ("8", "ExecutionReport"),
            ("j", "BusinessMessageReject"),
            ("R", "QuoteRequest"),
            ("V", "MarketDataRequest"),
            ("W", "MarketDataSnapshot"),
            ("X", "MarketDataIncrementalRefresh"),
            ("Y", "MarketDataRequestReject"),
            ("x", "SecurityListRequest"),
            ("y", "SecurityList"),
            ("A", "Logon"),
            ("0", "Heartbeat"),
            ("1", "TestRequest"),
            ("2", "ResendRequest"),
            ("3", "Reject"),
            ("4", "SequenceReset"),
            ("5", "Logout"),
            ("CREATE", "Session Created"),
            ("LOGON", "Logon Confirmed"),
            ("LOGOUT", "Logout Received"),
            ("?", "Unknown")
        };

        private readonly IMessageLogger _messageLogger;
        private readonly IFixEngine? _fixEngine;
        private string? _currentSessionKey;
        private bool _disposed;

        [ObservableProperty]
        private ObservableCollection<MessageLogEntryViewModel> _messages = new();

        [ObservableProperty]
        private MessageLogEntryViewModel? _selectedMessage;

        [ObservableProperty]
        private string _filterText = string.Empty;

        [ObservableProperty]
        private string _selectedDirection = "All";

        [ObservableProperty]
        private string _selectedMsgType = "All";

        [ObservableProperty]
        private bool _isLoading;

        [ObservableProperty]
        private string _rawMessageText = string.Empty;

        public ObservableCollection<string> DirectionOptions { get; } = new()
        {
            "All", "Incoming", "Outgoing"
        };

        /// <summary>
        /// The Type filter: "All" plus every message type among the loaded messages (and the
        /// selected one), kept in display order and extended as new types arrive live.
        /// </summary>
        public ObservableCollection<MsgTypeOption> MsgTypeOptions { get; } = new()
        {
            MsgTypeOption.All
        };

        public MessageLogViewModel(IMessageLogger messageLogger, IFixEngine? fixEngine = null)
        {
            _messageLogger = messageLogger ?? throw new ArgumentNullException(nameof(messageLogger));
            _fixEngine = fixEngine;

            // Subscribe to real-time events if engine is available
            if (_fixEngine != null)
            {
                _fixEngine.MessageReceived += OnMessageReceived;
                _fixEngine.MessageSent += OnMessageSent;
            }
        }

        private void OnMessageReceived(object? sender, MessageReceivedEvent e)
        {
            if (_disposed || e.SessionKey != _currentSessionKey) return;

            System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                if (_disposed) return;

                var entry = new MessageLogEntry(
                    e.ReceivedAtUtc,
                    MessageDirection.Incoming,
                    e.MsgType,
                    GetMessageSummary(e.MsgType),
                    e.RawMessage);

                // Insert at top (newest first)
                Messages.Insert(0, new MessageLogEntryViewModel(entry));
                AddMsgTypeOption(e.MsgType);
                OnPropertyChanged(nameof(FilteredMessages));
            });
        }

        private void OnMessageSent(object? sender, MessageSentEvent e)
        {
            if (_disposed || e.SessionKey != _currentSessionKey) return;

            System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                if (_disposed) return;

                var entry = new MessageLogEntry(
                    e.SentAtUtc,
                    MessageDirection.Outgoing,
                    e.MsgType,
                    GetMessageSummary(e.MsgType),
                    e.RawMessage);

                // Insert at top (newest first)
                Messages.Insert(0, new MessageLogEntryViewModel(entry));
                AddMsgTypeOption(e.MsgType);
                OnPropertyChanged(nameof(FilteredMessages));
            });
        }

        private static string GetMessageSummary(string msgType)
        {
            foreach (var (code, name) in KnownMessageTypes)
            {
                if (code == msgType)
                    return name;
            }

            return $"MsgType {msgType}";
        }

        public async Task LoadMessagesAsync(string sessionKey)
        {
            if (string.IsNullOrEmpty(sessionKey))
            {
                Messages.Clear();
                RebuildMsgTypeOptions();
                return;
            }

            _currentSessionKey = sessionKey;

            try
            {
                IsLoading = true;

                var entries = await _messageLogger.GetRecentAsync(sessionKey, 500);

                Messages.Clear();
                foreach (var entry in entries)
                {
                    Messages.Add(new MessageLogEntryViewModel(entry));
                }

                RebuildMsgTypeOptions();
                OnPropertyChanged(nameof(FilteredMessages));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to load messages: {ex.Message}");
            }
            finally
            {
                IsLoading = false;
            }
        }

        partial void OnSelectedMessageChanged(MessageLogEntryViewModel? value)
        {
            RawMessageText = value?.RawText ?? string.Empty;
        }

        [RelayCommand]
        private async Task RefreshAsync()
        {
            if (!string.IsNullOrEmpty(_currentSessionKey))
            {
                await LoadMessagesAsync(_currentSessionKey);
            }
        }

        [RelayCommand]
        private void CopyRawMessage()
        {
            if (!string.IsNullOrEmpty(RawMessageText))
            {
                try
                {
                    Clipboard.SetText(RawMessageText);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Failed to copy to clipboard: {ex.Message}");
                }
            }
        }

        [RelayCommand]
        private void ClearMessages()
        {
            Messages.Clear();
            RawMessageText = string.Empty;
            RebuildMsgTypeOptions();
            OnPropertyChanged(nameof(FilteredMessages));
        }

        // ────────────────────────────────────
        // Type filter
        // ────────────────────────────────────

        /// <summary>
        /// Rebuilds the Type filter from the loaded messages. The selected type stays selected —
        /// and listed — even when no message of that type is loaded, e.g. right after Clear.
        /// </summary>
        private void RebuildMsgTypeOptions()
        {
            var selected = string.IsNullOrEmpty(SelectedMsgType) ? "All" : SelectedMsgType;

            while (MsgTypeOptions.Count > 1)
                MsgTypeOptions.RemoveAt(MsgTypeOptions.Count - 1);

            foreach (var msgType in Messages.Select(m => m.MsgType).Distinct())
                AddMsgTypeOption(msgType);

            if (selected != "All")
                AddMsgTypeOption(selected);

            // Removing the selected item makes the ComboBox clear the selection; restore it.
            SelectedMsgType = selected;
        }

        /// <summary>Adds a message type to the Type filter, in display order, unless it is already listed.</summary>
        private void AddMsgTypeOption(string msgType)
        {
            if (string.IsNullOrEmpty(msgType) || MsgTypeOptions.Any(o => o.Code == msgType))
                return;

            var index = 1; // after "All"
            while (index < MsgTypeOptions.Count && IsListedBefore(MsgTypeOptions[index].Code, msgType))
                index++;

            MsgTypeOptions.Insert(index, new MsgTypeOption(msgType, $"{msgType} · {GetMessageSummary(msgType)}"));
        }

        /// <summary>Known types in KnownMessageTypes order first, then any other type alphabetically.</summary>
        private static bool IsListedBefore(string listed, string msgType)
        {
            var listedRank = GetDisplayRank(listed);
            var newRank = GetDisplayRank(msgType);

            return listedRank != newRank
                ? listedRank < newRank
                : string.CompareOrdinal(listed, msgType) < 0;
        }

        private static int GetDisplayRank(string msgType)
        {
            for (var i = 0; i < KnownMessageTypes.Length; i++)
            {
                if (KnownMessageTypes[i].Code == msgType)
                    return i;
            }

            return KnownMessageTypes.Length;
        }

        public ObservableCollection<MessageLogEntryViewModel> FilteredMessages
        {
            get
            {
                var filtered = Messages.AsEnumerable();

                if (SelectedDirection != "All")
                {
                    var direction = SelectedDirection == "Incoming"
                        ? MessageDirection.Incoming
                        : MessageDirection.Outgoing;
                    filtered = filtered.Where(m => m.Direction == direction);
                }

                // Null while the Type filter is being rebuilt (the ComboBox clears its selection).
                if (!string.IsNullOrEmpty(SelectedMsgType) && SelectedMsgType != "All")
                {
                    filtered = filtered.Where(m => m.MsgType == SelectedMsgType);
                }

                if (!string.IsNullOrWhiteSpace(FilterText))
                {
                    var search = FilterText.ToLowerInvariant();
                    filtered = filtered.Where(m =>
                        m.Summary.ToLowerInvariant().Contains(search) ||
                        m.MsgType.ToLowerInvariant().Contains(search) ||
                        m.RawText.ToLowerInvariant().Contains(search));
                }

                return new ObservableCollection<MessageLogEntryViewModel>(filtered);
            }
        }

        partial void OnFilterTextChanged(string value) => OnPropertyChanged(nameof(FilteredMessages));
        partial void OnSelectedDirectionChanged(string value) => OnPropertyChanged(nameof(FilteredMessages));
        partial void OnSelectedMsgTypeChanged(string value) => OnPropertyChanged(nameof(FilteredMessages));

        public void Dispose()
        {
            _disposed = true;
            if (_fixEngine != null)
            {
                _fixEngine.MessageReceived -= OnMessageReceived;
                _fixEngine.MessageSent -= OnMessageSent;
            }
        }
    }

    /// <summary>One entry in the Type filter: the FIX MsgType code and "code · name" for display.</summary>
    public sealed class MsgTypeOption
    {
        public static readonly MsgTypeOption All = new("All", "All");

        public MsgTypeOption(string code, string display)
        {
            Code = code;
            Display = display;
        }

        /// <summary>MsgType as stored on the message, e.g. "AE" — what the filter compares.</summary>
        public string Code { get; }

        /// <summary>Shown in the drop-down, e.g. "AE · TradeCaptureReport".</summary>
        public string Display { get; }
    }

    public partial class MessageLogEntryViewModel : ObservableObject
    {
        private readonly MessageLogEntry _entry;

        public MessageLogEntryViewModel(MessageLogEntry entry)
        {
            _entry = entry ?? throw new ArgumentNullException(nameof(entry));
        }

        public DateTime Timestamp => _entry.Timestamp;
        public string TimeFormatted => _entry.Timestamp.ToString("HH:mm:ss.fff");
        public MessageDirection Direction => _entry.Direction;
        public string DirectionText => _entry.Direction == MessageDirection.Incoming ? "IN" : "OUT";
        public string MsgType => _entry.MsgType;
        public string Summary => _entry.Summary;
        public string RawText => _entry.RawText;

        public string DirectionColor => _entry.Direction == MessageDirection.Incoming
            ? "#E3F2FD"
            : "#E8F5E9";
    }
}