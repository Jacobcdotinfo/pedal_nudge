using System;
using System.Diagnostics;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace PedalNudge.Windows;

public sealed class MainForm : Form
{
    private readonly JoyConPedalMonitor _monitor = new();

    private readonly ComboBox _deviceCombo = new();
    private readonly Button _refreshButton = new() { Text = "Refresh" };
    private readonly Button _connectButton = new() { Text = "Connect" };
    private readonly Button _disconnectButton = new() { Text = "Disconnect" };

    private readonly NumericUpDown _idleSeconds = Number(1, 120, 8, 0, 1);
    private readonly NumericUpDown _motionThreshold = Number(1, 500, 35, 1, 1);
    private readonly NumericUpDown _rumbleFrequency = Number(40, 600, 160, 0, 10);
    private readonly NumericUpDown _rumbleAmplitude = Number(0, 1, 0.55m, 2, 0.05m);
    private readonly NumericUpDown _rumbleOnMs = Number(50, 2000, 250, 0, 50);
    private readonly NumericUpDown _rumbleOffMs = Number(100, 5000, 850, 0, 50);
    private readonly Label _settingsFileValue = ValueLabel(AppSettingsStore.SettingsPath);
    private readonly Label _logFileValue = ValueLabel(AppLogger.LogPath);
    private readonly Button _openSettingsFolderButton = new() { Text = "Open settings/log folder" };

    private readonly Button _calibrateButton = new() { Text = "Calibrate while still" };
    private readonly Button _startButton = new() { Text = "Start watcher" };
    private readonly Button _stopButton = new() { Text = "Stop watcher" };
    private readonly Button _testRumbleButton = new() { Text = "Test rumble" };

    private readonly Label _statusValue = ValueLabel("Not connected.");
    private readonly Label _deviceValue = ValueLabel("None");
    private readonly Label _motionValue = ValueLabel("0.0");
    private readonly Label _idleValue = ValueLabel("0.0 s");
    private readonly Label _rumbleValue = ValueLabel("Off");
    private readonly ProgressBar _motionBar = new() { Minimum = 0, Maximum = 100, Dock = DockStyle.Fill };
    private readonly TextBox _logBox = new()
    {
        Multiline = true,
        ReadOnly = true,
        ScrollBars = ScrollBars.Vertical,
        Dock = DockStyle.Fill,
        Font = new Font("Consolas", 9f)
    };

    private MonitorSnapshot? _lastSnapshot;
    private bool _busy;
    private bool _loadingSettings;
    private bool _shutdownStarted;
    private bool _shutdownComplete;
    private string? _lastSettingsSaveError;

    public MainForm()
    {
        Text = "Pedal Nudge Joy-Con";
        MinimumSize = new Size(780, 620);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9f);

