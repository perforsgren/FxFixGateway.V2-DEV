using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FxFixGateway.Application.Services;
using FxFixGateway.Domain.Enums;
using FxFixGateway.Domain.Interfaces;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;

namespace FxFixGateway.UI.ViewModels
{
    public partial class SessionListViewModel : ObservableObject
    {
        private readonly SessionManagementService _sessionManagementService;
        private readonly IFixEngine _fixEngine;

        [ObservableProperty]
        private ObservableCollection<SessionViewModel> _sessions = new ObservableCollection<SessionViewModel>();

        [ObservableProperty]
        private SessionViewModel _selectedSession;

        [ObservableProperty]
        private string _searchText = string.Empty;

        [ObservableProperty]
        private bool _showOnlyEnabled = false;

        // Alias for XAML compatibility
        public string FilterText
        {
            get => SearchText;
            set => SearchText = value;
        }

        public int RunningSessionsCount => Sessions.Count(s =>
            s.Status == FxFixGateway.Domain.Enums.SessionStatus.LoggedOn ||
            s.Status == FxFixGateway.Domain.Enums.SessionStatus.Connecting ||
            s.Status == FxFixGateway.Domain.Enums.SessionStatus.Starting);

        /// <summary>Sessions that are logged on right now — for the summary card.</summary>
        public int LoggedOnCount => Sessions.Count(s => s.Status == SessionStatus.LoggedOn);

        /// <summary>Green when every enabled session is logged on, amber when some are, grey when none.</summary>
        public string SessionsStateColor
        {
            get
            {
                var loggedOn = LoggedOnCount;
                if (loggedOn == 0)
                    return "#64748B";

                var enabled = Sessions.Count(s => s.IsEnabled);
                return loggedOn >= enabled ? "#22C55E" : "#F59E0B";
            }
        }

        public event EventHandler<SessionViewModel?>? SessionSelected;

        public SessionListViewModel(
            SessionManagementService sessionManagementService,
            IFixEngine fixEngine)  // ← LÄGG TILL
        {
            _sessionManagementService = sessionManagementService ?? throw new ArgumentNullException(nameof(sessionManagementService));
            _fixEngine = fixEngine ?? throw new ArgumentNullException(nameof(fixEngine));  // ← LÄGG TILL
        }

        partial void OnSelectedSessionChanged(SessionViewModel? value)
        {
            SessionSelected?.Invoke(this, value);
        }

        public async Task LoadSessionsAsync()
        {
            try
            {
                var sessions = _sessionManagementService.GetAllSessions();

                foreach (var old in Sessions)
                    old.PropertyChanged -= OnSessionPropertyChanged;

                Sessions.Clear();
                foreach (var session in sessions)
                {
                    var viewModel = new SessionViewModel(session, _fixEngine);  // ← ANVÄND _fixEngine
                    viewModel.PropertyChanged += OnSessionPropertyChanged;
                    Sessions.Add(viewModel);
                }

                RaiseCountsChanged();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to load sessions: {ex.Message}");
                throw;
            }
        }

        // Keeps the summary card's counts current as sessions log on and off.
        private void OnSessionPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(SessionViewModel.Status) || e.PropertyName == nameof(SessionViewModel.IsEnabled))
                RaiseCountsChanged();
        }

        private void RaiseCountsChanged()
        {
            OnPropertyChanged(nameof(RunningSessionsCount));
            OnPropertyChanged(nameof(LoggedOnCount));
            OnPropertyChanged(nameof(SessionsStateColor));
        }

        [RelayCommand]
        private void AddSession()
        {
            // TODO: Implement add session dialog
            System.Windows.MessageBox.Show("Add session functionality not yet implemented");
        }

        [RelayCommand]
        private void FilterSessions()
        {
            // TODO: Implement filtering
        }

        partial void OnSearchTextChanged(string value)
        {
            FilterSessions();
        }
    }
}
