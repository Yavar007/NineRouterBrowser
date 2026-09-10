using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;

namespace NineRouterBrowser;

/// <summary>
/// Single-purpose window that hosts the 9router web UI.
/// </summary>
public partial class MainWindow : Window
{
    private const string TargetUrl = "http://localhost:20128";
    private static readonly string PlacementFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "NineRouterBrowser", "placement.json");

    public MainWindow()
    {
        InitializeComponent();
        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.F5) { Refresh(); e.Handled = true; }
        };
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        RestorePlacement();
        try
        {
            await Browser.EnsureCoreWebView2Async();
            Browser.CoreWebView2.NewWindowRequested += (_, args) =>
            {
                // Keep everything inside this window — never open Edge/tabs.
                args.Handled = true;
                Browser.CoreWebView2.Navigate(args.Uri);
            };
            Browser.NavigationCompleted += (_, args) =>
            {
                OfflineOverlay.Visibility = args.IsSuccess
                    ? Visibility.Collapsed
                    : Visibility.Visible;
            };
            Browser.Source = new Uri(TargetUrl);
        }
        catch
        {
            OfflineOverlay.Visibility = Visibility.Visible;
        }
    }

    private void Refresh()
    {
        if (Browser.CoreWebView2 != null) Browser.Reload();
        else Browser.Source = new Uri(TargetUrl);
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e) => Refresh();

    private void RetryButton_Click(object sender, RoutedEventArgs e) => Refresh();

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PlacementFile)!);
            var placement = new WindowPlacement
            {
                Top = Top,
                Left = Left,
                Width = Width,
                Height = Height,
                State = WindowState.ToString()
            };
            File.WriteAllText(PlacementFile, JsonSerializer.Serialize(placement));
        }
        catch { /* placement is best-effort only */ }
    }

    private void RestorePlacement()
    {
        try
        {
            if (!File.Exists(PlacementFile)) return;
            var placement = JsonSerializer.Deserialize<WindowPlacement>(
                File.ReadAllText(PlacementFile));
            if (placement is null) return;
            if (placement.Width > 200) Width = placement.Width;
            if (placement.Height > 200) Height = placement.Height;
            Top = placement.Top; Left = placement.Left;
        }
        catch { /* start with defaults */ }
    }

    private sealed class WindowPlacement
    {
        public double Top { get; set; }
        public double Left { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
        public string State { get; set; } = "Normal";
    }
}