using System.IO;
using System.Media;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using H.NotifyIcon;
using H.NotifyIcon.Core;
using SpeechToText.Audio;
using SpeechToText.Config;
using SpeechToText.Input;
using SpeechToText.Output;
using SpeechToText.Providers;
using SpeechToText.UI;

namespace SpeechToText;

public partial class App : Application
{
    enum Session { None, Dictate, Note }

    public AppSettings Settings { get; private set; } = null!;
    public KeyboardHook Hook { get; private set; } = null!;
    public bool IsBusy => _session != Session.None;

    Mutex? _single;
    TaskbarIcon _tray = null!;
    RecordingPill _pill = null!;
    readonly Recorder _recorder = new();
    readonly DispatcherTimer _maxTimer = new();
    readonly List<IDisposable> _hotkeys = new();
    TriggerLogic _dictate = null!, _note = null!;
    Session _session;
    string _lastTranscript = "";
    SettingsWindow? _settingsWindow;

    protected override void OnStartup(StartupEventArgs e)
    {
        _single = new Mutex(true, @"Local\SpeechToText.Tray", out bool first);
        if (!first) { Shutdown(); return; }

        // A tray app should never vanish silently: log, tell the user, keep running.
        DispatcherUnhandledException += (_, ex) =>
        {
            ex.Handled = true;
            Log(ex.Exception);
            if (_tray != null) Notify("Something went wrong", ex.Exception.Message + "\nDetails are in error.log in %APPDATA%\\SpeechToText.");
        };

        Settings = AppSettings.Load();
        Hook = new KeyboardHook();
        _pill = new RecordingPill();
        _recorder.Level += l => _pill.PushLevel(l);
        _maxTimer.Tick += (_, _) => StopSession();

        _dictate = new TriggerLogic(() => Settings.DictateMode);
        _dictate.Start += () => StartSession(Session.Dictate, _dictate);
        _dictate.Stop += StopSession;
        _dictate.Cancel += CancelSession;

        _note = new TriggerLogic(() => Settings.NoteMode);
        _note.Start += () => StartSession(Session.Note, _note);
        _note.Stop += StopSession;
        // A quick tap on the note hotkey (in Hold mode) opens the typed-note box, like Quick Notes did.
        _note.Cancel += () => { CancelSession(); ShowTextNote(); };

        CreateTray();
        ApplySettings();

        if (!File.Exists(Path.Combine(AppSettings.Dir, "settings.json")))
        {
            Settings.Save();
            OpenSettings(); // first run
        }
    }

    public void ApplySettings()
    {
        foreach (var h in _hotkeys) h.Dispose();
        _hotkeys.Clear();
        Bind(Settings.DictateHotkey, _dictate);
        Bind(Settings.NoteHotkey, _note);
        _maxTimer.Interval = TimeSpan.FromSeconds(Math.Max(10, Settings.MaxSeconds));
        _tray.ToolTipText = $"SpeechToText · {Settings.DictateHotkey} dictate · {Settings.NoteHotkey} note";
        RebuildProviderMenu();

        void Bind(string text, TriggerLogic t)
        {
            if (Hotkey.TryParse(text, out var hk)) _hotkeys.Add(Hook.Register(hk, t.Pressed, t.Released));
            else Notify("Hotkey", $"\"{text}\" isn't a valid hotkey.");
        }
    }

    // ---- recording session ----

    void StartSession(Session kind, TriggerLogic source)
    {
        if (_session != Session.None) { source.Reset(); return; }
        try { _recorder.Start(Settings.MicDevice); }
        catch (Exception ex)
        {
            source.Reset();
            Log(ex);
            Fail("Couldn't open the microphone", "Pick another one in Settings → Audio, or check that it is plugged in.");
            return;
        }

        _session = kind;
        Hook.EscapeHandler = CancelSession;
        _maxTimer.Start();
        Play("Speech On");
        _pill.ShowListening(kind == Session.Note);
        _tray.Icon = TrayIcons.Recording;
    }

    async void StopSession()
    {
        if (_session == Session.None || !_recorder.IsRecording) return;
        var kind = _session;
        EndRecordingUi();
        Play("Speech Off");

        var wav = await _recorder.StopAsync();
        if (wav == null) { Finish(); _pill.Hide(); return; }

        _pill.ShowBusy();
        _tray.Icon = TrayIcons.Busy;
        try
        {
            var text = await Transcriber.TranscribeAsync(Settings, wav);
            if (text.Length == 0) { _pill.Flash("Didn't catch that", ok: false); return; }

            if (Settings.TextStyle == TextStyle.Polished)
            {
                _pill.ShowBusy("Polishing…");
                try { text = await Polisher.PolishAsync(Settings, text); }
                catch (Exception ex)
                {
                    // Never lose the dictation: fall back to the raw text and say so.
                    Log(ex);
                    Notify("Used the raw text", ex.Message + "\nYour words were kept as spoken. Check Settings → Transcription → Text style.");
                }
            }
            _lastTranscript = text;

            bool toNotes = kind == Session.Note || Settings.DictateOutput != DictateOutput.Paste;
            bool paste = kind == Session.Dictate && Settings.DictateOutput != DictateOutput.Notes;

            if (paste) { _pill.Hide(); await Paster.PasteAsync(text, Settings.KeepInClipboard); }
            else if (Settings.KeepInClipboard) await Paster.CopyAsync(text);
            if (toNotes) await SaveNote(text, NoteKind.Voice, flash: !paste);
            else _pill.Hide();
        }
        catch (Exception ex)
        {
            _pill.Hide();
            Log(ex);
            Fail("Couldn't transcribe that", ex.Message + "\nCheck your connection, or your key in Settings → Transcription.");
        }
        finally { Finish(); }
    }

