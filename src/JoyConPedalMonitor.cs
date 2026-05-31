using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ThreadingTimer = System.Threading.Timer;
using HidSharp;
using wtf.cluster.JoyCon.Calibration;
using wtf.cluster.JoyCon.InputData;
using wtf.cluster.JoyCon.InputReports;
using wtf.cluster.JoyCon.Rumble;
using JoyConController = wtf.cluster.JoyCon.JoyCon;

namespace PedalNudge.Windows;

public sealed class JoyConPedalMonitor : IAsyncDisposable
{
    private static readonly TimeSpan RumbleWriteTimeout = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan RumbleLoopStopTimeout = TimeSpan.FromSeconds(2);

    private readonly object _stateLock = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    private JoyConController? _joyCon;
    private CalibrationData? _calibration;
    private MotionSettings _settings = new();
    private ThreadingTimer? _watchdogTimer;
    private CancellationTokenSource? _rumbleCts;
    private Task? _rumbleTask;

    private DateTime _lastMotionUtc = DateTime.UtcNow;
    private DateTime _ignoreMotionUntilUtc = DateTime.MinValue;
    private DateTime _lastSnapshotUtc = DateTime.MinValue;

    private double _latestMotionScore;
    private double _noiseFloor;
    private bool _watching;
    private bool _isRumbling;
    private bool _isCalibrating;
    private List<double> _calibrationSamples = new();
    private (double X, double Y, double Z)? _lastAccel;
    private string _deviceName = string.Empty;
    private string _status = "Not connected.";
    private string? _lastError;

    public event Action<string>? Log;
    public event Action<MonitorSnapshot>? SnapshotChanged;

    public MotionSettings Settings
    {
        get
        {
            lock (_stateLock)
            {
                return _settings;
            }
        }
        set
        {
            lock (_stateLock)
            {
                _settings = value;
            }

            PublishSnapshot(force: true);
        }
    }

    public static IReadOnlyList<JoyConDeviceOption> FindDevices()
    {
        var devices = DeviceList.Local
            .GetHidDevices(0x057e)
            .GroupBy(device => device.DevicePath)
            .Select(group => group.First())
            .Select(device => new JoyConDeviceOption(device, BuildDeviceLabel(device)))
            .OrderBy(option => option.DisplayName)
            .ToList();

        return devices;
    }

