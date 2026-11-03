using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;

namespace VoiceNoteTranscriber;

public enum TranscriptState { Queued, Working, Done, Failed }

/// <summary>One voice note and its card in the popup. Changed on the UI thread only.</summary>
public sealed class Transcript(string path) : INotifyPropertyChanged
{
    public string Path { get; } = path;
    public string FileName { get; } = System.IO.Path.GetFileName(path);

    public TranscriptState State { get => _state; set => Set(ref _state, value, nameof(IsBusy)); }
    private TranscriptState _state;

    /// <summary>What's happening while it's busy ("Transcribing…").</summary>
    public string Status { get => _status; set => Set(ref _status, value); }
    private string _status = "Waiting…";

    /// <summary>0..1 for the model download; null shows a running bar.</summary>
    public double? Progress { get => _progress; set => Set(ref _progress, value, nameof(HasProgress), nameof(ProgressValue)); }
    private double? _progress;

    /// <summary>The transcript; editable in the card, so a misheard word can be fixed before copying.</summary>
    public string Text { get => _text; set => Set(ref _text, value); }
    private string _text = "";

    /// <summary>Under the file name once done: "Türkçe · 0:48 · 1.9 s on GPU".</summary>
    public string Detail { get => _detail; set => Set(ref _detail, value); }
    private string _detail = "";

    public string CopyLabel { get => _copyLabel; set => Set(ref _copyLabel, value); }
    private string _copyLabel = "Copy";

    public bool IsBusy => State is TranscriptState.Queued or TranscriptState.Working;
    public bool HasProgress => Progress != null;
    public double ProgressValue => Progress ?? 0;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <param name="also">Computed properties that change with this one.</param>
    private void Set<T>(ref T field, T value, string? also = null, string? also2 = null,
                        [CallerMemberName] string? name = null)
    {
        if (Equals(field, value))
            return;
        field = value;
        foreach (var changed in new[] { name, also, also2 })
            if (changed != null)
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(changed));
    }
}
