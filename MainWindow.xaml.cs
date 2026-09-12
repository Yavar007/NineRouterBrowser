using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Input;

namespace NineRouterBrowser;

/// <summary>
/// Single-purpose window that hosts the 9router web UI.
/// Starts plain "9router" itself, waits for the ready banner, then loads the page.
/// </summary>
public partial class MainWindow : Window
{
    private const string TargetUrl = "http://localhost:20128";

    /// <summary>Marker printed by 9router once the server is up.</summary>
    private const string ReadyMarker = "Server: http://localhost:20128";

    private static readonly string PlacementFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "NineRouterBrowser", "placement.json");

    private Process? _nineRouterProcess;
    private CancellationTokenSource? _shutdownCts;
    private readonly StringBuilder _processLog = new();

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
        _shutdownCts = new CancellationTokenSource();
        await BootAsync(_shutdownCts.Token);
    }

    /// <summary>Full boot sequence: detect → start → wait banner → show page.</summary>
    private async Task BootAsync(CancellationToken ct)
    {
        LoadingOverlay.Visibility = Visibility.Visible;
        OfflineOverlay.Visibility = Visibility.Collapsed;
        Browser.Visibility = Visibility.Collapsed;

        try
        {
            if (await IsServerUpAsync())
            {
                // 9router already running (e.g. started from cmd) — skip launching.
                SetLoadingStatus("9router is already running — connecting…");
            }
            else
            {
                // Start plain "9router" and wait for the ready banner.
                SetLoadingStatus("Launching 9router…");
                var started = StartNineRouterProcess();
                if (!started)
                {
                    ShowOffline("Could not find the 9router command on PATH.\nInstall it or start it manually, then retry.");
                    return;
                }

                var ready = await WaitForReadyBannerAsync(ct);
                if (!ready)
                {
                    ShowOffline("9router did not print the ready banner in time.\nCheck the log below, then retry.");
                    return;
                }
            }

            // Ensure WebView2 is ready, then load the page.
            SetLoadingStatus("Opening dashboard…");
            await Browser.EnsureCoreWebView2Async();
            Browser.CoreWebView2.NewWindowRequested += (_, args) =>
            {
                // Keep everything inside this window — never open Edge/tabs.
                args.Handled = true;
                Browser.CoreWebView2.Navigate(args.Uri);
            };
            Browser.NavigationCompleted += (_, args) =>
            {
                if (args.IsSuccess)
                {
                    Browser.Visibility = Visibility.Visible;
                    LoadingOverlay.Visibility = Visibility.Collapsed;
                    OfflineOverlay.Visibility = Visibility.Collapsed;
                }
                else
                {
                    ShowOffline("The page failed to load even though 9router seems up.\nPress retry.");
                }
            };
            Browser.Source = new Uri(TargetUrl);
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            ShowOffline($"Unexpected error: {ex.Message}");
        }
    }

    /// <summary>Quick ping — true if something already answers on :20128.</summary>
    private static async Task<bool> IsServerUpAsync()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            var resp = await http.GetAsync(TargetUrl);
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Starts plain "9router" (no arguments) with stdout/stderr captured.
    /// Returns false if the process could not be started.
    /// </summary>
    private bool StartNineRouterProcess()
    {
        try
        {
            // First, find the 9router executable via PATH or common locations
            string? nineRouterPath = FindNineRouterExecutable();
            if (string.IsNullOrEmpty(nineRouterPath))
            {
                AppendLog("[could not locate 9router executable]");
                ShowOffline("Could not find the 9router command on PATH.\nInstall it via: npm install -g 9router\nor start it manually, then retry.");
                return false;
            }

            AppendLog($"> Found 9router at: {nineRouterPath}");

            string fileName;
            string arguments;

            if (nineRouterPath.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase))
            {
                // PowerShell script → run through pwsh.
                fileName = "pwsh";
                arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{nineRouterPath}\" -NoBrowser";
            }
            else if (nineRouterPath.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) ||
                     nineRouterPath.EndsWith(".bat", StringComparison.OrdinalIgnoreCase))
            {
                // .cmd / .bat are NOT executable images — CreateProcess rejects them.
                // They must go through cmd.exe /c.
                fileName = "cmd.exe";
                arguments = $"/c \"\"{nineRouterPath}\" -NoBrowser\"";
            }
            else
            {
                // .exe (or an extensionless binary) — launch directly.
                fileName = nineRouterPath;
                arguments = string.Empty;
            }

            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };

            _nineRouterProcess = new Process { StartInfo = psi, EnableRaisingEvents = true };
            _nineRouterProcess.OutputDataReceived += (_, args) => OnProcessLine(args.Data);
            _nineRouterProcess.ErrorDataReceived += (_, args) => OnProcessLine(args.Data);
            _nineRouterProcess.Exited += (_, __) =>
            {
                AppendLog("[process exited]");
                _nineRouterProcess = null;
            };

            var ok = _nineRouterProcess.Start();
            if (!ok) return false;
            _nineRouterProcess.BeginOutputReadLine();
            _nineRouterProcess.BeginErrorReadLine();
            AppendLog($"> 9router (pid {_nineRouterProcess.Id})");
            return true;
        }
        catch (Exception ex)
        {
            AppendLog($"[failed to start: {ex.Message}]");
            return false;
        }
    }

    /// <summary>
    /// Looks for 9router via `where 9router` or common npm/pnpm/yarn locations.
    /// </summary>
    private static string? FindNineRouterExecutable()
    {
        // 1) `where` may return the POSIX shim (no extension) first — ignore it
        //    and prefer real Windows executables.
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "where",
                Arguments = "9router",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
            };
            using var where = Process.Start(psi);
            if (where is not null)
            {
                var output = where.StandardOutput.ReadToEnd();
                where.WaitForExit();

                var lines = output
                    .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(l => l.Trim())
                    .Where(File.Exists)
                    .ToList();

                // Prefer real Windows wrappers, in this order.
                foreach (var ext in new[] { ".cmd", ".exe", ".bat", ".ps1" })
                {
                    var match = lines.FirstOrDefault(l =>
                        l.EndsWith(ext, StringComparison.OrdinalIgnoreCase));
                    if (match is not null) return match;
                }

                // If we only got the bare shim, try its Windows siblings.
                foreach (var l in lines)
                {
                    if (string.IsNullOrEmpty(Path.GetExtension(l)))
                    {
                        foreach (var ext in new[] { ".cmd", ".exe", ".bat", ".ps1" })
                        {
                            var candidate = l + ext;
                            if (File.Exists(candidate)) return candidate;
                        }
                    }
                }
            }
        }
        catch { /* fall through */ }

        // 2) Common npm/pnpm/yarn global locations.
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var candidates = new[]
        {
        Path.Combine(appData, "npm", "9router.cmd"),
        Path.Combine(appData, "npm", "9router.ps1"),
        Path.Combine(localAppData, "pnpm", "9router.cmd"),
        Path.Combine(localAppData, "pnpm", "9router.ps1"),
        Path.Combine(appData, "yarn", "bin", "9router.cmd"),
        Path.Combine(appData, "yarn", "bin", "9router.ps1"),
    };
        foreach (var c in candidates)
            if (File.Exists(c)) return c;

        // 3) Local node_modules/.bin
        try
        {
            var projBin = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "node_modules", ".bin");
            foreach (var name in new[] { "9router.cmd", "9router.ps1" })
            {
                var p = Path.Combine(projBin, name);
                if (File.Exists(p)) return p;
            }
        }
        catch { }

        return null;
    }
    /// <summary>
    /// Helper to format string with argument.
    /// </summary>
    private static string FormatWith(string format, object arg)
        => string.Format(CultureInfo.InvariantCulture, format, arg);

    /// <summary>
    /// Waits until the captured output contains the ready banner,
    /// or the server answers HTTP, or 60s pass.
    /// </summary>
    private async Task<bool> WaitForReadyBannerAsync(CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        var deadline = DateTime.UtcNow.AddSeconds(60);

        while (!ct.IsCancellationRequested && DateTime.UtcNow < deadline)
        {
            // 1) Banner seen in stdout?
            lock (_processLog)
            {
                if (_processLog.ToString().Contains(ReadyMarker, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            // 2) Server already answering HTTP?
            try
            {
                var resp = await http.GetAsync(TargetUrl, ct);
                if (resp.IsSuccessStatusCode) return true;
            }
            catch { /* not yet */ }

            // 3) Process died early?
            if (_nineRouterProcess is null || _nineRouterProcess.HasExited)
            {
                AppendLog("[9router process exited before becoming ready]");
                return false;
            }

            SetLoadingStatus("Waiting for 9router to become ready…");
            await Task.Delay(500, ct);
        }

        return false;
    }

    private void OnProcessLine(string? line)
    {
        if (line is null) return;
        AppendLog(line);
        // If the banner arrived, reflect it on the UI immediately.
        if (line.Contains(ReadyMarker, StringComparison.OrdinalIgnoreCase))
            Dispatcher.Invoke(() => SetLoadingStatus("9router is ready — loading dashboard…"));
    }

    private void AppendLog(string line)
    {
        Dispatcher.Invoke(() =>
        {
            lock (_processLog) { _processLog.AppendLine(line); }
            LoadingLog.Text += line + Environment.NewLine;
        });
    }

    private void SetLoadingStatus(string text)
    {
        if (Dispatcher.CheckAccess()) LoadingStatus.Text = text;
        else Dispatcher.Invoke(() => LoadingStatus.Text = text);
    }

    private void ShowOffline(string detail)
    {
        Dispatcher.Invoke(() =>
        {
            LoadingOverlay.Visibility = Visibility.Collapsed;
            Browser.Visibility = Visibility.Collapsed;
            OfflineDetail.Text = detail + "\n\n--- process log ---\n" + LoadingLog.Text;
            OfflineOverlay.Visibility = Visibility.Visible;
        });
    }

    private void Refresh()
    {
        if (Browser.CoreWebView2 != null) Browser.Reload();
        else Browser.Source = new Uri(TargetUrl);
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e) => Refresh();

    private async void RetryButton_Click(object sender, RoutedEventArgs e)
    {
        // Reset state and run the whole boot again.
        try { if (_nineRouterProcess is { HasExited: false }) _nineRouterProcess.Kill(true); } catch { }
        _nineRouterProcess = null;
        lock (_processLog) { _processLog.Clear(); }
        LoadingLog.Text = string.Empty;
        _shutdownCts?.Cancel();
        _shutdownCts = new CancellationTokenSource();
        await BootAsync(_shutdownCts.Token);
    }
    private async void CopyErrorLogClick(object sender,RoutedEventArgs e)
    {
        Clipboard.SetData(DataFormats.Text, OfflineDetail.Text);
    }
    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _shutdownCts?.Cancel();
        try
        {
            if (_nineRouterProcess is { HasExited: false })
                _nineRouterProcess.Kill(true);
        }
        catch { }

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
