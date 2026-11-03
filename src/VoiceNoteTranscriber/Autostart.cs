using System;
using Microsoft.Win32;

namespace VoiceNoteTranscriber;

/// <summary>"Start with Windows" via the per-user Run registry key (no admin needed).</summary>
public static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "VoiceNoteTranscriber";

    // Starts quietly in the tray rather than opening the window at sign-in.
    private static string LaunchCommand => $"\"{Environment.ProcessPath}\" --background";

    private static string? Read()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) as string;
    }

    public static bool IsEnabled => Read() != null;

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        if (enabled)
            key.SetValue(ValueName, LaunchCommand);
        else
            key.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    /// <summary>If autostart is on but points at another copy (the app was moved), repoint it at this one.</summary>
    public static void RefreshPath()
    {
        try
        {
            string? current = Read();
            if (current != null && !string.Equals(current, LaunchCommand, StringComparison.OrdinalIgnoreCase))
                SetEnabled(true);
        }
        catch (Exception)
        {
            // Not worth failing startup over.
        }
    }
}
