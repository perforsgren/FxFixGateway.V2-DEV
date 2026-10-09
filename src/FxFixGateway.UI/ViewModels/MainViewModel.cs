using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FxFixGateway.Application.Services;
using FxFixGateway.Domain.Interfaces;
using FxFixGateway.Infrastructure.PostMarker;
using Microsoft.Extensions.Logging;

namespace FxFixGateway.UI.ViewModels
{
    public partial class MainViewModel : ViewModelBase
    {
        // How often the GATEWAY card looks for a new log file (Serilog starts one at midnight and at 50 MB).
        private static readonly TimeSpan LogFileCheckInterval = TimeSpan.FromSeconds(30);

        private readonly SessionManagementService _sessionManagementService;
        private readonly ILogger<MainViewModel> _logger;
        private readonly DispatcherTimer _logFileTimer;

        [ObservableProperty]
        private SessionListViewModel _sessionList;

        [ObservableProperty]
        private SessionDetailViewModel _sessionDetail;

        [ObservableProperty]
        private string _statusBarText = "Ready";

        [ObservableProperty]
        private bool _isLoading;

        /// <summary>True while the PostMarker panel is shown instead of a FIX session's details.</summary>
        [ObservableProperty]
        private bool _isPostMarkerSelected;

        /// <summary>"Log: logs\gateway-20261009.txt" — the file Serilog is writing to now.</summary>
        [ObservableProperty]
        private string _logFileText = "Log: –";

        /// <summary>Full path of the current log file (tooltip).</summary>
        [ObservableProperty]
        private string _logFilePath = string.Empty;

        public MainViewModel(
            SessionManagementService sessionManagementService,
            SessionListViewModel sessionListViewModel,
            IMessageLogger messageLogger,
            IAckQueueRepository ackQueueRepository,
            IFixEngine fixEngine,
            PostMarkerIngestService postMarkerService,
            ILogger<MainViewModel> logger)
        {
            _sessionManagementService = sessionManagementService ?? throw new ArgumentNullException(nameof(sessionManagementService));
            _sessionList = sessionListViewModel ?? throw new ArgumentNullException(nameof(sessionListViewModel));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            _sessionDetail = new SessionDetailViewModel(
                _sessionManagementService,
                messageLogger,
                ackQueueRepository,
                fixEngine);

            PostMarker = new PostMarkerViewModel(postMarkerService);

            // ÄNDRAT: Använd SessionSelected event istället för PropertyChanged
            _sessionList.SessionSelected += OnSessionSelected;

            UpdateLogFile();
            _logFileTimer = new DispatcherTimer { Interval = LogFileCheckInterval };
            _logFileTimer.Tick += (s, e) => UpdateLogFile();
            _logFileTimer.Start();
        }

        private static string LogFolder => Path.Combine(Directory.GetCurrentDirectory(), "logs");

        /// <summary>The PostMarker panel and the PostMarker card in the connection list.</summary>
        public PostMarkerViewModel PostMarker { get; }

        /// <summary>"p901pef · WS68447" — who and where the gateway runs.</summary>
        public string MachineText => $"{Environment.UserName} · {Environment.MachineName}";

        /// <summary>"Since 08:15" (today) or "Since Fri 08:15".</summary>
        public string StartedText
        {
            get
            {
                using var process = Process.GetCurrentProcess();
                var started = process.StartTime;
                return started.Date == DateTime.Today
                    ? $"Since {started:HH:mm}"
                    : $"Since {started:ddd HH:mm}";
            }
        }

        public async Task InitializeAsync()
        {
            try
            {
                IsLoading = true;
                StatusBarText = "Loading sessions...";

                _logger.LogInformation("Initializing Main ViewModel...");

                await _sessionManagementService.InitializeAsync();
                await _sessionList.LoadSessionsAsync();

                var sessionCount = _sessionList.Sessions.Count;
                var runningCount = _sessionList.RunningSessionsCount;

                StatusBarText = $"{sessionCount} sessions loaded, {runningCount} running";

                _logger.LogInformation("Main ViewModel initialized successfully");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize Main ViewModel");
                StatusBarText = "Error loading sessions";
            }
            finally
            {
                IsLoading = false;
            }
        }

        // ÄNDRAT: Ta emot event från SessionListViewModel
        private void OnSessionSelected(object? sender, SessionViewModel? session)
        {
            SessionDetail.SelectedSession = session;

            if (session != null)
                IsPostMarkerSelected = false;
        }

        /// <summary>Shows the PostMarker panel and clears the FIX session selection.</summary>
        [RelayCommand]
        private void SelectPostMarker()
        {
            IsPostMarkerSelected = true;
            SessionList.SelectedSession = null!;
        }

        [RelayCommand]
        private async Task RefreshAll()
        {
            await _sessionList.LoadSessionsAsync();
            StatusBarText = $"Refreshed at {DateTime.Now:HH:mm:ss}";
        }

        /// <summary>
        /// Shows the log file Serilog writes to now: the most recently written gateway-*.txt in
        /// logs\ (SerilogConfiguration rolls daily, gateway-20261009.txt, and at 50 MB, gateway-20261009_001.txt).
        /// </summary>
        private void UpdateLogFile()
        {
            try
            {
                var folder = new DirectoryInfo(LogFolder);
                var current = folder.Exists
                    ? folder.EnumerateFiles("gateway-*.txt").OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault()
                    : null;

                LogFileText = current != null ? $"Log: logs\\{current.Name}" : "Log: no log file yet";
                LogFilePath = current?.FullName ?? folder.FullName;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not read the log folder");
            }
        }

        /// <summary>Opens the gateway's log folder (logs\ under the working directory, see SerilogConfiguration).</summary>
        [RelayCommand]
        private void OpenLogFolder()
        {
            var folder = LogFolder;

            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not open log folder {Folder}", folder);
                StatusBarText = $"Log folder: {folder}";
            }
        }

        [RelayCommand]
        private void Exit()
        {
            _logger.LogInformation("Application exit requested");
            System.Windows.Application.Current.Shutdown();
        }
    }
}
