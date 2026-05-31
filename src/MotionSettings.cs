namespace PedalNudge.Windows;

public sealed record MotionSettings
{
    public int IdleSeconds { get; init; } = 8;
    public double MotionThreshold { get; init; } = 35.0;
    public double AccelDeltaWeight { get; init; } = 180.0;
    public double RumbleFrequencyHz { get; init; } = 160.0;
    public double RumbleAmplitude { get; init; } = 0.55;
    public int RumbleOnMilliseconds { get; init; } = 250;
    public int RumbleOffMilliseconds { get; init; } = 850;
    public int RumbleSettleMilliseconds { get; init; } = 300;
}
