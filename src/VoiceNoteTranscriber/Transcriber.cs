using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Whisper.net;
using Whisper.net.LibraryLoader;

namespace VoiceNoteTranscriber;

/// <summary>
/// Whisper large-v3-turbo through whisper.cpp: on the GPU via Vulkan when there is one,
/// otherwise on the CPU. The model is downloaded on first use and kept in the data folder.
/// Not thread-safe: the tray's worker runs one voice note at a time.
/// </summary>
public sealed class Transcriber : IDisposable
{
    private const string ModelFile = "ggml-large-v3-turbo-q5_0.bin";
    private const string ModelUrl = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/" + ModelFile;

    private WhisperFactory? _factory;

    public static string ModelPath => Path.Combine(Settings.DataDir, "models", ModelFile);
    public static bool IsDownloaded => File.Exists(ModelPath);
    public bool IsLoaded => _factory != null;

    /// <summary>"GPU" or "CPU", once the model is loaded.</summary>
    public string Device { get; private set; } = "";

    public sealed record Result(string Text, string Language);

    /// <summary>Downloads (first use only) and loads the model.</summary>
    /// <param name="downloadProgress">0..1 while the model downloads.</param>
    /// <param name="loading">Called once the download is done and loading starts.</param>
    public async Task LoadAsync(Action<double> downloadProgress, Action loading, CancellationToken ct)
    {
        if (_factory != null)
            return;
        if (!IsDownloaded)
            await DownloadModelAsync(downloadProgress, ct);
        loading();
        _factory = await Task.Run(() => WhisperFactory.FromPath(ModelPath), ct);
        Device = RuntimeOptions.LoadedLibrary is RuntimeLibrary.Cpu or RuntimeLibrary.CpuNoAvx or null ? "CPU" : "GPU";
    }

    /// <param name="language">A Whisper language code, or "auto".</param>
    public async Task<Result> TranscribeAsync(float[] samples, string language, CancellationToken ct)
    {
        if (_factory == null)
            throw new InvalidOperationException("Load the model first.");
        var builder = _factory.CreateBuilder().WithLanguage(language);
        if (language == "auto")
            builder = builder.WithLanguageDetection();
        using var processor = builder.Build();

        var parts = new List<string>();
        string? detected = null;
        await foreach (var segment in processor.ProcessAsync(samples, ct))
        {
            parts.Add(segment.Text.Trim());
            detected ??= segment.Language;
        }
        return new Result(string.Join(" ", parts).Trim(), detected ?? language);
    }

    private static async Task DownloadModelAsync(Action<double> progress, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ModelPath)!);
        string partial = ModelPath + ".part";
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        using var response = await http.GetAsync(ModelUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        long total = response.Content.Headers.ContentLength ?? 0;
        await using (var source = await response.Content.ReadAsStreamAsync(ct))
        await using (var target = File.Create(partial))
        {
            var buffer = new byte[1 << 20];
            long done = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, ct)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), ct);
                done += read;
                if (total > 0)
                    progress((double)done / total);
            }
        }
        // Only a complete download gets the real name, so a cut-off one is never loaded.
        File.Move(partial, ModelPath, overwrite: true);
    }

    public void Dispose() => _factory?.Dispose();
}
