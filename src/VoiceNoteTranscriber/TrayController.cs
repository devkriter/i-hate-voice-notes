using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using System.Windows.Threading;
using VoiceNoteTranscriber.UI;
using Forms = System.Windows.Forms;

namespace VoiceNoteTranscriber;

/// <summary>The tray icon, its menu, and the queue that transcribes voice notes one at a time.</summary>
public sealed class TrayController : IDisposable
{
    private readonly Dispatcher _ui = Dispatcher.CurrentDispatcher;
    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.ToolStripMenuItem _languageMenu;
    private readonly Forms.ToolStripMenuItem _autostartItem;
    private readonly TranscriptsPopup _popup;
    private readonly Settings _settings;
    private readonly Transcriber _transcriber = new();
    private readonly Channel<Transcript> _queue = Channel.CreateUnbounded<Transcript>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _shutdown = new();

    /// <summary>Newest first; the popup shows these as cards.</summary>
    public ObservableCollection<Transcript> Items { get; } = [];

    // A transcript finished while the popup was hidden: the icon turns green until it's opened.
    private bool _unseen;
    private string? _drawnState;

    public TrayController(Settings settings)
    {
        _settings = settings;

        var menu = new Forms.ContextMenuStrip { ShowImageMargin = false };
        var showItem = new Forms.ToolStripMenuItem("Show voice notes", null, (_, _) => ShowPopup());
        showItem.Font = new Font(showItem.Font, System.Drawing.FontStyle.Bold); // the click action
        menu.Items.Add(showItem);
        menu.Items.Add(new Forms.ToolStripMenuItem("Open voice notes…", null, (_, _) => _popup!.PickFiles()));
        menu.Items.Add(new Forms.ToolStripMenuItem("Paste voice note", null, (_, _) => Paste()));
        menu.Items.Add(new Forms.ToolStripSeparator());
        _languageMenu = new Forms.ToolStripMenuItem("Language");
        foreach (var (code, label) in Settings.Languages)
            _languageMenu.DropDownItems.Add(new Forms.ToolStripMenuItem(label, null, (_, _) => SetLanguage(code)) { Tag = code });
        menu.Items.Add(_languageMenu);
        _autostartItem = new Forms.ToolStripMenuItem("Start with Windows", null, (_, _) => ToggleAutostart());
        menu.Items.Add(_autostartItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(new Forms.ToolStripMenuItem("Exit", null, (_, _) => Exit()));
        menu.Opening += (_, _) => RefreshMenu();

        _icon = new Forms.NotifyIcon
        {
            ContextMenuStrip = menu,
            Text = "I hate voice notes",
            Icon = TrayIconRenderer.DrawIcon("idle", IconSize),
            Visible = true,
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left)
                _popup!.Toggle();
        };
        _icon.BalloonTipClicked += (_, _) => ShowPopup();

        _popup = new TranscriptsPopup(this, settings);
        _popup.IsVisibleChanged += (_, _) =>
        {
            if (_popup.IsVisible)
                _unseen = false;
            RefreshIcon();
        };
        _ = Task.Run(() => WorkLoopAsync(_shutdown.Token));
    }

    private static int IconSize => Forms.SystemInformation.SmallIconSize.Width;

    // ---- menu ------------------------------------------------------------

    private void RefreshMenu()
    {
        foreach (Forms.ToolStripMenuItem item in _languageMenu.DropDownItems)
            item.Checked = (string)item.Tag! == _settings.Language;
        _autostartItem.Checked = Autostart.IsEnabled;
    }

    public void SetLanguage(string code)
    {
        _settings.Language = code;
        _settings.Save();
        _popup.ShowLanguage();
    }

    private void ToggleAutostart()
    {
        try
        {
            Autostart.SetEnabled(!Autostart.IsEnabled);
        }
        catch (Exception e)
        {
            App.LogError("Autostart", e);
        }
    }

    public void ShowPopup() => _popup.ShowNearTray();

    private void Exit()
    {
        Dispose();
        System.Windows.Application.Current.Shutdown();
    }

    // ---- queue -------------------------------------------------------------

    /// <summary>Queues voice notes and shows the popup with their cards (UI thread).</summary>
    public void AddFiles(string[] paths)
    {
        foreach (var path in paths.Where(File.Exists))
        {
            var item = new Transcript(path);
            Items.Insert(0, item);
            _queue.Writer.TryWrite(item);
        }
        ShowPopup();
        RefreshIcon();
    }

