namespace PedalNudge.Windows;

public sealed record MonitorSnapshot(
    bool IsConnected,
    bool IsWatching,
    bool IsIdle,
    bool IsRumbling,
    double MotionScore,
    double EffectiveThreshold,
    double SecondsSinceMotion,
    string Status,
    string DeviceName,
    string? LastError = null);
