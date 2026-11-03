using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace VoiceNoteTranscriber.UI;

/// <summary>
/// The tray's flyout: a card per voice note, newest on top. Like a tray popup it
/// hides when you click elsewhere, unless pinned (pin it to drag files onto it).
/// </summary>
public partial class TranscriptsPopup : Window
{
    private static readonly TimeSpan ToggleWindow = TimeSpan.FromMilliseconds(300);
    private static readonly Brush PinnedBrush = Frozen(Color.FromRgb(0x60, 0xCD, 0xFF));
    private static readonly Brush DropBorder = Frozen(Color.FromRgb(0x3A, 0x3A, 0x3A));

    public sealed record LanguageOption(string Code, string Label);

    private readonly TrayController _tray;
    private readonly Settings _settings;
    private DateTime _hiddenAt;
    private DateTime _deactivatedAt;
    // Pinned: stays open, can sit behind other windows and remembers where
    // you put it, like a normal app. For this run of the app only.
    private bool _pinned;
    // Hide on focus loss. Off while a popup that files opened hasn't been touched yet:
    // the app that sent them (WhatsApp) may take focus straight back, and the
    // transcript should still be there to read.
    private bool _autoHide;
    // The file picker takes focus; that mustn't close the popup under it.
    private bool _dialogOpen;
    private bool _dragging;
    private bool _settingLanguage;
    private bool _isShutdown;

    public TranscriptsPopup(TrayController tray, Settings settings)
    {
        InitializeComponent();
        _tray = tray;
        _settings = settings;

        Cards.ItemsSource = tray.Items;
        tray.Items.CollectionChanged += (_, _) => UpdateEmpty();
        LanguageBox.ItemsSource = Settings.Languages.Select(l => new LanguageOption(l.Code, l.Label)).ToList();
        ShowLanguage();
        UpdateEmpty();

        Deactivated += (_, _) =>
        {
            _deactivatedAt = DateTime.UtcNow;
            if (IsVisible && _autoHide && !_pinned && !_dialogOpen)
                HidePopup();
        };
        PreviewMouseDown += (_, _) => _autoHide = true;
        StateChanged += (_, _) => FitMaximized();
        KeyDown += (_, e) => { if (e.Key == Key.Escape) HidePopup(); };
        PreviewKeyDown += OnPreviewKeyDown;
        DragEnter += OnDragOver;
        DragOver += OnDragOver;
        DragLeave += (_, _) => EndDrag();
        Drop += OnDrop;
        // Make the native window now, and render it once off-screen, so the
        // first real show doesn't pay for window creation or first-render setup.
        new WindowInteropHelper(this).EnsureHandle();
        WarmUp();
    }

    private void WarmUp()
    {
        bool showActivated = ShowActivated;
        ShowActivated = false;
        Left = Top = -32000; // off every screen
        Show();
        UpdateLayout();
        Hide();
        ShowActivated = showActivated;
    }

    // ---- show / hide ---------------------------------------------------------

    /// <summary>Tray click.</summary>
    public void Toggle()
    {
        // Pinned and behind other windows: bring it forward rather than closing it.
        // (A tray click takes focus first, so "was it in front" means it lost focus just now.)
        if (IsVisible && _pinned && DateTime.UtcNow - _deactivatedAt > ToggleWindow)
            ShowAt(nearCursor: true);
        else if (IsVisible)
            HidePopup();
        else if (DateTime.UtcNow - _hiddenAt > ToggleWindow)
            ShowAt(nearCursor: true);
        // else: this click is what just closed it.
    }

    /// <summary>Files arrived (Open with, a second launch): show in the corner by the tray.</summary>
    public void ShowNearTray() => ShowAt(nearCursor: false);

    private void ShowAt(bool nearCursor)
    {
        if (_isShutdown)
            return;
        // A tray click is a flyout from the start; files wait to be read (see _autoHide).
        _autoHide = nearCursor;
        if (IsVisible)
        {
            Activate();
            Native.SetForegroundWindow(new WindowInteropHelper(this).Handle);
            return;
        }
        if (!_pinned)
        {
            // Unpinned it's a popup again: default size, by the tray.
            WindowState = WindowState.Normal;
            Width = 440;
            Height = 560;
            Position(nearCursor);
        }
        Show();
        Activate();
        Native.SetForegroundWindow(new WindowInteropHelper(this).Handle);
    }