    /// <summary>Queues the audio files on the clipboard (Ctrl+V in the popup, or the tray menu).</summary>
    public async void Paste()
    {
        var files = await ClipboardFiles.GetAsync();
        if (files.Length > 0)
        {
            AddFiles(files);
            return;
        }
        ShowPopup();
        _popup.ShowHint("Nothing to paste: copy a voice note first");
    }

    /// <summary>Removes finished cards (UI thread).</summary>
    public void ClearFinished()
    {
        foreach (var item in Items.Where(i => !i.IsBusy).ToList())
            Items.Remove(item);
        _unseen = false;
        RefreshIcon();
    }

    private async Task WorkLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var item in _queue.Reader.ReadAllAsync(ct))
                await TranscribeAsync(item, ct);
        }
        catch (OperationCanceledException) { }
    }

    private async Task TranscribeAsync(Transcript item, CancellationToken ct)
    {
        string language = "auto";
        await _ui.InvokeAsync(() =>
        {
            if (!Items.Contains(item))
                return;
            item.State = TranscriptState.Working;
            item.Status = "Reading the voice note…";
            language = _settings.Language;
            RefreshIcon();
        });
        try
        {
            var samples = await Task.Run(() => AudioDecoder.Decode(item.Path), ct);
            double seconds = samples.Length / (double)AudioDecoder.SampleRate;

            if (!_transcriber.IsLoaded)
            {
                if (!Transcriber.IsDownloaded)
                    await _ui.InvokeAsync(() => item.Status = "Downloading the speech model (once, ~570 MB)…");
                await _transcriber.LoadAsync(
                    p => _ui.InvokeAsync(() => item.Progress = p),
                    () => _ui.InvokeAsync(() => (item.Status, item.Progress) = ("Loading the speech model…", null)),
                    ct);
            }
            await _ui.InvokeAsync(() => item.Status = "Transcribing…");

            var watch = Stopwatch.StartNew();
            var result = await _transcriber.TranscribeAsync(samples, language, ct);
            watch.Stop();

            await _ui.InvokeAsync(() =>
            {
                item.Progress = null;
                item.Text = result.Text.Length > 0 ? result.Text : "(no speech found)";
                item.Detail = $"{LanguageName(result.Language)} · {Duration(seconds)} · " +
                              $"{watch.Elapsed.TotalSeconds:0.0} s on {_transcriber.Device}";
                item.State = TranscriptState.Done;
                Finished(item, "Voice note transcribed", item.Text);
            });
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e)
        {
            App.LogError($"Transcribing {item.Path}", e);
            await _ui.InvokeAsync(() =>
            {
                item.Progress = null;
                item.Text = $"Couldn't transcribe this file: {e.Message}";
                item.State = TranscriptState.Failed;
                Finished(item, "Couldn't transcribe", item.FileName);
            });
        }
    }

    /// <summary>Hidden popup: a notification with the text, and a green icon until it's opened.</summary>
    private void Finished(Transcript item, string title, string body)
    {
        if (!_popup.IsVisible)
        {
            _unseen = true;
            // Windows cuts notifications short anyway; most voice notes fit.
            _icon.ShowBalloonTip(10000, title, body.Length > 240 ? body[..240] + "…" : body,
                                 item.State == TranscriptState.Failed ? Forms.ToolTipIcon.Warning : Forms.ToolTipIcon.None);
        }
        RefreshIcon();
    }

    private void RefreshIcon()
    {
        int busy = Items.Count(i => i.IsBusy);
        string state = busy > 0 ? "working" : _unseen ? "ready" : "idle";
        _icon.Text = busy switch
        {
            0 => _unseen ? "I hate voice notes\nA transcript is ready" : "I hate voice notes",
            1 => "I hate voice notes\nTranscribing a voice note…",
            _ => $"I hate voice notes\nTranscribing {busy} voice notes…",
        };
        _popup.ShowState(state, busy);
        if (state == _drawnState)
            return;
        _drawnState = state;
        var old = _icon.Icon;
        _icon.Icon = TrayIconRenderer.DrawIcon(state, IconSize);
        old?.Dispose();
    }

    private static string LanguageName(string code) => code switch
    {
        "en" => "English",
        "tr" => "Türkçe",
        _ => code.ToUpperInvariant(),
    };

    private static string Duration(double seconds) =>
        TimeSpan.FromSeconds(Math.Round(seconds)).ToString(seconds >= 3600 ? @"h\:mm\:ss" : @"m\:ss");

    public void Dispose()
    {
        if (_shutdown.IsCancellationRequested)
            return;
        _shutdown.Cancel();
        _popup.Shutdown();
        _icon.Visible = false;
        _icon.Icon?.Dispose();
        _icon.Dispose();
        _transcriber.Dispose(); // the worker may still be inside it; we're exiting anyway
    }
}
