using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
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
        private const string IconUri = "pack://application:,,,/Resources/app.ico";

        private bool _shutdownCompleted;

        public MainWindow()
        {
            InitializeComponent();

            Icon = BitmapFrame.Create(new Uri(IconUri, UriKind.Absolute));
            TitleBarIcon.Source = LoadBestIconFrame(IconUri, preferredSize: 32);
        }

        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            // Initiera ViewModel när fönstret laddats
            if (DataContext is MainViewModel viewModel)
            {
                await viewModel.InitializeAsync();
            }
        }

        /// <summary>The .ico frame closest to <paramref name="preferredSize"/> pixels, for a sharp title bar icon.</summary>
        private static BitmapSource LoadBestIconFrame(string packUri, int preferredSize)
        {
            var decoder = new IconBitmapDecoder(
                new Uri(packUri, UriKind.Absolute),
                BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);

            return decoder.Frames
                .OrderBy(f => Math.Abs(f.PixelWidth - preferredSize))
                .First();
        }

        // ────────────────────────────────────
        // Custom title bar buttons
        // ────────────────────────────────────

        private void MinimizeButton_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        private void MaximizeButton_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
