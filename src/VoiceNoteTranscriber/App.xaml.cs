using System;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace VoiceNoteTranscriber;

public partial class App : Application
{
    // Per user: another account on the same PC runs its own copy.
    private static readonly string InstanceName = "VoiceNoteTranscriber-" + Environment.UserName;

    private Mutex? _singleInstance;
    private TrayController? _tray;
    private readonly CancellationTokenSource _shutdown = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandledException;
        // The tray menu is WinForms: give it modern visuals and follow dark mode.
        System.Windows.Forms.Application.EnableVisualStyles();
#pragma warning disable WFO5001 // dark mode for WinForms is marked experimental
        System.Windows.Forms.Application.SetColorMode(System.Windows.Forms.SystemColorMode.System);
#pragma warning restore WFO5001
        TaskScheduler.UnobservedTaskException += (_, args) => args.SetObserved();

        if (e.Args.Contains("--unregister"))
        {
            OpenWith.Unregister();
            Autostart.SetEnabled(false);
            Shutdown();
            return;
        }

        var files = e.Args.Where(a => !a.StartsWith("--")).Select(Path.GetFullPath).ToArray();

        // One tray icon per user. "Open with" on several files starts one copy per
        // file: the extra copies hand their file to the first and exit.
        _singleInstance = new Mutex(true, @"Local\" + InstanceName, out bool first);
        if (!first)
        {
            // We were just launched by the user, so we may pass on the right to take the foreground.
            Native.AllowSetForegroundWindow(Native.ASFW_ANY);
            SendToRunningCopy(files);
            Shutdown();
            return;
        }

        OpenWith.Register();
        Autostart.RefreshPath();
        ClipboardFiles.CleanUp();
        _tray = new TrayController(Settings.Load());
        _ = Task.Run(() => ListenAsync(_shutdown.Token));
        if (files.Length > 0)
            _tray.AddFiles(files);
        else if (!e.Args.Contains("--background"))
            _tray.ShowPopup(); // started from the Start menu or by double-click: show it's there
    }

    // ---- second launches ------------------------------------------------------

    /// <summary>Hands files (or just "show yourself") to the copy that's already running.</summary>
    private static void SendToRunningCopy(string[] files)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", InstanceName, PipeDirection.Out);
            pipe.Connect(5000); // the first copy may still be starting up
            using var writer = new StreamWriter(pipe);
            writer.Write(string.Join("\n", files));
        }
        catch (Exception ex)
        {
            LogError("Handing files to the running copy", ex);
        }
    }

    private async Task ListenAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(InstanceName, PipeDirection.In, 1,
                                                           PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await pipe.WaitForConnectionAsync(ct);
                using var reader = new StreamReader(pipe);
                var files = (await reader.ReadToEndAsync(ct)).Split('\n', StringSplitOptions.RemoveEmptyEntries);
                await Dispatcher.InvokeAsync(() =>
                {
                    if (files.Length > 0)
                        _tray?.AddFiles(files);
                    else
                        _tray?.ShowPopup();
                });
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { LogError("Listening for second launches", ex); }
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _shutdown.Cancel();
        _tray?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Keep the tray icon alive; note the problem in the log.
        LogError("Unhandled", e.Exception);
        e.Handled = true;
    }

    /// <summary>Notes a problem in error.log in the app's data folder.</summary>
    public static void LogError(string what, Exception e)
    {
        try
        {
            File.AppendAllText(Path.Combine(Settings.DataDir, "error.log"),
                               $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {what}: {e}\n\n");
        }
        catch (Exception) { }
    }
}
