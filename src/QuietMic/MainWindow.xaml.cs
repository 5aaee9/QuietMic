using System.Windows;
using System.Windows.Media;
using QuietMic.Audio;
using QuietMic.Denoise;
using QuietMic.Models;
using QuietMic.Settings;
using QuietMic.VirtualMic;
using Wpf.Ui.Controls;

namespace QuietMic;

public partial class MainWindow : FluentWindow
{
    private static readonly SolidColorBrush OkBrush = new(Color.FromRgb(0x3F, 0xB9, 0x50));
    private static readonly SolidColorBrush WarnBrush = new(Color.FromRgb(0xE8, 0x9B, 0x1E));

    private readonly AppSettings _settings = AppSettings.Load();
    private AudioEngine? _engine;
    private CancellationTokenSource? _downloadCancellation;
    private VirtualCable.VirtualCableDevices? _cable;
    private bool _running;
    private bool _busy;
    private bool _loading;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _loading = true;
        StrengthSlider.Value = Math.Clamp(_settings.Strength * 100, 0, 100);
        BypassToggle.IsChecked = false;
        MonitorToggle.IsChecked = _settings.MonitorEnabled;

        ModelCombo.ItemsSource = ModelCatalog.All;
        ModelCombo.SelectedItem = ModelCatalog.Find(_settings.ModelId) ?? ModelCatalog.All[0];

        RefreshDevices();
        _loading = false;

