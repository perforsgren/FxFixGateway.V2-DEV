using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using FxFixGateway.Domain.Enums;
using FxFixGateway.Domain.Interfaces;
using FxFixGateway.UI.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace FxFixGateway.UI
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        private const int DwmwaUseImmersiveDarkModeBefore20H1 = 19;
        private const int DwmwaUseImmersiveDarkMode = 20;

        private bool _shutdownCompleted;

        public MainWindow()
        {
            InitializeComponent();

            var iconUri = new Uri("pack://application:,,,/Resources/app.ico", UriKind.Absolute);
            Icon = System.Windows.Media.Imaging.BitmapFrame.Create(iconUri);
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            UseDarkTitleBar();
        }

        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            // Initiera ViewModel när fönstret laddats
            if (DataContext is MainViewModel viewModel)
            {
                await viewModel.InitializeAsync();
            }
        }

        /// <summary>
        /// Dark window title bar to match the dark theme (Windows 10 1809 and later; attribute 19
        /// before 20H1, 20 after). Purely cosmetic — ignored where not supported.
        /// </summary>
        private void UseDarkTitleBar()
        {
            try
            {
                var handle = new WindowInteropHelper(this).Handle;
                var enabled = 1;

                if (DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref enabled, sizeof(int)) != 0)
                    DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkModeBefore20H1, ref enabled, sizeof(int));
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Dark title bar not available");
            }
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    }
}
