using System;
using System.IO;

namespace PedalNudge.Windows;

public static class AppLogger
{
    private static readonly object Gate = new();

    public static string LogDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "PedalNudge");

    public static string LogPath { get; } = Path.Combine(LogDirectory, "pedal-nudge.log");

    public static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);
            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}";

            lock (Gate)
            {
                File.AppendAllText(LogPath, line);
            }
        }
        catch
        {
            // Logging must never crash the app.
        }
    }

    public static void LogException(string context, Exception exception)
    {
        Log($"{context}: {exception.GetType().FullName}: {exception.Message}{Environment.NewLine}{exception}");
    }
}