        BuildLayout();
        LoadSavedSettings();
        WireEvents();
        UpdateControlState();
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        RefreshDevices();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_shutdownComplete)
        {
            base.OnFormClosing(e);
            return;
        }

        e.Cancel = true;

        if (_shutdownStarted)
        {
            return;
        }

        _shutdownStarted = true;
        BeginShutdownAndClose();
    }

    private void BeginShutdownAndClose()
    {
        _ = ShutdownAndCloseAsync();
    }

    private async Task ShutdownAndCloseAsync()
    {
        try
        {
            SetBusy(true);
            AppendLog("Closing: saving settings and disconnecting Joy-Con...");
            SaveSettings();

            using var disconnectCts = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            var disconnectTask = Task.Run(() => _monitor.DisconnectAsync(disconnectCts.Token));
            var timeoutTask = Task.Delay(TimeSpan.FromSeconds(5));
            var completedTask = await Task.WhenAny(disconnectTask, timeoutTask).ConfigureAwait(true);

            if (completedTask == disconnectTask)
            {
                await disconnectTask.ConfigureAwait(true);
                AppendLog("Shutdown disconnect completed.");
            }
            else
            {
                AppLogger.Log("Shutdown disconnect timed out. Allowing the application window to close anyway.");
                AppendLog("Shutdown disconnect timed out. Closing app anyway.");
            }
        }
        catch (Exception ex)
        {
            AppLogger.LogException("Shutdown cleanup failed", ex);
            AppendLog($"Shutdown cleanup failed: {ex.Message}");
        }
        finally
        {
            _shutdownComplete = true;

            try
            {
                if (!IsDisposed)
                {
                    if (InvokeRequired && IsHandleCreated)
                    {
                        BeginInvoke(new Action(Close));
                    }
                    else
                    {
                        Close();
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.LogException("Final window close failed", ex);
                Application.ExitThread();
            }
        }
    }

    private void BuildLayout()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(12),
            ColumnCount = 1,
            RowCount = 5
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        Controls.Add(root);

        var title = new Label
        {
            Text = "Pedal Nudge Joy-Con",
            AutoSize = true,
            Font = new Font(Font.FontFamily, 16f, FontStyle.Bold),
            Margin = new Padding(0, 0, 0, 8)
        };
        root.Controls.Add(title, 0, 0);

        root.Controls.Add(BuildDeviceGroup(), 0, 1);
        root.Controls.Add(BuildSettingsGroup(), 0, 2);
        root.Controls.Add(BuildStatusGroup(), 0, 3);
        root.Controls.Add(BuildLogGroup(), 0, 4);
    }

    private Control BuildDeviceGroup()
    {
        var group = new GroupBox
        {
            Text = "Device",
            Dock = DockStyle.Top,
            AutoSize = true,
            Padding = new Padding(10),
            Margin = new Padding(0, 0, 0, 10)
        };

        var panel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            WrapContents = true
        };

        _deviceCombo.DropDownStyle = ComboBoxStyle.DropDownList;
        _deviceCombo.Width = 430;

        panel.Controls.Add(_deviceCombo);
        panel.Controls.Add(_refreshButton);
        panel.Controls.Add(_connectButton);
        panel.Controls.Add(_disconnectButton);
        group.Controls.Add(panel);
        return group;
    }

    private Control BuildSettingsGroup()
    {
        var group = new GroupBox
        {
            Text = "Tuning",
            Dock = DockStyle.Top,
            AutoSize = true,
            Padding = new Padding(10),
            Margin = new Padding(0, 0, 0, 10)
        };

        var outer = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 1,
            RowCount = 2
        };
        outer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        outer.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 4,
            RowCount = 3
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));

        AddSetting(table, 0, 0, "Idle seconds", _idleSeconds);
        AddSetting(table, 0, 1, "Motion threshold", _motionThreshold);
        AddSetting(table, 1, 0, "Rumble frequency", _rumbleFrequency);
        AddSetting(table, 1, 1, "Rumble amplitude", _rumbleAmplitude);
        AddSetting(table, 2, 0, "Rumble on ms", _rumbleOnMs);
        AddSetting(table, 2, 1, "Rumble off ms", _rumbleOffMs);

        var settingsPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            WrapContents = true,
            Margin = new Padding(0, 8, 0, 0)
        };
        settingsPanel.Controls.Add(Label("Settings file"));
        settingsPanel.Controls.Add(_settingsFileValue);
        settingsPanel.Controls.Add(Label("Log file"));
        settingsPanel.Controls.Add(_logFileValue);
        settingsPanel.Controls.Add(_openSettingsFolderButton);

        outer.Controls.Add(table, 0, 0);
        outer.Controls.Add(settingsPanel, 0, 1);

        group.Controls.Add(outer);
        return group;
    }

    private Control BuildStatusGroup()
    {
        var group = new GroupBox
        {
            Text = "Watcher",
            Dock = DockStyle.Top,
            AutoSize = true,
            Padding = new Padding(10),
            Margin = new Padding(0, 0, 0, 10)
        };

        var outer = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 1,
            RowCount = 2
        };
        outer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        outer.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var actionPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            WrapContents = true,
            Margin = new Padding(0, 0, 0, 8)
        };
        actionPanel.Controls.Add(_calibrateButton);
        actionPanel.Controls.Add(_startButton);
        actionPanel.Controls.Add(_stopButton);
        actionPanel.Controls.Add(_testRumbleButton);

        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 2,
            RowCount = 6
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        AddStatus(table, 0, "Status", _statusValue);
        AddStatus(table, 1, "Device", _deviceValue);
        AddStatus(table, 2, "Motion score", _motionValue);
        AddStatus(table, 3, "Idle time", _idleValue);
        AddStatus(table, 4, "Rumble", _rumbleValue);
        table.Controls.Add(Label("Motion meter"), 0, 5);
        table.Controls.Add(_motionBar, 1, 5);

        outer.Controls.Add(actionPanel, 0, 0);
        outer.Controls.Add(table, 0, 1);
        group.Controls.Add(outer);
        return group;
    }

    private Control BuildLogGroup()
    {
        var group = new GroupBox
        {
            Text = "Log",
            Dock = DockStyle.Fill,
            Padding = new Padding(10),
            Margin = new Padding(0)
        };
        group.Controls.Add(_logBox);
        return group;
    }

    private void WireEvents()
    {
        _refreshButton.Click += (_, _) => RefreshDevices();
        _connectButton.Click += async (_, _) => await RunUiTaskAsync(ConnectSelectedDeviceAsync);
        _disconnectButton.Click += async (_, _) => await RunUiTaskAsync(_monitor.DisconnectAsync);
        _calibrateButton.Click += async (_, _) => await RunUiTaskAsync(CalibrateAsync);
        _startButton.Click += (_, _) => StartWatcher();
        _stopButton.Click += async (_, _) => await RunUiTaskAsync(_monitor.StopWatchingAsync);
        _testRumbleButton.Click += async (_, _) =>
        {
            SaveSettings();
            await RunUiTaskAsync(() => _monitor.TestRumbleAsync(ReadSettings()));
        };
        _openSettingsFolderButton.Click += (_, _) => OpenSettingsFolder();

        foreach (var input in TuningControls())
        {
            input.ValueChanged += (_, _) => TuningValueChanged();
        }

        _monitor.Log += message => BeginOnUi(() => AppendLog(message));
        _monitor.SnapshotChanged += snapshot => BeginOnUi(() => ApplySnapshot(snapshot));
    }

    private void RefreshDevices()
    {
        try
        {
            var devices = JoyConPedalMonitor.FindDevices();
            _deviceCombo.Items.Clear();

            foreach (var device in devices)
            {
                _deviceCombo.Items.Add(device);
            }

            if (_deviceCombo.Items.Count > 0)
            {
                _deviceCombo.SelectedIndex = 0;
                AppendLog($"Found {_deviceCombo.Items.Count} Nintendo HID device(s). Select your Joy-Con and connect.");
            }
            else
            {
                AppendLog("No Nintendo HID devices found. Pair the Joy-Con in Windows Bluetooth settings, then refresh.");
            }
        }
        catch (Exception ex)
        {
            AppendLog($"Device refresh failed: {ex.Message}");
            MessageBox.Show(this, ex.Message, "Refresh failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        UpdateControlState();
    }

    private async Task ConnectSelectedDeviceAsync()
    {
        if (_deviceCombo.SelectedItem is not JoyConDeviceOption selectedDevice)
        {
            MessageBox.Show(this, "Select a Joy-Con first.", "No device selected", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        await _monitor.ConnectAsync(selectedDevice).ConfigureAwait(true);
    }

    private async Task CalibrateAsync()
    {
        var originalText = _calibrateButton.Text;
        var progress = new Progress<double>(value =>
        {
            _calibrateButton.Text = $"Calibrating {value:P0}";
        });

        try
        {
            await _monitor.CalibrateStillAsync(TimeSpan.FromSeconds(2.5), progress).ConfigureAwait(true);
        }
        finally
        {
            _calibrateButton.Text = originalText;
        }
    }

    private void StartWatcher()
    {
        try
        {
            SaveSettings();
            _monitor.StartWatching(ReadSettings());
        }
        catch (Exception ex)
        {
            AppendLog($"Start failed: {ex.Message}");
            MessageBox.Show(this, ex.Message, "Start failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void LoadSavedSettings()
    {
        _loadingSettings = true;

        try
        {
            var fileExisted = AppSettingsStore.SettingsFileExists;
            var settings = AppSettingsStore.Load(out var error);
            ApplySettings(settings);

            if (error is not null)
            {
                AppendLog($"Settings file could not be loaded: {error}. Using defaults. Path: {AppSettingsStore.SettingsPath}");
            }
            else if (fileExisted)
            {
                AppendLog($"Loaded saved tuning settings from {AppSettingsStore.SettingsPath}");
            }
            else
            {
                AppendLog($"Settings will be saved to {AppSettingsStore.SettingsPath}");
            }
        }
        finally
        {
            _loadingSettings = false;
        }

        SaveSettings();
    }

    private void SaveSettings()
    {
        if (_loadingSettings)
        {
            return;
        }

        var settings = AppSettings.FromMotionSettings(ReadSettings());

        if (AppSettingsStore.Save(settings, out var error))
        {
            _lastSettingsSaveError = null;
            return;
        }

        if (!string.Equals(_lastSettingsSaveError, error, StringComparison.Ordinal))
        {
            _lastSettingsSaveError = error;
            AppendLog($"Settings save failed: {error}");
        }
    }

    private void ApplySettings(AppSettings settings)
    {
        SetNumber(_idleSeconds, settings.IdleSeconds);
        SetNumber(_motionThreshold, settings.MotionThreshold);
        SetNumber(_rumbleFrequency, settings.RumbleFrequencyHz);
        SetNumber(_rumbleAmplitude, settings.RumbleAmplitude);
        SetNumber(_rumbleOnMs, settings.RumbleOnMilliseconds);
        SetNumber(_rumbleOffMs, settings.RumbleOffMilliseconds);
    }

    private void TuningValueChanged()
    {
        if (_loadingSettings)
        {
            return;
        }

        SaveSettings();

        if (_lastSnapshot?.IsWatching ?? false)
        {
            _monitor.Settings = ReadSettings();
        }
    }

    private void OpenSettingsFolder()
    {
        try
        {
            System.IO.Directory.CreateDirectory(AppSettingsStore.SettingsDirectory);
            Process.Start(new ProcessStartInfo
            {
                FileName = AppSettingsStore.SettingsDirectory,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            AppendLog($"Could not open settings folder: {ex.Message}");
            MessageBox.Show(this, ex.Message, "Open settings folder failed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private MotionSettings ReadSettings()
    {
        return new MotionSettings
        {
            IdleSeconds = (int)_idleSeconds.Value,
            MotionThreshold = (double)_motionThreshold.Value,
            RumbleFrequencyHz = (double)_rumbleFrequency.Value,
            RumbleAmplitude = (double)_rumbleAmplitude.Value,
            RumbleOnMilliseconds = (int)_rumbleOnMs.Value,
            RumbleOffMilliseconds = (int)_rumbleOffMs.Value
        };
    }

    private async Task RunUiTaskAsync(Func<Task> action)
    {
        SetBusy(true);
        try
        {
            await action().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            AppendLog($"Error: {ex.Message}");
            MessageBox.Show(this, ex.Message, "Pedal Nudge", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void ApplySnapshot(MonitorSnapshot snapshot)
    {
        _lastSnapshot = snapshot;

        _statusValue.Text = snapshot.LastError is null ? snapshot.Status : $"{snapshot.Status} ({snapshot.LastError})";
        _deviceValue.Text = snapshot.IsConnected ? snapshot.DeviceName : "None";
        _motionValue.Text = $"{snapshot.MotionScore:0.0} / {snapshot.EffectiveThreshold:0.0}";
        _idleValue.Text = snapshot.IsWatching ? $"{snapshot.SecondsSinceMotion:0.0} s" : "0.0 s";
        _rumbleValue.Text = snapshot.IsRumbling ? "Vibrating" : snapshot.IsIdle ? "Idle" : "Off";

        var percent = snapshot.EffectiveThreshold <= 0
            ? 0
            : (int)Math.Clamp(snapshot.MotionScore / snapshot.EffectiveThreshold * 100.0, 0, 100);
        _motionBar.Value = percent;

        UpdateControlState();
    }

    private void UpdateControlState()
    {
        if (_shutdownStarted && !_shutdownComplete)
        {
            _refreshButton.Enabled = false;
            _deviceCombo.Enabled = false;
            _connectButton.Enabled = false;
            _disconnectButton.Enabled = false;
            _calibrateButton.Enabled = false;
            _startButton.Enabled = false;
            _stopButton.Enabled = false;
            _testRumbleButton.Enabled = false;
            return;
        }

        var connected = _lastSnapshot?.IsConnected ?? false;
        var watching = _lastSnapshot?.IsWatching ?? false;

        _refreshButton.Enabled = !_busy && !connected;
        _deviceCombo.Enabled = !_busy && !connected;
        _connectButton.Enabled = !_busy && !connected && _deviceCombo.Items.Count > 0;
        _disconnectButton.Enabled = !_busy && connected;
        _calibrateButton.Enabled = !_busy && connected;
        _startButton.Enabled = !_busy && connected && !watching;
        _stopButton.Enabled = !_busy && connected && watching;
        _testRumbleButton.Enabled = !_busy && connected;
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        UseWaitCursor = busy;
        UpdateControlState();
    }

    private void AppendLog(string message)
    {
        AppLogger.Log(message);

        if (IsDisposed || Disposing)
        {
            return;
        }

        _logBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
    }

    private void BeginOnUi(Action action)
    {
        if (IsDisposed || Disposing || !IsHandleCreated)
        {
            return;
        }

        if (InvokeRequired)
        {
            try
            {
                BeginInvoke(new Action(() => SafeRunUiAction(action)));
            }
            catch (ObjectDisposedException)
            {
                // Closing.
            }
            catch (InvalidOperationException ex)
            {
                AppLogger.LogException("BeginInvoke skipped during shutdown", ex);
            }
        }
        else
        {
            SafeRunUiAction(action);
        }
    }

    private void SafeRunUiAction(Action action)
    {
        if (IsDisposed || Disposing)
        {
            return;
        }

        try
        {
            action();
        }
        catch (ObjectDisposedException)
        {
            // Closing.
        }
        catch (InvalidOperationException ex)
        {
            AppLogger.LogException("UI update skipped", ex);
        }
        catch (Exception ex)
        {
            AppLogger.LogException("UI update failed", ex);
        }
    }

    private static void AddSetting(TableLayoutPanel table, int row, int pairIndex, string text, Control input)
    {
        var labelColumn = pairIndex * 2;
        table.Controls.Add(Label(text), labelColumn, row);
        table.Controls.Add(input, labelColumn + 1, row);
    }

    private static void AddStatus(TableLayoutPanel table, int row, string text, Control value)
    {
        table.Controls.Add(Label(text), 0, row);
        table.Controls.Add(value, 1, row);
    }

    private static Label Label(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Anchor = AnchorStyles.Left,
        TextAlign = ContentAlignment.MiddleLeft,
        Margin = new Padding(0, 4, 10, 4)
    };

    private static Label ValueLabel(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Anchor = AnchorStyles.Left,
        TextAlign = ContentAlignment.MiddleLeft,
        Margin = new Padding(0, 4, 0, 4)
    };

    private NumericUpDown[] TuningControls() =>
        new[]
        {
            _idleSeconds,
            _motionThreshold,
            _rumbleFrequency,
            _rumbleAmplitude,
            _rumbleOnMs,
            _rumbleOffMs
        };

    private static void SetNumber(NumericUpDown control, double value)
    {
        decimal decimalValue;

        try
        {
            decimalValue = Convert.ToDecimal(value);
        }
        catch
        {
            decimalValue = control.Value;
        }

        if (decimalValue < control.Minimum)
        {
            decimalValue = control.Minimum;
        }
        else if (decimalValue > control.Maximum)
        {
            decimalValue = control.Maximum;
        }

        control.Value = decimalValue;
    }

    private static NumericUpDown Number(decimal min, decimal max, decimal value, int decimalPlaces, decimal increment)
    {
        return new NumericUpDown
        {
            Minimum = min,
            Maximum = max,
            Value = value,
            DecimalPlaces = decimalPlaces,
            Increment = increment,
            Width = 110,
            Anchor = AnchorStyles.Left
        };
    }
}