    public async Task ConnectAsync(JoyConDeviceOption deviceOption, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(deviceOption);

        await DisconnectAsync().ConfigureAwait(false);

        lock (_stateLock)
        {
            _status = $"Opening {deviceOption.DisplayName}...";
            _deviceName = deviceOption.DisplayName;
            _lastError = null;
            _noiseFloor = 0;
            _latestMotionScore = 0;
            _lastAccel = null;
        }

        PublishSnapshot(force: true);

        var joyCon = new JoyConController(deviceOption.Device);
        joyCon.ReportReceived += HandleReportReceivedAsync;
        joyCon.StoppedOnError += HandleStoppedOnErrorAsync;

        try
        {
            joyCon.Start();
            await joyCon.SetInputReportModeAsync(JoyConController.InputReportType.Full, cancellationToken: cancellationToken).ConfigureAwait(false);
            await joyCon.EnableImuAsync(true, cancellationToken: cancellationToken).ConfigureAwait(false);
            await joyCon.EnableRumbleAsync(true, cancellationToken: cancellationToken).ConfigureAwait(false);

            try
            {
                await joyCon.SetPlayerLedsAsync(
                    JoyConController.LedState.On,
                    JoyConController.LedState.Off,
                    JoyConController.LedState.Off,
                    JoyConController.LedState.Off,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log?.Invoke($"LED setup skipped: {ex.Message}");
            }

            var calibration = await TryReadCalibrationAsync(joyCon, cancellationToken).ConfigureAwait(false);

            lock (_stateLock)
            {
                _joyCon = joyCon;
                _calibration = calibration;
                _lastMotionUtc = DateTime.UtcNow;
                _status = calibration?.ImuCalibration is null
                    ? "Connected. IMU is on; using raw sensor fallback."
                    : "Connected. IMU is on and calibration data loaded.";
            }

            Log?.Invoke($"Connected: {deviceOption.DisplayName}");
            PublishSnapshot(force: true);
        }
        catch
        {
            joyCon.ReportReceived -= HandleReportReceivedAsync;
            joyCon.StoppedOnError -= HandleStoppedOnErrorAsync;
            try
            {
                joyCon.Stop();
                joyCon.Dispose();
            }
            catch
            {
                // Best effort cleanup.
            }

            lock (_stateLock)
            {
                _status = "Connect failed.";
                _deviceName = string.Empty;
            }

            PublishSnapshot(force: true);
            throw;
        }
    }

    public void StartWatching(MotionSettings settings)
    {
        JoyConController? joyCon;

        lock (_stateLock)
        {
            joyCon = _joyCon;
            if (joyCon is null)
            {
                throw new InvalidOperationException("Connect a Joy-Con first.");
            }

            _settings = settings;
            _watching = true;
            _isRumbling = false;
            _lastMotionUtc = DateTime.UtcNow;
            _ignoreMotionUntilUtc = DateTime.MinValue;
            _lastError = null;
            _status = "Watching for pedaling motion.";

            _rumbleCts?.Cancel();
            _rumbleCts?.Dispose();
            _rumbleCts = new CancellationTokenSource();

            _watchdogTimer?.Dispose();
            _watchdogTimer = new ThreadingTimer(_ => WatchdogTick(), null, 250, 250);
        }

        Log?.Invoke("Watcher started.");
        PublishSnapshot(force: true);
    }

    public Task StopWatchingAsync()
    {
        return StopWatchingAsync(CancellationToken.None);
    }

    public async Task StopWatchingAsync(CancellationToken cancellationToken)
    {
        CancellationTokenSource? rumbleCts;
        Task? rumbleTask;

        lock (_stateLock)
        {
            _watching = false;
            _status = _joyCon is null ? "Not connected." : "Connected. Watcher stopped.";

            _watchdogTimer?.Dispose();
            _watchdogTimer = null;

            rumbleCts = _rumbleCts;
            rumbleTask = _rumbleTask;
            _rumbleCts = null;
            _rumbleTask = null;
        }

        rumbleCts?.Cancel();

        if (rumbleTask is not null)
        {
            try
            {
                var stopped = await WaitForTaskAsync(rumbleTask, RumbleLoopStopTimeout, cancellationToken).ConfigureAwait(false);
                if (!stopped)
                {
                    Log?.Invoke("Rumble loop did not stop before timeout; continuing cleanup.");
                    AppLogger.Log("Rumble loop did not stop before timeout; continuing cleanup.");
                }
            }
            catch (OperationCanceledException)
            {
                Log?.Invoke("Stop watcher was cancelled during shutdown.");
            }
            catch (Exception ex)
            {
                Log?.Invoke($"Rumble loop stopped with error: {ex.Message}");
                AppLogger.LogException("Rumble loop stopped with error", ex);
            }
        }

        rumbleCts?.Dispose();

        try
        {
            await StopRumbleAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Log?.Invoke("Stop rumble timed out during cleanup.");
            AppLogger.Log("Stop rumble timed out during cleanup.");
        }
        catch (Exception ex)
        {
            Log?.Invoke($"Stop rumble failed during cleanup: {ex.Message}");
            AppLogger.LogException("Stop rumble failed during cleanup", ex);
        }

        Log?.Invoke("Watcher stopped.");
        PublishSnapshot(force: true);
    }

    public Task DisconnectAsync()
    {
        return DisconnectAsync(CancellationToken.None);
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken)
    {
        await StopWatchingAsync(cancellationToken).ConfigureAwait(false);

        JoyConController? joyCon;
        lock (_stateLock)
        {
            joyCon = _joyCon;
        }

        if (joyCon is not null)
        {
            try
            {
                await WriteRumbleAsync(joyCon, new RumbleSet(), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Log?.Invoke("Rumble-off command timed out during disconnect.");
                AppLogger.Log("Rumble-off command timed out during disconnect.");
            }
            catch (Exception ex)
            {
                Log?.Invoke($"Rumble-off command failed during disconnect: {ex.Message}");
                AppLogger.LogException("Rumble-off command failed during disconnect", ex);
            }

            joyCon.ReportReceived -= HandleReportReceivedAsync;
            joyCon.StoppedOnError -= HandleStoppedOnErrorAsync;

            try
            {
                joyCon.Stop();
                joyCon.Dispose();
            }
            catch (Exception ex)
            {
                Log?.Invoke($"Controller cleanup warning: {ex.Message}");
                AppLogger.LogException("Controller cleanup warning", ex);
            }
        }

        lock (_stateLock)
        {
            _joyCon = null;
            _calibration = null;
            _deviceName = string.Empty;
            _watching = false;
            _isRumbling = false;
            _status = "Not connected.";
            _latestMotionScore = 0;
            _noiseFloor = 0;
            _lastAccel = null;
        }

        Log?.Invoke("Disconnected.");
        PublishSnapshot(force: true);
    }

    public async Task CalibrateStillAsync(TimeSpan duration, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        lock (_stateLock)
        {
            if (_joyCon is null)
            {
                throw new InvalidOperationException("Connect a Joy-Con first.");
            }

            _calibrationSamples = new List<double>();
            _isCalibrating = true;
            _status = "Calibrating. Keep the Joy-Con still.";
        }

        PublishSnapshot(force: true);

        var stopwatch = Stopwatch.StartNew();
        try
        {
            while (stopwatch.Elapsed < duration)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(Math.Clamp(stopwatch.Elapsed.TotalMilliseconds / duration.TotalMilliseconds, 0, 1));
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            lock (_stateLock)
            {
                _isCalibrating = false;
            }
        }

        double noiseFloor;
        int sampleCount;
        lock (_stateLock)
        {
            sampleCount = _calibrationSamples.Count;
            if (sampleCount < 10)
            {
                _status = "Calibration failed: not enough samples.";
                throw new InvalidOperationException("Not enough IMU samples were received. Confirm the Joy-Con is connected and IMU reports are flowing.");
            }

            noiseFloor = Percentile(_calibrationSamples, 0.95);
            _noiseFloor = noiseFloor;
            _lastMotionUtc = DateTime.UtcNow;
            _status = $"Calibration done. Noise floor: {noiseFloor:0.0}.";
        }

        Log?.Invoke($"Still calibration captured {sampleCount} samples. Noise floor: {noiseFloor:0.0}. Effective threshold: {GetEffectiveThreshold():0.0}.");
        progress?.Report(1);
        PublishSnapshot(force: true);
    }

    public async Task TestRumbleAsync(MotionSettings settings, CancellationToken cancellationToken = default)
    {
        JoyConController? joyCon;
        lock (_stateLock)
        {
            joyCon = _joyCon;
        }

        if (joyCon is null)
        {
            throw new InvalidOperationException("Connect a Joy-Con first.");
        }

        await WriteRumbleAsync(joyCon, new RumbleSet(settings.RumbleFrequencyHz, Clamp01(settings.RumbleAmplitude)), cancellationToken).ConfigureAwait(false);
        await Task.Delay(settings.RumbleOnMilliseconds, cancellationToken).ConfigureAwait(false);
        await WriteRumbleAsync(joyCon, new RumbleSet(), CancellationToken.None).ConfigureAwait(false);
    }

    public double GetEffectiveThreshold()
    {
        lock (_stateLock)
        {
            return EffectiveThresholdNoLock();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync().ConfigureAwait(false);
        _writeGate.Dispose();
    }

    private static string BuildDeviceLabel(HidDevice device)
    {
        string name;
        try
        {
            name = device.GetProductName();
            if (string.IsNullOrWhiteSpace(name))
            {
                name = "Nintendo HID device";
            }
        }
        catch
        {
            name = "Nintendo HID device";
        }

        return $"{name}  VID:{device.VendorID:X4} PID:{device.ProductID:X4}";
    }

    private async Task<CalibrationData?> TryReadCalibrationAsync(JoyConController joyCon, CancellationToken cancellationToken)
    {
        try
        {
            var factoryCalibration = await joyCon.GetFactoryCalibrationAsync(cancellationToken).ConfigureAwait(false);
            var userCalibration = await joyCon.GetUserCalibrationAsync(cancellationToken).ConfigureAwait(false);
            return factoryCalibration + userCalibration;
        }
        catch (Exception ex)
        {
            Log?.Invoke($"Calibration read failed, using raw fallback: {ex.Message}");
            return null;
        }
    }

    private Task HandleReportReceivedAsync(JoyConController source, IJoyConReport report)
    {
        if (report is not InputFullWithImu input)
        {
            return Task.CompletedTask;
        }

        var now = DateTime.UtcNow;
        var frameScores = new List<double>(capacity: 3);

        foreach (var frame in input.Imu.Frames)
        {
            frameScores.Add(ComputeMotionScore(frame));
        }

        var motionScore = frameScores.Count == 0 ? 0 : frameScores.Max();
        bool shouldPublish;
        string? logMessage = null;

        lock (_stateLock)
        {
            _latestMotionScore = motionScore;

            if (_isCalibrating)
            {
                _calibrationSamples.AddRange(frameScores);
            }

            var effectiveThreshold = EffectiveThresholdNoLock();
            if (now >= _ignoreMotionUntilUtc && motionScore >= effectiveThreshold)
            {
                var wasIdle = SecondsSinceMotionNoLock(now) >= _settings.IdleSeconds;
                _lastMotionUtc = now;

                if (wasIdle)
                {
                    _status = "Motion resumed. Watcher active.";
                    logMessage = "Motion resumed.";
                }
                else if (_watching && !_isRumbling)
                {
                    _status = "Watching for pedaling motion.";
                }
            }

            shouldPublish = (now - _lastSnapshotUtc).TotalMilliseconds >= 100;
            if (shouldPublish)
            {
                _lastSnapshotUtc = now;
            }
        }

        if (logMessage is not null)
        {
            Log?.Invoke(logMessage);
        }

        if (shouldPublish)
        {
            PublishSnapshot(force: false);
        }

        return Task.CompletedTask;
    }

    private Task HandleStoppedOnErrorAsync(JoyConController source, Exception exception)
    {
        lock (_stateLock)
        {
            _watching = false;
            _isRumbling = false;
            _status = "Controller disconnected or stopped on error.";
            _lastError = exception.Message;
            _watchdogTimer?.Dispose();
            _watchdogTimer = null;
            _rumbleCts?.Cancel();
        }

        Log?.Invoke($"Controller stopped: {exception.Message}");
        PublishSnapshot(force: true);
        return Task.CompletedTask;
    }

    private double ComputeMotionScore(ImuFrame frame)
    {
        var calibration = _calibration?.ImuCalibration;

        double accelX;
        double accelY;
        double accelZ;
        double gyroX;
        double gyroY;
        double gyroZ;

        if (calibration is not null)
        {
            var calibrated = frame.GetCalibrated(calibration);
            accelX = calibrated.AccelX;
            accelY = calibrated.AccelY;
            accelZ = calibrated.AccelZ;
            gyroX = calibrated.GyroX;
            gyroY = calibrated.GyroY;
            gyroZ = calibrated.GyroZ;
        }
        else
        {
            // Conservative raw fallback for the Joy-Con default +/-8G and +/-2000 dps modes.
            accelX = frame.AccelX / 4096.0;
            accelY = frame.AccelY / 4096.0;
            accelZ = frame.AccelZ / 4096.0;
            gyroX = frame.GyroX / 16.4;
            gyroY = frame.GyroY / 16.4;
            gyroZ = frame.GyroZ / 16.4;
        }

        var gyroMagnitude = Math.Sqrt(gyroX * gyroX + gyroY * gyroY + gyroZ * gyroZ);
        var accelDelta = 0.0;

        lock (_stateLock)
        {
            if (_lastAccel is { } lastAccel)
            {
                var dx = accelX - lastAccel.X;
                var dy = accelY - lastAccel.Y;
                var dz = accelZ - lastAccel.Z;
                accelDelta = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            }

            _lastAccel = (accelX, accelY, accelZ);
        }

        MotionSettings settings;
        lock (_stateLock)
        {
            settings = _settings;
        }

        return gyroMagnitude + accelDelta * settings.AccelDeltaWeight;
    }

    private void WatchdogTick()
    {
        bool startRumbleLoop = false;
        CancellationToken token = default;

        lock (_stateLock)
        {
            var secondsIdle = SecondsSinceMotionNoLock(DateTime.UtcNow);
            var rumbleLoopRunning = _rumbleTask is { IsCompleted: false };

            if (_watching && _joyCon is not null && !rumbleLoopRunning && secondsIdle >= _settings.IdleSeconds)
            {
                _status = "No motion detected. Nudging.";
                startRumbleLoop = true;
                token = _rumbleCts?.Token ?? CancellationToken.None;
                _rumbleTask = Task.Run(() => RumbleUntilMotionAsync(token), token);
            }
        }

        if (startRumbleLoop)
        {
            Log?.Invoke("No motion detected. Starting rumble pulses.");
        }

        PublishSnapshot(force: false);
    }

    private async Task RumbleUntilMotionAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                JoyConController? joyCon;
                MotionSettings settings;
                bool shouldContinue;

                lock (_stateLock)
                {
                    joyCon = _joyCon;
                    settings = _settings;
                    shouldContinue = _watching && joyCon is not null && SecondsSinceMotionNoLock(DateTime.UtcNow) >= settings.IdleSeconds;

                    if (!shouldContinue)
                    {
                        _isRumbling = false;
                        return;
                    }

                    _isRumbling = true;
                    _status = "Vibrating until motion resumes.";
                    _ignoreMotionUntilUtc = DateTime.UtcNow.AddMilliseconds(settings.RumbleOnMilliseconds + settings.RumbleSettleMilliseconds);
                }

                PublishSnapshot(force: true);

                await WriteRumbleAsync(joyCon!, new RumbleSet(settings.RumbleFrequencyHz, Clamp01(settings.RumbleAmplitude)), cancellationToken).ConfigureAwait(false);
                await Task.Delay(settings.RumbleOnMilliseconds, cancellationToken).ConfigureAwait(false);

                await WriteRumbleAsync(joyCon!, new RumbleSet(), cancellationToken).ConfigureAwait(false);

                lock (_stateLock)
                {
                    _isRumbling = false;
                    _status = "Quiet gap: waiting for real motion.";
                    _ignoreMotionUntilUtc = DateTime.UtcNow.AddMilliseconds(settings.RumbleSettleMilliseconds);
                }

                PublishSnapshot(force: true);
                await Task.Delay(settings.RumbleOffMilliseconds, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when the watcher is stopped or the app is closing.
        }
        catch (Exception ex)
        {
            lock (_stateLock)
            {
                _isRumbling = false;
                _lastError = ex.Message;
                _status = "Rumble loop stopped on error.";
            }

            Log?.Invoke($"Rumble loop error: {ex.Message}");
            AppLogger.LogException("Rumble loop error", ex);
        }
        finally
        {
            try
            {
                await StopRumbleAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                AppLogger.LogException("Best-effort stop rumble failed", ex);
            }

            lock (_stateLock)
            {
                _isRumbling = false;
            }

            PublishSnapshot(force: true);
        }
    }

    private async Task StopRumbleAsync(CancellationToken cancellationToken)
    {
        JoyConController? joyCon;
        lock (_stateLock)
        {
            joyCon = _joyCon;
        }

        if (joyCon is null)
        {
            return;
        }

        await WriteRumbleAsync(joyCon, new RumbleSet(), cancellationToken).ConfigureAwait(false);

        lock (_stateLock)
        {
            _isRumbling = false;
        }
    }

    private async Task WriteRumbleAsync(JoyConController joyCon, RumbleSet rumbleSet, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(RumbleWriteTimeout);

        await _writeGate.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
        try
        {
            await joyCon.WriteRumble(rumbleSet, timeoutCts.Token).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private static async Task<bool> WaitForTaskAsync(Task task, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var timeoutTask = Task.Delay(timeout, cancellationToken);
        var completed = await Task.WhenAny(task, timeoutTask).ConfigureAwait(false);

        if (completed != task)
        {
            return false;
        }

        await task.ConfigureAwait(false);
        return true;
    }

    private double EffectiveThresholdNoLock()
    {
        var calibratedFloor = _noiseFloor > 0 ? _noiseFloor + 6.0 : 0.0;
        return Math.Max(_settings.MotionThreshold, calibratedFloor);
    }

    private double SecondsSinceMotionNoLock(DateTime nowUtc)
    {
        return Math.Max(0, (nowUtc - _lastMotionUtc).TotalSeconds);
    }

    private void PublishSnapshot(bool force)
    {
        MonitorSnapshot snapshot;
        lock (_stateLock)
        {
            var now = DateTime.UtcNow;
            var secondsIdle = SecondsSinceMotionNoLock(now);
            var isIdle = _watching && secondsIdle >= _settings.IdleSeconds;
            snapshot = new MonitorSnapshot(
                IsConnected: _joyCon is not null,
                IsWatching: _watching,
                IsIdle: isIdle,
                IsRumbling: _isRumbling,
                MotionScore: _latestMotionScore,
                EffectiveThreshold: EffectiveThresholdNoLock(),
                SecondsSinceMotion: secondsIdle,
                Status: _status,
                DeviceName: _deviceName,
                LastError: _lastError);
        }

        if (force || SnapshotChanged is not null)
        {
            SnapshotChanged?.Invoke(snapshot);
        }
    }

    private static double Percentile(IReadOnlyList<double> values, double percentile)
    {
        if (values.Count == 0)
        {
            return 0;
        }

        var sorted = values.OrderBy(value => value).ToArray();
        var index = Math.Clamp((int)Math.Ceiling(percentile * sorted.Length) - 1, 0, sorted.Length - 1);
        return sorted[index];
    }

    private static double Clamp01(double value) => Math.Clamp(value, 0.0, 1.0);
}