    async void CancelSession()
    {
        if (_session == Session.None) return;
        EndRecordingUi();
        await _recorder.StopAsync();
        Finish();
        _pill.Hide();
    }

    void EndRecordingUi()
    {
        _maxTimer.Stop();
        Hook.EscapeHandler = null;
        _dictate.Reset();
        _note.Reset();
    }

    void Finish()
    {
        _session = Session.None;
        _tray.Icon = TrayIcons.Idle;
    }

    async Task SaveNote(string text, NoteKind kind, bool flash)
    {
        if (!NoteTargets.AnyEnabled(Settings.Notes)) { Fail("Note not saved", "No note app is turned on. Turn one on in Settings → Notes."); return; }
        var failed = await NoteTargets.SendAsync(Settings.Notes, new Note(DateTime.Now, text, kind));
        if (failed.Count > 0) Fail("Note not saved everywhere", string.Join("\n", failed) + "\nCheck those apps in Settings → Notes.");
        else if (flash) _pill.Flash("Saved to notes");
    }

    void ShowTextNote() => new TextNoteWindow(t => SaveNote(t, NoteKind.Text, flash: true)).Show();

    // ---- tray ----

    MenuItem _providerMenu = null!;
    MenuItem _pauseItem = null!;

    void CreateTray()
    {
        _providerMenu = new MenuItem { Header = "Provider" };
        _pauseItem = new MenuItem { Header = "Pause hotkeys", IsCheckable = true };
        _pauseItem.Click += (_, _) => Hook.Suspended = _pauseItem.IsChecked;

        var menu = new ContextMenu();
        menu.Items.Add(Item("Settings…", OpenSettings));
        menu.Items.Add(Item("New text note…", ShowTextNote));
        menu.Items.Add(Item("Copy last transcript", () => { if (_lastTranscript.Length > 0) Clipboard.SetText(_lastTranscript); }));
        menu.Items.Add(new Separator());
        menu.Items.Add(_providerMenu);
        menu.Items.Add(_pauseItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Quit", () => Shutdown()));

        _tray = new TaskbarIcon
        {
            Icon = TrayIcons.Idle,
            ContextMenu = menu,
            NoLeftClickDelay = true,
        };
        _tray.TrayLeftMouseUp += (_, _) => OpenSettings();
        _tray.ForceCreate(enablesEfficiencyMode: false);
    }

    void RebuildProviderMenu()
    {
        _providerMenu.Items.Clear();
        foreach (var p in Transcriber.All)
        {
            var item = new MenuItem { Header = p.Name, IsCheckable = true, IsChecked = p.Id == Settings.Provider };
            item.Click += (_, _) => { Settings.Provider = p.Id; Settings.Save(); RebuildProviderMenu(); };
            _providerMenu.Items.Add(item);
        }
    }

    static MenuItem Item(string header, Action a)
    {
        var m = new MenuItem { Header = header };
        m.Click += (_, _) => a();
        return m;
    }

    void OpenSettings()
    {
        if (_settingsWindow != null) { _settingsWindow.Activate(); return; }
        _settingsWindow = new SettingsWindow(this);
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    // ---- feedback ----

    void Fail(string title, string message)
    {
        Play("Speech Misrecognition");
        Notify(title, message);
    }

    static void Log(Exception ex)
    {
        try
        {
            Directory.CreateDirectory(AppSettings.Dir);
            File.AppendAllText(Path.Combine(AppSettings.Dir, "error.log"), $"{DateTime.Now:o}\n{ex}\n\n");
        }
        catch { /* logging must never throw */ }
    }

    void Notify(string title, string message) =>
        _tray.ShowNotification(title, message, NotificationIcon.Warning);

    void Play(string name)
    {
        if (!Settings.Sounds) return;
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Media", name + ".wav");
        if (File.Exists(path)) try { new SoundPlayer(path).Play(); } catch { /* sound is optional */ }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Hook?.Dispose();
        _recorder.Dispose();
        _tray?.Dispose();
        _single?.Dispose();
        base.OnExit(e);
    }
}
