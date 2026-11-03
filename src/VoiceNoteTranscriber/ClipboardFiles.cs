using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace VoiceNoteTranscriber;

/// <summary>
/// Ctrl+V: audio files copied in Explorer or with the Copy button in the Windows
/// Share window (WhatsApp → Share → Copy).
/// </summary>
internal static class ClipboardFiles
{
    /// <summary>Our copies of pasted files.</summary>
    private static string PastedDir => Path.Combine(Settings.DataDir, "pasted");

    /// <summary>Deletes pasted copies from earlier days.</summary>
    public static void CleanUp()
    {
        try
        {
            if (!Directory.Exists(PastedDir))
                return;
            foreach (var file in new DirectoryInfo(PastedDir).GetFiles())
                if (file.LastWriteTimeUtc < DateTime.UtcNow.AddDays(-1))
                    file.Delete();
        }
        catch (Exception) { } // in use by a transcription; next time
    }

    /// <summary>Copies of the audio files on the clipboard; empty if there are none (UI thread).</summary>
    public static async Task<string[]> GetAsync()
    {
        var sources = new List<string>();
        try
        {
            // Explorer's copy, and most apps: a plain file list.
            if (System.Windows.Clipboard.ContainsFileDropList())
                sources.AddRange(System.Windows.Clipboard.GetFileDropList().Cast<string>());
            else
            {
                // Packaged apps and the Share window may only offer WinRT storage items.
                var content = Clipboard.GetContent();
                if (content.Contains(StandardDataFormats.StorageItems))
                {
                    var copies = new List<string>();
                    foreach (var item in await content.GetStorageItemsAsync())
                        if (item is StorageFile file && IsAudio(file.Name))
                            copies.Add(await CopyInAsync(file)); // may have no path on disk at all
                    return copies.ToArray();
                }
            }
        }
        catch (Exception e) // another app has the clipboard open
        {
            App.LogError("Reading the clipboard", e);
            return [];
        }

        var paths = new List<string>();
        foreach (var source in sources.Where(IsAudio).Where(File.Exists))
        {
            try
            {
                // Our own copy: what the Share window hands out may be a temporary file.
                Directory.CreateDirectory(PastedDir);
                string copy = UniquePath(Path.Combine(PastedDir, Path.GetFileName(source)));
                File.Copy(source, copy);
                paths.Add(copy);
            }
            catch (Exception e)
            {
                App.LogError($"Copying pasted {source}", e);
                paths.Add(source); // try the original
            }
        }
        return paths.ToArray();
    }

    private static async Task<string> CopyInAsync(StorageFile file)
    {
        Directory.CreateDirectory(PastedDir);
        var folder = await StorageFolder.GetFolderFromPathAsync(PastedDir);
        return (await file.CopyAsync(folder, file.Name, NameCollisionOption.GenerateUniqueName)).Path;
    }

    private static bool IsAudio(string path) =>
        OpenWith.Extensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    private static string UniquePath(string path)
    {
        string dir = Path.GetDirectoryName(path)!, name = Path.GetFileNameWithoutExtension(path), ext = Path.GetExtension(path);
        for (int i = 2; File.Exists(path); i++)
            path = Path.Combine(dir, $"{name} ({i}){ext}");
        return path;
    }
}
