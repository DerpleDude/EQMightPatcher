using System.Collections.ObjectModel;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Input;

namespace EQMightPatcher;

public partial class MainWindow : Window
{
    private readonly PatcherSettings _settings;
    private readonly PatcherService _service = new();
    private readonly ObservableCollection<string> _logEntries = [];
    private CancellationTokenSource? _cts;
    private bool _patchComplete = false;

    public MainWindow()
    {
        InitializeComponent();
        _settings = PatcherSettings.Load();
        EQDirTextBox.Text = _settings.EQDirectory;
        LogItems.ItemsSource = _logEntries;
        UpdateButtonState();
        _ = RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        PatchNotesText.Text = "Loading...";
        ActionButton.IsEnabled = false;

        var eqDir = _settings.EQDirectory;
        if (!Directory.Exists(eqDir))
        {
            PatchNotesText.Text = "No patch notes available — set your EQ directory first.";
            return;
        }

        var (hasNew, log) = await Task.Run(() => _service.FetchAndCheck(eqDir));

        PatchNotesText.Text = log;
        ActionButton.Content = hasNew ? "Update Now!" : "Up to date!";
        ActionButton.IsEnabled = hasNew;
        if (!hasNew) _patchComplete = true;
        PlaceholderButton1.IsEnabled = _patchComplete && Directory.Exists(_settings.EQDirectory);
    }

    private void UpdateButtonState()
    {
        ActionButton.IsEnabled = Directory.Exists(_settings.EQDirectory);
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
            DragMove();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        Close();
    }

    private void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        using var dlg = new FolderBrowserDialog
        {
            Description = "Select your EverQuest directory",
            SelectedPath = Directory.Exists(_settings.EQDirectory) ? _settings.EQDirectory : "",
        };
        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            EQDirTextBox.Text = dlg.SelectedPath;
            _settings.EQDirectory = dlg.SelectedPath;
            _settings.Save();
            _ = RefreshAsync();
        }
    }

    private void EQDirTextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        _settings.EQDirectory = EQDirTextBox.Text;
        _settings.Save();
        _patchComplete = false;
        PlaceholderButton1.IsEnabled = false;
        UpdateButtonState();
    }

    private void AppendLog(string message)
    {
        _logEntries.Add(message);
        LogScroller.ScrollToBottom();
    }

    private async void ActionButton_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();

        ActionButton.IsEnabled = false;
        ProgressBar.Value = 0;
        StatusText.Text = "";
        _logEntries.Clear();

        var progress = new Progress<(double Percent, string Status)>(report =>
        {
            ProgressBar.Value = report.Percent;
            StatusText.Text = report.Status;
        });

        var log = new Progress<string>(AppendLog);

        try
        {
            await _service.SyncAndPatch(_settings.EQDirectory, progress, log, _cts.Token);
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Cancelled.";
            AppendLog("Cancelled.");
            ProgressBar.Value = 0;
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Error: {ex.Message}";
            AppendLog($"[error] {ex.Message}");
            ProgressBar.Value = 0;
        }
        finally
        {
            await RefreshAsync();
        }
    }

    private void DerpdogLink_Click(object sender, MouseButtonEventArgs e)
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "https://github.com/DerpleDude",
            UseShellExecute = true,
        });
    }

    private void PlaceholderButton1_Click(object sender, RoutedEventArgs e)
    {
        var eqDir = _settings.EQDirectory;
        if (!Directory.Exists(eqDir))
        {
            StatusText.Text = "EQ directory not set.";
            return;
        }
        var exe = Path.Combine(eqDir, "eqgame.exe");
        if (!File.Exists(exe))
        {
            StatusText.Text = "eqgame.exe not found in EQ directory.";
            return;
        }
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = exe,
            Arguments = "patchme",
            WorkingDirectory = eqDir,
            UseShellExecute = true,
        });
    }
    private async void PlaceholderButton2_Click(object sender, RoutedEventArgs e)
    {
        var eqDir = _settings.EQDirectory;
        if (!Directory.Exists(eqDir))
        {
            StatusText.Text = "EQ directory not set.";
            return;
        }

        PlaceholderButton2.IsEnabled = false;
        var patchDir = Path.Combine(eqDir, "4gbPatch");
        var zipPath = Path.Combine(patchDir, "4gb_patch.zip");

        try
        {
            Directory.CreateDirectory(patchDir);
            StatusText.Text = "Downloading 4GB Patch...";
            AppendLog("Downloading 4gb_patch.zip...");

            using var http = new HttpClient();
            var bytes = await http.GetByteArrayAsync("https://ntcore.com/files/4gb_patch.zip");
            await File.WriteAllBytesAsync(zipPath, bytes);
            AppendLog($"  Downloaded {bytes.Length / 1024} KB");

            StatusText.Text = "Extracting...";
            AppendLog("Extracting...");
            ZipFile.ExtractToDirectory(zipPath, patchDir, overwriteFiles: true);
            AppendLog($"  Extracted to {patchDir}");

            var exe = Directory.EnumerateFiles(patchDir, "*.exe", SearchOption.TopDirectoryOnly).FirstOrDefault();
            if (exe == null)
            {
                StatusText.Text = "Extracted but no .exe found.";
                AppendLog("  [warn] no .exe found in extracted contents");
                return;
            }

            var eqExe = Path.Combine(eqDir, "eqgame.exe");
            AppendLog($"  Launching {Path.GetFileName(exe)} with {eqExe}");
            StatusText.Text = "Running 4GB Patch...";
            var proc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = exe,
                Arguments = $"\"{eqExe}\"",
                WorkingDirectory = patchDir,
                UseShellExecute = true,
            });
            if (proc != null)
            {
                await proc.WaitForExitAsync();
                if (proc.ExitCode == 0)
                {
                    AppendLog("  4GB patch applied successfully.");
                    System.Windows.MessageBox.Show("Patch Successful!", "EQ Might Patcher", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    AppendLog($"  [warn] process exited with code {proc.ExitCode}");
                }
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Error: {ex.Message}";
            AppendLog($"  [error] {ex.Message}");
        }
        finally
        {
            PlaceholderButton2.IsEnabled = true;
        }
    }
    private void PlaceholderButton3_Click(object sender, RoutedEventArgs e)
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "https://www.redguides.com/community/threads/aquietones-guide-to-emu-stability.91839/",
            UseShellExecute = true,
        });
    }
}