        UpdateModelDetails();
        UpdateCableState();
        UpdateStrengthLabel();
    }

    private void RefreshDevices()
    {
        var cable = VirtualCable.Find();

        var allCapture = DeviceList.Capture();
        var microphones = allCapture
            .Where(device => !device.Matches("CABLE Output") && !device.IsAdapter("VB-Audio Virtual Cable"))
            .Where(device => !device.Matches("VoiceMeeter Out"))
            .ToList();

        // 没有实体麦克风时（虚拟机、只接了虚拟声卡），允许选择虚拟声卡自身。
        if (microphones.Count == 0)
        {
            microphones = allCapture.ToList();
        }

        var monitors = DeviceList.Render()
            .Where(device => device.Id != cable?.Render.Id)
            .ToList();

        MicCombo.ItemsSource = microphones;
        MicCombo.SelectedItem =
            microphones.FirstOrDefault(device => device.Id == _settings.MicrophoneId)
            ?? microphones.FirstOrDefault(device => device.IsDefault)
            ?? microphones.FirstOrDefault();

        MonitorCombo.ItemsSource = monitors;
        MonitorCombo.SelectedItem =
            monitors.FirstOrDefault(device => device.Id == _settings.MonitorId)
            ?? monitors.FirstOrDefault(device => device.IsDefault);

        MonitorCombo.IsEnabled = MonitorToggle.IsChecked == true;
    }

    private void UpdateCableState()
    {
        _cable = VirtualCable.Find();

        if (_cable is null)
        {
            CableIcon.Symbol = SymbolRegular.Warning24;
            CableIcon.Foreground = WarnBrush;
            CableStatus.Text = "未检测到虚拟声卡。安装 VB-CABLE 后，它会以“CABLE Output”出现在录音设备中，其他软件选择它即可听到降噪后的声音。";
            InstallCableButton.Visibility = Visibility.Visible;
        }
        else
        {
            CableIcon.Symbol = SymbolRegular.CheckmarkCircle24;
            CableIcon.Foreground = OkBrush;
            CableStatus.Text = $"已就绪。在 Discord / Teams / 游戏里，把麦克风选为“{_cable.Capture.Name}”。";
            InstallCableButton.Visibility = Visibility.Collapsed;
        }

        UpdateStartAvailability();
    }

    private void UpdateModelDetails()
    {
        if (ModelCombo.SelectedItem is not DenoiseModel model)
        {
            ModelDescription.Text = string.Empty;
            DownloadButton.Visibility = Visibility.Collapsed;
            DownloadText.Text = string.Empty;
            UpdateStartAvailability();
            return;
        }

        ModelDescription.Text = model.Description;
        var installed = ModelCatalog.IsInstalled(model);
        DownloadButton.Visibility = installed ? Visibility.Collapsed : Visibility.Visible;
        DownloadText.Text = installed
            ? "已下载"
            : $"{model.TotalSize / 1024d / 1024d:0.0} MB";

        UpdateStartAvailability();
    }

    private void UpdateStartAvailability()
    {
        if (_busy)
        {
            StartButton.IsEnabled = false;
            return;
        }

        if (_running)
        {
            StartButton.IsEnabled = true;
            return;
        }

        StartButton.IsEnabled =
            _cable is not null
            && MicCombo.SelectedItem is AudioDevice
            && ModelCombo.SelectedItem is DenoiseModel model
            && ModelCatalog.IsInstalled(model);
    }

    private void UpdateStrengthLabel()
    {
        StrengthLabel.Text = $"{(int)StrengthSlider.Value}%";
    }

    private void ShowError(string message)
    {
        ErrorBar.Title = "出错了";
        ErrorBar.Message = message;
        ErrorBar.IsOpen = true;
    }

    private async void OnStartClick(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }

        if (_running)
        {
            await StopAsync();
            return;
        }

        if (MicCombo.SelectedItem is not AudioDevice microphone)
        {
            ShowError("请选择一个麦克风。");
            return;
        }

        if (ModelCombo.SelectedItem is not DenoiseModel model)
        {
            ShowError("请选择一个降噪模型。");
            return;
        }

        if (_cable is null)
        {
            ShowError("还没有可用的虚拟声卡，请先安装。");
            return;
        }

        if (!ModelCatalog.IsInstalled(model))
        {
            await DownloadSelectedAsync();
            if (!ModelCatalog.IsInstalled(model))
            {
                return;
            }
        }

        _busy = true;
        UpdateStartAvailability();
        StatusText.Text = "正在加载模型…";
        ErrorBar.IsOpen = false;

        AudioEngine? engine = null;
        try
        {
            var denoiser = await Task.Run(() => DenoiserFactory.Create(model));
            var monitorId = MonitorToggle.IsChecked == true && MonitorCombo.SelectedItem is AudioDevice monitor
                ? monitor.Id
                : null;

            engine = new AudioEngine(denoiser, model.OutputGain)
            {
                Wet = (float)(StrengthSlider.Value / 100),
                Bypass = BypassToggle.IsChecked == true
            };

            engine.InputLevelChanged += level => Dispatcher.BeginInvoke(() => InputLevelBar.Value = level * 100);
            engine.OutputLevelChanged += level => Dispatcher.BeginInvoke(() => OutputLevelBar.Value = level * 100);
            engine.Faulted += exception => Dispatcher.BeginInvoke(() => ShowError(exception.Message));

            await Task.Run(() => engine.Start(microphone.Id, _cable.Render.Id, monitorId));

            _engine = engine;
            _running = true;
            StartButton.Content = "停止";
            StartButton.Icon = new SymbolIcon(SymbolRegular.Stop24);
            StartButton.Appearance = ControlAppearance.Caution;
            StatusText.Text = "运行中 · 正在输出到虚拟麦克风";
        }
        catch (Exception exception)
        {
            engine?.Dispose();
            ShowError(exception.Message);
            StatusText.Text = "就绪";
        }
        finally
        {
            _busy = false;
            UpdateStartAvailability();
        }
    }

    private async Task StopAsync(bool restarting = false)
    {
        _running = false;
        var engine = _engine;
        _engine = null;

        if (engine is not null)
        {
            await Task.Run(engine.Dispose);
        }

        StartButton.Content = "启动";
        StartButton.Icon = new SymbolIcon(SymbolRegular.Play24);
        StartButton.Appearance = ControlAppearance.Primary;
        InputLevelBar.Value = 0;
        OutputLevelBar.Value = 0;
        StatusText.Text = restarting ? "正在重启…" : "就绪";
        UpdateStartAvailability();
    }

    private async Task RestartIfRunningAsync()
    {
        if (!_running || _busy)
        {
            return;
        }

        _busy = true;
        await StopAsync(restarting: true);
        _busy = false;
        await StartRestartAsync();
    }

    private async Task StartRestartAsync()
    {
        // 复用启动路径：按钮状态在 StopAsync 中已复位。
        if (MicCombo.SelectedItem is not AudioDevice microphone
            || ModelCombo.SelectedItem is not DenoiseModel model
            || _cable is null)
        {
            return;
        }

        _busy = true;
        UpdateStartAvailability();

        try
        {
            var denoiser = await Task.Run(() => DenoiserFactory.Create(model));
            var monitorId = MonitorToggle.IsChecked == true && MonitorCombo.SelectedItem is AudioDevice monitor
                ? monitor.Id
                : null;

            var engine = new AudioEngine(denoiser, model.OutputGain)
            {
                Wet = (float)(StrengthSlider.Value / 100),
                Bypass = BypassToggle.IsChecked == true
            };

            engine.InputLevelChanged += level => Dispatcher.BeginInvoke(() => InputLevelBar.Value = level * 100);
            engine.OutputLevelChanged += level => Dispatcher.BeginInvoke(() => OutputLevelBar.Value = level * 100);
            engine.Faulted += exception => Dispatcher.BeginInvoke(() => ShowError(exception.Message));

            await Task.Run(() => engine.Start(microphone.Id, _cable.Render.Id, monitorId));

            _engine = engine;
            _running = true;
            StartButton.Content = "停止";
            StartButton.Icon = new SymbolIcon(SymbolRegular.Stop24);
            StartButton.Appearance = ControlAppearance.Caution;
            StatusText.Text = "运行中 · 正在输出到虚拟麦克风";
        }
        catch (Exception exception)
        {
            ShowError(exception.Message);
            StatusText.Text = "就绪";
        }
        finally
        {
            _busy = false;
            UpdateStartAvailability();
        }
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        var wasRunning = _running;
        if (wasRunning)
        {
            await StopAsync(restarting: true);
        }

        RefreshDevices();
        UpdateCableState();

        if (wasRunning)
        {
            await StartRestartAsync();
        }
    }

    private async void OnModelChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        UpdateModelDetails();
        if (ModelCombo.SelectedItem is DenoiseModel model)
        {
            _settings.ModelId = model.Id;
            _settings.Save();
        }

        await RestartIfRunningAsync();
    }

    private async void OnMicrophoneChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        if (MicCombo.SelectedItem is AudioDevice microphone)
        {
            _settings.MicrophoneId = microphone.Id;
            _settings.Save();
        }

        UpdateStartAvailability();
        await RestartIfRunningAsync();
    }

    private async void OnMonitorDeviceChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        if (MonitorCombo.SelectedItem is AudioDevice monitor)
        {
            _settings.MonitorId = monitor.Id;
            _settings.Save();
        }

        await RestartIfRunningAsync();
    }

    private async void OnMonitorToggled(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        MonitorCombo.IsEnabled = MonitorToggle.IsChecked == true;
        _settings.MonitorEnabled = MonitorToggle.IsChecked == true;
        _settings.Save();
        await RestartIfRunningAsync();
    }

    private void OnBypassChanged(object sender, RoutedEventArgs e)
    {
        if (_engine is not null)
        {
            _engine.Bypass = BypassToggle.IsChecked == true;
        }
    }

    private void OnStrengthChanged(object sender, System.Windows.RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading)
        {
            return;
        }

        UpdateStrengthLabel();
        _settings.Strength = StrengthSlider.Value / 100;
        _settings.Save();

        if (_engine is not null)
        {
            _engine.Wet = (float)(StrengthSlider.Value / 100);
        }
    }

    private async void OnDownloadClick(object sender, RoutedEventArgs e)
    {
        await DownloadSelectedAsync();
    }

    private async Task DownloadSelectedAsync()
    {
        if (ModelCombo.SelectedItem is not DenoiseModel model || _busy)
        {
            return;
        }

        _downloadCancellation = new CancellationTokenSource();
        DownloadButton.IsEnabled = false;
        DownloadProgress.Visibility = Visibility.Visible;
        DownloadProgress.Value = 0;
        StatusText.Text = $"正在下载 {model.DisplayName}…";

        try
        {
            await ModelDownloader.DownloadAsync(
                model,
                (received, total) => Dispatcher.BeginInvoke(() =>
                {
                    DownloadProgress.Value = received * 100d / Math.Max(total, 1);
                    DownloadText.Text = $"{received / 1024d / 1024d:0.0} / {total / 1024d / 1024d:0.0} MB";
                }),
                _downloadCancellation.Token);
        }
        catch (OperationCanceledException)
        {
            ShowError("下载已取消。");
        }
        catch (Exception exception)
        {
            ShowError($"模型下载失败：{exception.Message}");
        }
        finally
        {
            DownloadButton.IsEnabled = true;
            DownloadProgress.Visibility = Visibility.Collapsed;
            if (ModelCatalog.IsInstalled(model))
            {
                StatusText.Text = "就绪";
            }

            UpdateModelDetails();
        }
    }

    private async void OnInstallCableClick(object sender, RoutedEventArgs e)
    {
        InstallCableButton.IsEnabled = false;
        ErrorBar.IsOpen = false;

        try
        {
            await VirtualCable.InstallAsync(
                message => Dispatcher.BeginInvoke(() => StatusText.Text = message),
                CancellationToken.None);
        }
        catch (Exception exception)
        {
            ShowError($"虚拟声卡安装失败：{exception.Message}");
        }
        finally
        {
            InstallCableButton.IsEnabled = true;
            RefreshDevices();
            UpdateCableState();
        }

        if (VirtualCable.Find() is null)
        {
            ShowError("驱动已安装但设备还没出现，请重启电脑后再打开 QuietMic。");
        }
        else
        {
            StatusText.Text = "虚拟声卡已就绪";
        }
    }

    private void OnOpenSoundSettingsClick(object sender, RoutedEventArgs e)
    {
        VirtualCable.OpenSoundSettings();
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _settings.Strength = StrengthSlider.Value / 100;
        _settings.MonitorEnabled = MonitorToggle.IsChecked == true;
        _settings.Save();

        _downloadCancellation?.Cancel();

        var engine = _engine;
        _engine = null;
        if (engine is not null)
        {
            Task.Run(engine.Dispose).Wait(2000);
        }
    }
}
