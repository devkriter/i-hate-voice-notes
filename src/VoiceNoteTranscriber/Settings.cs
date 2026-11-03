using System;
using System.IO;
using System.Text.Json;

namespace VoiceNoteTranscriber;

/// <summary>The few choices the app remembers, in %LOCALAPPDATA%\VoiceNoteTranscriber\settings.json.</summary>
public sealed class Settings
{
    /// <summary>Whisper language code, or "auto" to detect it per voice note.</summary>
    public string Language { get; set; } = "auto";

    public static readonly string DataDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VoiceNoteTranscriber");

    private static string FilePath => Path.Combine(DataDir, "settings.json");

    /// <summary>The menu's languages: (Whisper code, label).</summary>
    public static readonly (string Code, string Label)[] Languages =
    [
        ("auto", "Detect automatically"),
        ("en", "English"),
        ("tr", "Türkçe"),
    ];

    public static Settings Load()
    {
        try
        {
            return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
        }
        catch (Exception)
        {
            return new Settings(); // first run, or a bad hand edit
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception e)
        {
            App.LogError("Saving settings", e);
        }
    }
}
