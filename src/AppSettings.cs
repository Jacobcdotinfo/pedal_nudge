using System;
using System.IO;
using System.Text.Json;

namespace PedalNudge.Windows;

public sealed class AppSettings
{
    public int IdleSeconds { get; set; } = 8;
    public double MotionThreshold { get; set; } = 35.0;
    public double RumbleFrequencyHz { get; set; } = 160.0;
    public double RumbleAmplitude { get; set; } = 0.55;
    public int RumbleOnMilliseconds { get; set; } = 250;
    public int RumbleOffMilliseconds { get; set; } = 850;

    public static AppSettings FromMotionSettings(MotionSettings settings)
    {
        return new AppSettings
        {
            IdleSeconds = settings.IdleSeconds,
            MotionThreshold = settings.MotionThreshold,
            RumbleFrequencyHz = settings.RumbleFrequencyHz,
            RumbleAmplitude = settings.RumbleAmplitude,
            RumbleOnMilliseconds = settings.RumbleOnMilliseconds,
            RumbleOffMilliseconds = settings.RumbleOffMilliseconds
        };
    }
}

public static class AppSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        WriteIndented = true
    };

    public static string SettingsDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "PedalNudge");

    public static string SettingsPath { get; } = Path.Combine(SettingsDirectory, "settings.json");

    public static bool SettingsFileExists => File.Exists(SettingsPath);

    public static AppSettings Load(out string? error)
    {
        error = null;

        try
        {
            if (!File.Exists(SettingsPath))
            {
                return new AppSettings();
            }

            var json = File.ReadAllText(SettingsPath);
            return JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return new AppSettings();
        }
    }

    public static bool Save(AppSettings settings, out string? error)
    {
        error = null;

        try
        {
            Directory.CreateDirectory(SettingsDirectory);
            var json = JsonSerializer.Serialize(settings, JsonOptions);
            File.WriteAllText(SettingsPath, json);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
