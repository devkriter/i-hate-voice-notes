using System;
using Microsoft.Win32;

namespace VoiceNoteTranscriber;

/// <summary>
/// Puts the app in "Open with" for audio files (WhatsApp Desktop's voice note menu
/// uses it too), for the current user only. It never becomes the default app.
/// </summary>
public static class OpenWith
{
    private const string ProgId = "VoiceNoteTranscriber.Audio";
    private const string Classes = @"Software\Classes\";
    public static readonly string[] Extensions = [".opus", ".ogg", ".oga", ".m4a", ".mp3", ".wav", ".aac", ".wma", ".mp4"];

    /// <summary>Points the entries at this copy of the exe (run at every start, so moving the app just works).</summary>
    public static void Register()
    {
        try
        {
            string exe = Environment.ProcessPath!;
            using (var prog = Registry.CurrentUser.CreateSubKey(Classes + ProgId))
            {
                prog.SetValue("", "Voice note");
                prog.SetValue("FriendlyTypeName", "Voice note");
            }
            using (var icon = Registry.CurrentUser.CreateSubKey(Classes + ProgId + @"\DefaultIcon"))
                icon.SetValue("", $"\"{exe}\",0");
            using (var open = Registry.CurrentUser.CreateSubKey(Classes + ProgId + @"\shell\open"))
                open.SetValue("FriendlyAppName", "I hate voice notes");
            using (var command = Registry.CurrentUser.CreateSubKey(Classes + ProgId + @"\shell\open\command"))
                command.SetValue("", $"\"{exe}\" \"%1\"");
            foreach (var ext in Extensions)
            {
                using var key = Registry.CurrentUser.CreateSubKey(Classes + ext + @"\OpenWithProgids");
                key.SetValue(ProgId, Array.Empty<byte>(), RegistryValueKind.None);
            }
            Native.SHChangeNotify(Native.SHCNE_ASSOCCHANGED, 0, IntPtr.Zero, IntPtr.Zero);
        }
        catch (Exception e)
        {
            App.LogError("Registering Open with", e);
        }
    }

    public static void Unregister()
    {
        Registry.CurrentUser.DeleteSubKeyTree(Classes + ProgId, throwOnMissingSubKey: false);
        foreach (var ext in Extensions)
        {
            using var key = Registry.CurrentUser.OpenSubKey(Classes + ext + @"\OpenWithProgids", writable: true);
            key?.DeleteValue(ProgId, throwOnMissingValue: false);
        }
        Native.SHChangeNotify(Native.SHCNE_ASSOCCHANGED, 0, IntPtr.Zero, IntPtr.Zero);
    }
}
