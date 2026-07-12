using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Principal;
using System.Windows;
using System.Windows.Input;
using WinISOBuilder.Models;
using WinISOBuilder.Services;

namespace WinISOBuilder.ViewModels;

public class MainViewModel : INotifyPropertyChanged
{
    private readonly DismService _dism = new();
    private const double IsoLoadWeight = 1.0;
    private const double DriverScanWeight = 1.0;
    private const double BuildPrepWeight = 0.15;
    private const double BuildInjectWeight = 0.55;
    private const double BuildIsoWeight = 0.30;

    private string _isoPath = "";
    private string _sourceTypeText = "ISO file";
    private string _driverPath = "";
    private string _outputPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), $"WinISO_Custom_{DateTime.Now:yyyyMMdd_HHmmss}.iso");
    private string _statusText = "Ready";
    private string _statusDetail = "Waiting for input";
    private string _isoInfo = "";
    private string _editionInfo = "";
    private string _dialogTitle = "";
    private string _dialogMessage = "";
    private string _dialogPrimaryText = "OK";
    private string _dialogSecondaryText = "";
    private string _unattendedUserName = "ADMIN";
    private string _selectedTimeZone = "GMT+7 - SE Asia Standard Time";
    private string _logText = "[Ready] Waiting for input...";
    private bool _isBusy;
    private bool _isBuildCompleted;
    private bool _isoReady;
    private bool _driversReady;
    private bool _isDialogOpen;
    private bool _isConfirmationDialog;
    private bool _isAdvancedMode = true;
    private bool _showOnboarding = true;
    private bool _isAdmin;
    private bool _isDismReady;
    private bool _isAdkReady;
    private bool _isUnattendedEnabled;
    private double _progress;
    private int _selectedCount;
    private int _buildProgressTicks;
    private int _buildProgressTotalTicks;
    private CancellationTokenSource? _buildCancellation;

    public string IsoPath { get => _isoPath; set { _isoPath = value; OnPropertyChanged(); } }
    public string SourceTypeText { get => _sourceTypeText; set { _sourceTypeText = value; OnPropertyChanged(); } }
    public string DriverPath { get => _driverPath; set { _driverPath = value; OnPropertyChanged(); DriversReady = false; DriverCount = 0; RefreshReadiness(); } }
    public string OutputPath { get => _outputPath; set { _outputPath = value; OnPropertyChanged(); RefreshReadiness(); } }
    public string StatusText { get => _statusText; set { _statusText = value; OnPropertyChanged(); } }
    public string StatusDetail { get => _statusDetail; set { _statusDetail = value; OnPropertyChanged(); } }
    public string IsoInfo { get => _isoInfo; set { _isoInfo = value; OnPropertyChanged(); } }
    public string EditionInfo { get => _editionInfo; set { _editionInfo = value; OnPropertyChanged(); } }
    public string DialogTitle { get => _dialogTitle; set { _dialogTitle = value; OnPropertyChanged(); } }
    public string DialogMessage { get => _dialogMessage; set { _dialogMessage = value; OnPropertyChanged(); } }
    public string DialogPrimaryText { get => _dialogPrimaryText; set { _dialogPrimaryText = value; OnPropertyChanged(); } }
    public string DialogSecondaryText { get => _dialogSecondaryText; set { _dialogSecondaryText = value; OnPropertyChanged(); } }
    public string UnattendedUserName { get => _unattendedUserName; set { _unattendedUserName = value; OnPropertyChanged(); OnPropertyChanged(nameof(BuildSummary)); } }
    public string SelectedTimeZone { get => _selectedTimeZone; set { _selectedTimeZone = value; OnPropertyChanged(); OnPropertyChanged(nameof(UnattendedTimeZoneId)); OnPropertyChanged(nameof(BuildSummary)); } }
    public string LogText { get => _logText; set { _logText = value; OnPropertyChanged(); } }
    public bool IsBusy { get => _isBusy; set { _isBusy = value; OnPropertyChanged(); } }
    public bool IsBuildCompleted { get => _isBuildCompleted; set { _isBuildCompleted = value; OnPropertyChanged(); } }
    public bool IsoReady { get => _isoReady; set { _isoReady = value; OnPropertyChanged(); } }
    public bool DriversReady { get => _driversReady; set { _driversReady = value; OnPropertyChanged(); } }
    public bool IsDialogOpen { get => _isDialogOpen; set { _isDialogOpen = value; OnPropertyChanged(); } }
    public bool IsConfirmationDialog { get => _isConfirmationDialog; set { _isConfirmationDialog = value; OnPropertyChanged(); } }
    public bool IsAdvancedMode { get => _isAdvancedMode; set { _isAdvancedMode = value; OnPropertyChanged(); OnPropertyChanged(nameof(ModeText)); } }
    public bool ShowOnboarding { get => _showOnboarding; set { _showOnboarding = value; OnPropertyChanged(); } }
    public bool IsAdmin { get => _isAdmin; set { _isAdmin = value; OnPropertyChanged(); OnPropertyChanged(nameof(AdminStatusColor)); } }
    public bool IsDismReady { get => _isDismReady; set { _isDismReady = value; OnPropertyChanged(); OnPropertyChanged(nameof(DismStatusColor)); } }
    public bool IsAdkReady { get => _isAdkReady; set { _isAdkReady = value; OnPropertyChanged(); OnPropertyChanged(nameof(AdkStatusColor)); } }
    public bool IsUnattendedEnabled { get => _isUnattendedEnabled; set { _isUnattendedEnabled = value; OnPropertyChanged(); OnPropertyChanged(nameof(UnattendedStatusText)); OnPropertyChanged(nameof(BuildSummary)); } }
    public double Progress { get => _progress; set { _progress = value; OnPropertyChanged(); } }
    public string ProgressPercentText => $"{Math.Round(Progress * 100):0}%";
    public string BuildButtonText => IsBusy ? "BUILDING..." : IsBuildCompleted ? "DONE" : "RUN AND BUILD";
    public bool CanCancelBuild => IsBusy && _buildCancellation != null && !_buildCancellation.IsCancellationRequested;
    public string ModeText => IsAdvancedMode ? "ADVANCED" : "SIMPLE";
    public string SourceStepStatus => IsoReady ? "READY" : "WAITING";
    public string DriverStepStatus => DriversReady ? "READY" : "WAITING";
    public string BuildStepStatus => IsBuildCompleted ? "DONE" : CanBuild ? "READY" : "BLOCKED";
    public string SourceReadyText => IsoReady ? "Source selected" : "Select ISO or extracted source";
    public string EditionsReadyText => Editions.Any(e => e.IsSelected) ? $"{SelectedCount} edition(s) selected" : "Select at least one edition";
    public string DriversReadyText => DriversReady ? $"Driver folder valid ({DriverCount} .inf)" : "Select a driver folder";
    public string OutputReadyText => !string.IsNullOrWhiteSpace(OutputPath) ? "Output path configured" : "Choose output ISO path";
    public string ToolchainReadyText => "Admin / DISM / ADK checks available during build";
    public string AdminStatusColor => IsAdmin ? "#198754" : "#dc3545";
    public string DismStatusColor => IsDismReady ? "#198754" : "#dc3545";
    public string AdkStatusColor => IsAdkReady ? "#198754" : "#dc3545";
    public string UnattendedStatusText => IsUnattendedEnabled ? "LAB MODE" : "OFF";
    public string UnattendedTimeZoneId => TimeZoneOptions.TryGetValue(SelectedTimeZone, out var id) ? id : "SE Asia Standard Time";
    public string BuildSummary =>
        $"Source: {(IsoReady ? SourceTypeText : "Not selected")} | Editions: {SelectedCount} | Drivers: {DriverCount} | Unattended: {UnattendedStatusText} ({UnattendedUserName}, {UnattendedTimeZoneId}) | Output: {(string.IsNullOrWhiteSpace(OutputPath) ? "Not selected" : Path.GetFileName(OutputPath))}";
    public int SelectedCount { get => _selectedCount; set { _selectedCount = value; OnPropertyChanged(); } }
    public ObservableCollection<EditionItem> Editions { get; } = [];
    public ObservableCollection<string> TimeZones { get; } = ["GMT+7 - SE Asia Standard Time", "UTC - UTC", "GMT+8 - Singapore Standard Time", "GMT+9 - Tokyo Standard Time", "US Pacific - Pacific Standard Time", "US Eastern - Eastern Standard Time"];
    private static readonly Dictionary<string, string> TimeZoneOptions = new()
    {
        ["GMT+7 - SE Asia Standard Time"] = "SE Asia Standard Time",
        ["UTC - UTC"] = "UTC",
        ["GMT+8 - Singapore Standard Time"] = "Singapore Standard Time",
        ["GMT+9 - Tokyo Standard Time"] = "Tokyo Standard Time",
        ["US Pacific - Pacific Standard Time"] = "Pacific Standard Time",
        ["US Eastern - Eastern Standard Time"] = "Eastern Standard Time"
    };

    public bool CanBuild =>
        IsoReady
        && DriversReady
        && DriverCount > 0
        && IsAdmin
        && IsDismReady
        && IsAdkReady
        && !IsBusy
        && Editions.Any(e => e.IsSelected)
        && !string.IsNullOrWhiteSpace(OutputPath);

    public ICommand BrowseDriverCommand { get; }
    public ICommand BrowseExtractedFolderCommand { get; }
    public ICommand BrowseIsoCommand { get; }
    public ICommand BuildCommand { get; }
    public ICommand CancelBuildCommand { get; }
    public ICommand BrowseOutputCommand { get; }
    public ICommand ClearLogCommand { get; }
    public ICommand CopyLogCommand { get; }
    public ICommand OpenLogFolderCommand { get; }
    public ICommand OpenAdminHelpCommand { get; }
    public ICommand OpenDismHelpCommand { get; }
    public ICommand OpenAdkDownloadCommand { get; }
    public ICommand ToggleModeCommand { get; }
    public ICommand CloseOnboardingCommand { get; }
    public ICommand SelectAllCommand { get; }
    public ICommand DeselectAllCommand { get; }
    public ICommand RemoveEditionCommand { get; }
    public ICommand CloseDialogCommand { get; }
    public ICommand CancelDialogCommand { get; }

    public MainViewModel()
    {
        BrowseExtractedFolderCommand = new RelayCommand(async _ => await BrowseExtractedFolderAsync());
        BrowseIsoCommand = new RelayCommand(async _ => await BrowseIsoAsync());
        BrowseDriverCommand = new RelayCommand(_ => BrowseDriver());
        BuildCommand = new RelayCommand(async _ => await BuildAsync(), _ => CanBuild);
        CancelBuildCommand = new RelayCommand(_ => CancelBuild(), _ => CanCancelBuild);
        BrowseOutputCommand = new RelayCommand(_ => BrowseOutput());
        ClearLogCommand = new RelayCommand(_ => LogText = "");
        CopyLogCommand = new RelayCommand(_ => { if (!string.IsNullOrWhiteSpace(LogText)) Clipboard.SetText(LogText); });
        OpenLogFolderCommand = new RelayCommand(_ =>
        {
            Directory.CreateDirectory(_dism.LogRoot);
            Process.Start(new ProcessStartInfo { FileName = _dism.LogRoot, UseShellExecute = true });
        });
        OpenAdminHelpCommand = new RelayCommand(_ => OpenUrl("https://support.microsoft.com/windows/run-as-administrator"));
        OpenDismHelpCommand = new RelayCommand(_ => OpenUrl("https://learn.microsoft.com/windows-hardware/manufacture/desktop/dism-reference--deployment-image-servicing-and-management"));
        OpenAdkDownloadCommand = new RelayCommand(_ => OpenUrl("https://learn.microsoft.com/windows-hardware/get-started/adk-install"));
        ToggleModeCommand = new RelayCommand(_ => IsAdvancedMode = !IsAdvancedMode);
        CloseOnboardingCommand = new RelayCommand(_ => ShowOnboarding = false);
        SelectAllCommand = new RelayCommand(_ => SetAllSelected(true));
        DeselectAllCommand = new RelayCommand(_ => SetAllSelected(false), _ => Editions.Count > 0);
        RemoveEditionCommand = new RelayCommand(RemoveEdition, _ => Editions.Count > 1 && !IsBusy);
        CloseDialogCommand = new RelayCommand(async _ => await ConfirmDialogAsync());
        CancelDialogCommand = new RelayCommand(_ => CloseDialog());
        PropertyChanged += (_, _) => Application.Current.Dispatcher.Invoke(() => CommandManager.InvalidateRequerySuggested());
        RefreshToolchainStatus();
    }

    private void RefreshToolchainStatus()
    {
        IsAdmin = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
        IsDismReady = FindTool("dism.exe") != null;
        IsAdkReady = FindTool("oscdimg.exe") != null || File.Exists(@"C:\Program Files (x86)\Windows Kits\10\Assessment and Deployment Kit\Deployment Tools\amd64\Oscdimg\oscdimg.exe");
    }

    private static string? FindTool(string fileName)
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathEnv.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var fullPath = Path.Combine(dir, fileName);
            if (File.Exists(fullPath))
                return fullPath;
        }
        return null;
    }

    private static void OpenUrl(string url)
    {
        Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
    }

    private void ShowDialog(string title, string message)
    {
        DialogTitle = title;
        DialogMessage = message;
        DialogPrimaryText = "OK";
        DialogSecondaryText = "";
        IsConfirmationDialog = false;
        IsDialogOpen = true;
        AppendLog($"[{title}] {message}");
    }

    private TaskCompletionSource<bool>? _dialogCompletion;

    private Task<bool> ShowConfirmationAsync(string title, string message, string primaryText, string secondaryText)
    {
        DialogTitle = title;
        DialogMessage = message;
        DialogPrimaryText = primaryText;
        DialogSecondaryText = secondaryText;
        IsConfirmationDialog = true;
        IsDialogOpen = true;
        _dialogCompletion = new TaskCompletionSource<bool>();
        return _dialogCompletion.Task;
    }

    private async Task ConfirmDialogAsync()
    {
        if (!IsConfirmationDialog)
        {
            CloseDialog();
            return;
        }

        _dialogCompletion?.TrySetResult(true);
        CloseDialog();
        await Task.CompletedTask;
    }

    private void CloseDialog()
    {
        _dialogCompletion?.TrySetResult(false);
        _dialogCompletion = null;
        IsDialogOpen = false;
        IsConfirmationDialog = false;
    }

    private void AppendLog(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        LogText = string.IsNullOrWhiteSpace(LogText)
            ? line
            : $"{LogText}{Environment.NewLine}{line}";
    }

    private void RemoveEdition(object? parameter)
    {
        if (parameter is not EditionItem edition || Editions.Count <= 1)
            return;

        edition.PropertyChanged -= Edition_PropertyChanged;
        Editions.Remove(edition);
        RefreshSelectedCount();
        StatusText = $"Excluded edition: {edition.Name}";
        StatusDetail = $"Remaining editions: {Editions.Count}";
        AppendLog($"Excluded edition from output ISO: {edition.DisplayText}");
    }

    private void Edition_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(EditionItem.IsSelected))
            RefreshSelectedCount();
    }

    private void SetAllSelected(bool selected)
    {
        foreach (var e in Editions)
            e.IsSelected = selected;
        RefreshSelectedCount();
    }

    private void RefreshSelectedCount()
    {
        SelectedCount = Editions.Count(e => e.IsSelected);
        RefreshReadiness();
    }

    private void RefreshReadiness()
    {
        OnPropertyChanged(nameof(CanBuild));
        OnPropertyChanged(nameof(SourceStepStatus));
        OnPropertyChanged(nameof(DriverStepStatus));
        OnPropertyChanged(nameof(BuildStepStatus));
        OnPropertyChanged(nameof(SourceReadyText));
        OnPropertyChanged(nameof(EditionsReadyText));
        OnPropertyChanged(nameof(DriversReadyText));
        OnPropertyChanged(nameof(OutputReadyText));
        OnPropertyChanged(nameof(BuildSummary));
    }

    private void OnProgressChanged()
    {
        OnPropertyChanged(nameof(ProgressPercentText));
    }

    private void OnBusyChanged()
    {
        OnPropertyChanged(nameof(BuildButtonText));
        OnPropertyChanged(nameof(CanCancelBuild));
    }

    private void SetProgress(double value)
    {
        Progress = Math.Max(0, Math.Min(1, value));
    }

    private void AdvanceBuildProgress()
    {
        if (_buildProgressTotalTicks <= 0)
            return;

        _buildProgressTicks = Math.Min(_buildProgressTicks + 1, _buildProgressTotalTicks);
        var ratio = (double)_buildProgressTicks / _buildProgressTotalTicks;
        SetProgress(BuildPrepWeight + (ratio * BuildInjectWeight));
    }

    private async Task BrowseIsoAsync()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select Windows ISO",
            Filter = "ISO files (*.iso)|*.iso",
            DefaultExt = ".iso"
        };
        if (dlg.ShowDialog() != true) return;

        IsoPath = dlg.FileName;
        SourceTypeText = "ISO file";
        IsBusy = true;
        IsBuildCompleted = false;
        SetProgress(0);
        IsoReady = false;
        LogText = "";
        AppendLog($"Selected ISO source: {IsoPath}");

        try
        {
            var info = await _dism.GetImageInfoAsync(IsoPath, msg =>
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    StatusText = msg;
                    StatusDetail = "Reading image metadata and available editions";
                    SetProgress(msg.Contains("Extracting", StringComparison.OrdinalIgnoreCase) ? 0.2 : 0.75);
                    AppendLog(msg);
                });
            });

            Application.Current.Dispatcher.Invoke(() =>
            {
                IsoInfo = info.ImageName;
                EditionInfo = info.Architecture;
                Editions.Clear();
                foreach (var e in info.Editions)
                {
                    e.PropertyChanged += Edition_PropertyChanged;
                    Editions.Add(e);
                }
                RefreshSelectedCount();
                IsoReady = true;
                SetProgress(IsoLoadWeight);
                StatusText = $"Loaded {Editions.Count} edition(s). Exclude any edition you do not want in the output ISO.";
                StatusDetail = "Source is ready for driver injection";
                AppendLog($"Loaded {Editions.Count} edition(s) from ISO source.");
            });
        }
        catch (Exception ex)
        {
            StatusText = $"Error: {ex.Message}";
            StatusDetail = "The source could not be opened";
            ShowDialog("ISO Error", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task BrowseExtractedFolderAsync()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Select extracted Windows source folder"
        };
        if (dlg.ShowDialog() != true) return;

        IsoPath = dlg.FolderName;
        SourceTypeText = "Extracted folder";
        IsBusy = true;
        IsBuildCompleted = false;
        SetProgress(0);
        IsoReady = false;
        LogText = "";
        AppendLog($"Selected extracted source: {IsoPath}");

        try
        {
            var info = await _dism.GetImageInfoFromExtractedFolderAsync(IsoPath, msg =>
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    StatusText = msg;
                    StatusDetail = "Checking extracted source structure";
                    SetProgress(msg.Contains("Validating", StringComparison.OrdinalIgnoreCase) ? 0.25 : 0.75);
                    AppendLog(msg);
                });
            });

            Application.Current.Dispatcher.Invoke(() =>
            {
                IsoInfo = info.ImageName;
                EditionInfo = info.Architecture;
                Editions.Clear();
                foreach (var e in info.Editions)
                {
                    e.PropertyChanged += Edition_PropertyChanged;
                    Editions.Add(e);
                }
                RefreshSelectedCount();
                IsoReady = true;
                SetProgress(IsoLoadWeight);
                StatusText = $"Loaded {Editions.Count} edition(s) from extracted folder. Exclude any edition you do not want in the output ISO.";
                StatusDetail = "Extracted source is ready for driver injection";
                AppendLog($"Loaded {Editions.Count} edition(s) from extracted folder.");
            });
        }
        catch (Exception ex)
        {
            StatusText = $"Error: {ex.Message}";
            StatusDetail = "The extracted folder is not valid";
            ShowDialog("Source Folder Error", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void BrowseDriver()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Select folder containing drivers (.inf files)"
        };
        if (dlg.ShowDialog() != true) return;

        DriverPath = dlg.FolderName;

        SetProgress(0.1);
        StatusText = "Scanning driver folder...";
        StatusDetail = "Looking for .inf files in the selected folder";
        AppendLog($"Scanning driver folder: {DriverPath}");

        var infCount = 0;
        try { infCount = Directory.EnumerateFiles(DriverPath, "*.inf", SearchOption.AllDirectories).Count(); }
        catch { /* ignore */ }

        DriverCount = infCount;
        DriversReady = infCount > 0;
        SetProgress(DriverScanWeight);
        StatusText = $"Found {infCount} driver(s) in selected folder.";
        StatusDetail = infCount > 0
            ? "Driver folder is ready"
            : "No .inf driver files were found in this folder";
        AppendLog($"Found {infCount} driver(s).");
        RefreshReadiness();
    }

    private void BrowseOutput()
    {
        var saveDlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Save Custom ISO As",
            Filter = "ISO files (*.iso)|*.iso",
            DefaultExt = ".iso",
            FileName = string.IsNullOrWhiteSpace(OutputPath) ? $"WinISO_Custom_{DateTime.Now:yyyyMMdd_HHmmss}.iso" : Path.GetFileName(OutputPath),
            InitialDirectory = Directory.Exists(Path.GetDirectoryName(OutputPath)) ? Path.GetDirectoryName(OutputPath) : Environment.GetFolderPath(Environment.SpecialFolder.Desktop)
        };
        if (saveDlg.ShowDialog() == true)
        {
            OutputPath = saveDlg.FileName;
            AppendLog($"Output ISO path: {OutputPath}");
        }
    }

    private int _driverCount;
    public int DriverCount { get => _driverCount; set { _driverCount = value; OnPropertyChanged(); } }

    private async Task BuildAsync()
    {
        if (string.IsNullOrEmpty(IsoPath) || string.IsNullOrEmpty(DriverPath) || !Editions.Any(e => e.IsSelected))
            return;

        if (!ValidateBeforeBuild(out var validationError))
        {
            ShowDialog("Build blocked", validationError);
            return;
        }

        if (IsUnattendedEnabled)
        {
            var userName = UnattendedUserName.Trim();
            if (!IsValidUserName(userName))
            {
                ShowDialog("Invalid account name", "Use 1-20 letters/numbers only. Reserved names like Administrator, Guest, DefaultAccount, and WDAGUtilityAccount are not allowed.");
                return;
            }

            var accepted = await ShowConfirmationAsync(
                "Lab Mode confirmation",
                $"Security-sensitive unattended settings are enabled:\n\n- Creates local administrator account {userName} without password\n- Sets password to never expire\n- Time zone: {UnattendedTimeZoneId}\n- Disables UAC\n- Prevents automatic device encryption\n\nOnly use this ISO in trusted lab or internal deployment environments.",
                "OK",
                "CANCEL");

            if (!accepted)
            {
                AppendLog("Build cancelled by user before applying Lab Mode unattended setup.");
                return;
            }
        }

        var selected = Editions.Where(e => e.IsSelected).ToList();
        var removed = Editions.Count - selected.Count;

        var outputPath = OutputPath;

        IsBusy = true;
        IsBuildCompleted = false;
        SetProgress(0);
        StatusText = removed > 0
            ? $"Starting... ({selected.Count} edition(s) keep, {removed} remove)"
            : $"Starting... ({selected.Count} edition(s))";
        StatusDetail = "Preparing working source and driver servicing session";
        LogText = "";
        AppendLog($"Build started. Selected editions: {selected.Count}. Removed editions: {removed}.");
        _buildProgressTicks = 0;
        _buildProgressTotalTicks = Math.Max(1, selected.Count * 3);
        _buildCancellation = new CancellationTokenSource();
        OnPropertyChanged(nameof(CanCancelBuild));

        try
        {
            SetProgress(0.08);
            await _dism.InjectDriversIntoSelectedAsync(DriverPath, Editions, msg =>
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    StatusText = msg;
                    StatusDetail = "Mounting image, injecting drivers, and committing changes";
                    AppendLog(msg);

                    if (msg.Contains("Preparing writable working copy", StringComparison.OrdinalIgnoreCase))
                    {
                        SetProgress(0.12);
                        return;
                    }

                    if (msg.Contains("Injecting drivers into", StringComparison.OrdinalIgnoreCase)
                        || msg.Contains("Driver injection complete", StringComparison.OrdinalIgnoreCase))
                    {
                        AdvanceBuildProgress();
                        return;
                    }

                    AdvanceBuildProgress();
                });
            }, _buildCancellation.Token);

            Application.Current.Dispatcher.Invoke(() =>
            {
                SetProgress(0.82);
                if (IsUnattendedEnabled)
                    _dism.InjectUnattendedSetup(UnattendedUserName.Trim(), UnattendedTimeZoneId, AppendLog);

                StatusText = "Building ISO...";
                StatusDetail = "Packaging bootable ISO from the prepared workspace";
                AppendLog("Building ISO...");
            });

            await _dism.BuildIsoAsync(outputPath, 0.3f, msg =>
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    StatusText = msg;
                    StatusDetail = "Writing final ISO image to disk";
                    AppendLog(msg);

                    if (msg.Contains("Locating boot files", StringComparison.OrdinalIgnoreCase))
                        SetProgress(0.88);
                    else if (msg.Contains("Building bootable ISO", StringComparison.OrdinalIgnoreCase))
                        SetProgress(0.94);
                    else if (msg.Contains("This may take several minutes", StringComparison.OrdinalIgnoreCase))
                        SetProgress(0.97);
                    else
                        SetProgress(1.0);
                });
            }, _buildCancellation.Token);

            Application.Current.Dispatcher.Invoke(() =>
            {
                SetProgress(1.0);
                StatusText = "ISO created successfully!";
                StatusDetail = "The customized image is ready";
                AppendLog($"ISO created successfully: {outputPath}");
                IsBuildCompleted = true;
            });
        }
        catch (OperationCanceledException)
        {
            StatusText = "Build cancelled.";
            StatusDetail = "The running tool was stopped and temporary mounts were discarded where possible";
            AppendLog("Build cancelled by user.");
            try { _dism.Cleanup(); } catch { }
        }
        catch (Exception ex)
        {
            StatusText = $"Build failed: {ex.Message}";
            StatusDetail = "The ISO build stopped before completion";
            ShowDialog("Build Error", ex.Message);
            try { _dism.Cleanup(); } catch { }
        }
        finally
        {
            _buildCancellation?.Dispose();
            _buildCancellation = null;
            IsBusy = false;
            OnPropertyChanged(nameof(CanCancelBuild));
        }
    }

    private bool ValidateBeforeBuild(out string message)
    {
        RefreshToolchainStatus();

        if (!IsAdmin)
        {
            message = "Run WinISO Builder as Administrator before servicing Windows images.";
            return false;
        }

        if (!IsDismReady)
        {
            message = "dism.exe was not found in PATH.";
            return false;
        }

        if (!IsAdkReady)
        {
            message = "oscdimg.exe was not found. Install Windows ADK Deployment Tools.";
            return false;
        }

        if (DriverCount <= 0)
        {
            message = "Select a driver folder that contains at least one .inf file.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(OutputPath)
            || !OutputPath.EndsWith(".iso", StringComparison.OrdinalIgnoreCase))
        {
            message = "Choose an output path ending in .iso.";
            return false;
        }

        var outputDirectory = Path.GetDirectoryName(OutputPath);
        if (string.IsNullOrWhiteSpace(outputDirectory) || !Directory.Exists(outputDirectory))
        {
            message = "The output directory does not exist.";
            return false;
        }

        try
        {
            var probePath = Path.Combine(outputDirectory, $".winisobuilder-write-test-{Guid.NewGuid():N}.tmp");
            File.WriteAllText(probePath, "");
            File.Delete(probePath);
        }
        catch
        {
            message = "The output directory is not writable.";
            return false;
        }

        message = "";
        return true;
    }

    private void CancelBuild()
    {
        if (_buildCancellation == null || _buildCancellation.IsCancellationRequested)
            return;

        AppendLog("Cancellation requested...");
        StatusText = "Cancelling build...";
        StatusDetail = "Stopping the active servicing command";
        _buildCancellation.Cancel();
        OnPropertyChanged(nameof(CanCancelBuild));
    }

    public void Cleanup()
    {
        try
        {
            _buildCancellation?.Cancel();
            _dism.Cleanup();
        }
        catch
        {
            // Best-effort cleanup on application shutdown.
        }
    }

    private static bool IsValidUserName(string userName)
    {
        if (string.IsNullOrWhiteSpace(userName) || userName.Length > 20)
            return false;

        var reserved = new[] { "administrator", "guest", "defaultaccount", "wdagutilityaccount" };
        if (reserved.Contains(userName.ToLowerInvariant()))
            return false;

        return userName.All(char.IsLetterOrDigit);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        if (name == nameof(Progress))
            OnProgressChanged();
        if (name == nameof(IsBusy))
            OnBusyChanged();
    }
}

public class RelayCommand : ICommand
{
    private readonly Action<object?> _execute;
    private readonly Func<object?, bool>? _canExecute;
    public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }
    public event EventHandler? CanExecuteChanged { add => CommandManager.RequerySuggested += value; remove => CommandManager.RequerySuggested -= value; }
    public bool CanExecute(object? param) => _canExecute?.Invoke(param) ?? true;
    public void Execute(object? param) => _execute(param);
}