    /// <summary>
    /// Centered above the cursor (a tray click: the tray is usually bottom-right), or in
    /// the bottom-right corner of the cursor's monitor. Kept on that monitor either way.
    /// </summary>
    private void Position(bool nearCursor)
    {
        var (cursor, work, scale) = Native.CursorMonitor();
        int width = (int)Math.Round(Width * scale), height = (int)Math.Round(Height * scale);
        int margin = (int)(12 * scale);
        int x = nearCursor ? cursor.X - width / 2 : work.Right - width - margin;
        int y = nearCursor ? cursor.Y - height - (int)(20 * scale) : work.Bottom - height - margin;
        x = Math.Max(work.Left, Math.Min(work.Right - width, x));
        y = Math.Max(work.Top, Math.Min(work.Bottom - height, y));
        var hwnd = new WindowInteropHelper(this).Handle;
        Native.SetWindowPos(hwnd, IntPtr.Zero, x, y, width, height, Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
    }

    /// <summary>
    /// A maximized window without a title bar overhangs the screen by its
    /// resize border; pad it back in so nothing is cut off.
    /// </summary>
    private void FitMaximized()
    {
        var border = SystemParameters.WindowResizeBorderThickness;
        Frame.Margin = WindowState == WindowState.Maximized
            ? new Thickness(border.Left + 4, border.Top + 4, border.Right + 4, border.Bottom + 4)
            : new Thickness(0);
    }

    private void HidePopup()
    {
        if (!IsVisible)
            return;
        _hiddenAt = DateTime.UtcNow;
        Hide();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => HidePopup();

    private void OnPinClick(object sender, RoutedEventArgs e)
    {
        _pinned = !_pinned;
        // Pinned it's a normal window: other windows can cover it, and it has a
        // taskbar button so you can get back to it.
        Topmost = !_pinned;
        ShowInTaskbar = _pinned;
        // Filled: the fill glyph has no needle, so it's drawn over the outline.
        PinButton.Content = _pinned
            ? new Grid { Children = { new TextBlock { Text = "" }, new TextBlock { Text = "" } } }
            : "";
        PinButton.Foreground = _pinned ? PinnedBrush : Brushes.White;
        PinButton.ToolTip = _pinned ? "Unpin: close when you click elsewhere"
                                    : "Pin: keep open when you click elsewhere (and to drag files in)";
        Activate();
    }

    /// <summary>Closes the window for good (the app is exiting).</summary>
    public void Shutdown()
    {
        if (_isShutdown)
            return;
        _isShutdown = true;
        Close();
    }

    // ---- state from the tray ------------------------------------------------

    /// <summary>The title bar's dot follows the tray icon's color.</summary>
    public void ShowState(string state, int busy)
    {
        var c = TrayIconRenderer.StateColor(state);
        StateDot.Fill = Frozen(Color.FromRgb(c.R, c.G, c.B));
        TitleDetail.Text = busy switch
        {
            0 => "",
            1 => "· transcribing 1",
            _ => $"· transcribing {busy}",
        };
        UpdateEmpty();
    }

    public void ShowLanguage()
    {
        _settingLanguage = true;
        LanguageBox.SelectedItem = ((System.Collections.Generic.List<LanguageOption>)LanguageBox.ItemsSource)
            .FirstOrDefault(l => l.Code == _settings.Language);
        _settingLanguage = false;
    }

    private void OnLanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_settingLanguage && LanguageBox.SelectedItem is LanguageOption option)
            _tray.SetLanguage(option.Code);
    }

    private void UpdateEmpty()
    {
        bool empty = _tray.Items.Count == 0;
        DropHint.Visibility = empty || _dragging ? Visibility.Visible : Visibility.Collapsed;
        DropTitle.Text = _dragging ? "Drop to transcribe" : "No voice notes yet";
        DropHint.BorderBrush = _dragging ? PinnedBrush : DropBorder;
        CopyAllButton.IsEnabled = _tray.Items.Any(i => i.State == TranscriptState.Done);
        ClearButton.IsEnabled = _tray.Items.Any(i => !i.IsBusy);
    }

    // ---- files in -------------------------------------------------------------

    private void OnOpenClick(object sender, RoutedEventArgs e) => PickFiles();

    public void PickFiles()
    {
        if (_dialogOpen)
            return;
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Open voice notes",
            Multiselect = true,
            Filter = "Voice notes and audio|" + string.Join(";", OpenWith.Extensions.Select(x => "*" + x)) + "|All files|*.*",
        };
        _dialogOpen = true;
        bool picked;
        try
        {
            picked = IsVisible ? dialog.ShowDialog(this) == true : dialog.ShowDialog() == true;
        }
        finally
        {
            _dialogOpen = false;
        }
        if (picked)
            _tray.AddFiles(dialog.FileNames);
    }

    /// <summary>Ctrl+V pastes voice notes, except text into a transcript being edited.</summary>
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.V || Keyboard.Modifiers != ModifierKeys.Control)
            return;
        if (Keyboard.FocusedElement is TextBox && Clipboard.ContainsText())
            return;
        e.Handled = true;
        _tray.Paste();
    }

    /// <summary>A short message in the title bar, e.g. when there's nothing to paste.</summary>
    public async void ShowHint(string text)
    {
        TitleDetail.Text = "· " + text;
        await Task.Delay(3000);
        if (TitleDetail.Text == "· " + text)
            TitleDetail.Text = "";
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
        if (!_dragging && e.Effects == DragDropEffects.Copy)
        {
            _dragging = true;
            UpdateEmpty();
        }
    }

    private void EndDrag()
    {
        _dragging = false;
        UpdateEmpty();
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        EndDrag();
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
            _tray.AddFiles(files);
    }

    // ---- copy / clear ------------------------------------------------------------

    private async void OnCopyClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not Transcript item || !TryCopy(item.Text))
            return;
        item.CopyLabel = "Copied";
        await Task.Delay(1500);
        item.CopyLabel = "Copy";
    }

    private async void OnCopyAllClick(object sender, RoutedEventArgs e)
    {
        // Oldest first, the order they were said in.
        var texts = _tray.Items.Where(i => i.State == TranscriptState.Done).Reverse().Select(i => i.Text);
        if (!TryCopy(string.Join("\n\n", texts)))
            return;
        CopyAllButton.Content = "Copied";
        await Task.Delay(1500);
        CopyAllButton.Content = "Copy all";
    }

    private static bool TryCopy(string text)
    {
        try
        {
            Clipboard.SetText(text);
            return true;
        }
        catch (Exception e) // another app has the clipboard open
        {
            App.LogError("Copying", e);
            return false;
        }
    }

    private void OnClearClick(object sender, RoutedEventArgs e) => _tray.ClearFinished();

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
