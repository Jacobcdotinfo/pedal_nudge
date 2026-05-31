using HidSharp;

namespace PedalNudge.Windows;

public sealed class JoyConDeviceOption
{
    public JoyConDeviceOption(HidDevice device, string displayName)
    {
        Device = device;
        DisplayName = displayName;
    }

    public HidDevice Device { get; }
    public string DisplayName { get; }

    public override string ToString() => DisplayName;
}
