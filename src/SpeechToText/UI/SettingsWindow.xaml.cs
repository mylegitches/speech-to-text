using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using SpeechToText.Audio;
using SpeechToText.Config;
using SpeechToText.Input;
using SpeechToText.Local;
using SpeechToText.Output;
using SpeechToText.Providers;

namespace SpeechToText.UI;

/// <summary>Every change saves itself (debounced) — there is no OK/Apply.</summary>
public partial class SettingsWindow : Window
{
    readonly App _app;
    readonly AppSettings _s;
    readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    readonly Recorder _testRecorder = new();
    readonly Hardware _hw = GpuDetect.Detect();
    CancellationTokenSource? _download;
    bool _loading = true;

    static readonly (string Label, int Seconds)[] Lengths = [("1 minute", 60), ("2 minutes", 120), ("5 minutes", 300), ("10 minutes", 600)];

    public SettingsWindow(App app)
    {
        InitializeComponent();
        _app = app;
        _s = app.Settings;

        foreach (var cb in new[] { DictateMode, NoteMode })
            cb.ItemsSource = new[] { "Smart (tap or hold)", "Hold to talk", "Press to toggle" };
        DictateOutput.ItemsSource = new[] { "Paste at cursor", "Notes", "Paste and notes" };
        Provider.ItemsSource = Transcriber.All;
        PolishProvider.ItemsSource = Polisher.All;
        // Order matches the LocalDevice enum: Auto, Cuda, Vulkan, Cpu. Unavailable ones are shown but disabled.
        LocalDeviceBox.ItemsSource = new[]
        {
            new ComboBoxItem { Content = $"Auto — {_hw.Describe(_hw.Best)}" },
            new ComboBoxItem { Content = _hw.Cuda ? $"NVIDIA GPU (CUDA {_hw.CudaMajor})" : "NVIDIA GPU (CUDA) — not found", IsEnabled = _hw.Cuda },
            new ComboBoxItem { Content = _hw.Vulkan ? "GPU (Vulkan)" : "GPU (Vulkan) — not found", IsEnabled = _hw.Vulkan },
            new ComboBoxItem { Content = "CPU — slow: several times your recording length" },
        };
        Mic.ItemsSource = new[] { "Windows default" }.Concat(Recorder.Devices()).ToList();
        try { OutputDevice.ItemsSource = new[] { new SystemAudio.OutputDevice("", "Windows default") }.Concat(SystemAudio.Devices()).ToList(); }
        catch { OutputDevice.ItemsSource = new List<SystemAudio.OutputDevice> { new("", "Windows default") }; }
        MaxSeconds.ItemsSource = Lengths.Select(l => l.Label).ToList();

        LoadFromSettings();
        _loading = false;

        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); Save(); };
        AddHandler(TextBox.TextChangedEvent, new RoutedEventHandler(Changed));
        AddHandler(ToggleButton.CheckedEvent, new RoutedEventHandler(Changed));
        AddHandler(ToggleButton.UncheckedEvent, new RoutedEventHandler(Changed));
        AddHandler(Selector.SelectionChangedEvent, new RoutedEventHandler(Changed));
        AddHandler(PasswordBox.PasswordChangedEvent, new RoutedEventHandler(Changed));
        Provider.SelectionChanged += (_, _) => { if (!_loading) LoadProvider(); };
        Model.SelectionChanged += (_, _) => { if (!_loading) Dispatcher.BeginInvoke(RefreshLocal); };
        LocalDeviceBox.SelectionChanged += (_, _) =>
        {
            if (_loading) return;
            _s.LocalDevice = (LocalDevice)LocalDeviceBox.SelectedIndex;
            RefreshLocal();
        };
        PolishProvider.SelectionChanged += (_, _) => { if (!_loading) LoadPolisher(); };
        StylePolished.Checked += (_, _) => PolishPanel.Visibility = Visibility.Visible;
        StylePolished.Unchecked += (_, _) => PolishPanel.Visibility = Visibility.Collapsed;

        foreach (var box in new[] { DictateHotkey, NoteHotkey }) WireHotkeyBox(box);

        _testRecorder.Level += l => Dispatcher.BeginInvoke(() => MicLevel.Value = l);
        Closed += (_, _) => { _download?.Cancel(); _testRecorder.Dispose(); if (_saveTimer.IsEnabled) Save(); };
    }

    void LoadFromSettings()
    {
        DictateHotkey.Text = _s.DictateHotkey;
        DictateMode.SelectedIndex = (int)_s.DictateMode;
        DictateOutput.SelectedIndex = (int)_s.DictateOutput;
        KeepInClipboard.IsChecked = _s.KeepInClipboard;
        NoteHotkey.Text = _s.NoteHotkey;
        NoteMode.SelectedIndex = (int)_s.NoteMode;
        StartWithWindows.IsChecked = Startup.IsEnabled;
        Sounds.IsChecked = _s.Sounds;

        Provider.SelectedItem = Transcriber.Get(_s.Provider);
        LoadProvider();
        LanguageBox.Text = _s.Language;
        StyleRaw.IsChecked = _s.TextStyle == TextStyle.Raw;
        StylePolished.IsChecked = _s.TextStyle == TextStyle.Polished;
        PolishProvider.SelectedItem = Polisher.Get(_s.PolishProvider);
        LoadPolisher();

        var n = _s.Notes;
        QnEnabled.IsChecked = n.QuickNotesEnabled; QnFile.Text = n.QuickNotesFile;
        MdEnabled.IsChecked = n.MarkdownEnabled; MdPath.Text = n.MarkdownPath; MdLine.Text = n.MarkdownLine;
        NotionEnabled.IsChecked = n.NotionEnabled; NotionPage.Text = n.NotionPageId; NotionToken.Password = SecretStore.Get("notion") ?? "";
        JoplinEnabled.IsChecked = n.JoplinEnabled; JoplinFolder.Text = n.JoplinFolderId; JoplinToken.Password = SecretStore.Get("joplin") ?? "";
        WebhookEnabled.IsChecked = n.WebhookEnabled; WebhookUrl.Text = n.WebhookUrl;

        Mic.SelectedIndex = Math.Clamp(_s.MicDevice + 1, 0, Mic.Items.Count - 1);
        CaptureSystemAudio.IsChecked = _s.CaptureSystemAudio;
        var outputs = OutputDevice.ItemsSource as List<SystemAudio.OutputDevice> ?? [];
        OutputDevice.SelectedItem = outputs.FirstOrDefault(d => d.Id == _s.OutputDevice) ?? outputs.FirstOrDefault();
        var li = Array.FindIndex(Lengths, l => l.Seconds == _s.MaxSeconds);
        MaxSeconds.SelectedIndex = li < 0 ? 2 : li;
    }

    void LoadProvider()
    {
        var was = _loading;
        _loading = true;
        var info = (ProviderInfo)Provider.SelectedItem;
        var ps = _s.For(info.Id);
        Model.ItemsSource = info.Models;
        Model.Text = info.Entry(ps.Model.Length > 0 ? ProviderInfo.ModelId(ps.Model) : info.DefaultModel);
        BaseUrl.Text = ps.BaseUrl.Length > 0 ? ps.BaseUrl : info.BaseUrl;
        BaseUrl.Visibility = BaseUrlLabel.Visibility = info.CustomUrl ? Visibility.Visible : Visibility.Collapsed;
        ApiKey.Password = SecretStore.Get(info.Id) ?? "";
        TestResult.Text = "";

        bool local = Transcriber.IsLocal(info.Id);
        ApiKeyLabel.Visibility = ApiKeyRow.Visibility = local ? Visibility.Collapsed : Visibility.Visible;
        LocalDeviceLabel.Visibility = LocalDeviceBox.Visibility = LocalFiles.Visibility = local ? Visibility.Visible : Visibility.Collapsed;
        LocalDeviceBox.SelectedIndex = (int)_s.LocalDevice;
        _loading = was;
        if (local) RefreshLocal();
    }

    // ---- On this PC (offline) ----

    LocalModel SelectedLocalModel => LocalWhisper.Model(ProviderInfo.ModelId(Model.Text));

    static string RuntimeSize(LocalDevice d, Hardware hw) => d switch
    {
        LocalDevice.Cuda => hw.CudaMajor == 12 ? "540 MB" : "150 MB",
        LocalDevice.Vulkan => "57 MB",
        _ => "2 MB",
    };

    void RefreshLocal()
    {
        if (!Transcriber.IsLocal(_s.Provider) && Provider.SelectedItem is ProviderInfo p && !Transcriber.IsLocal(p.Id)) return;
        if (_download != null) return; // progress owns the status while downloading
        var model = SelectedLocalModel;
        var device = _hw.Resolve((LocalDevice)Math.Max(0, LocalDeviceBox.SelectedIndex));
        bool ready = LocalWhisper.IsDownloaded(model) && LocalWhisper.RuntimeReady(device) && LocalWhisper.RuntimeReady(LocalDevice.Cpu);

        if (ready)
        {
            LocalStatus.Text = $"✓ Ready · {model.Size} model on this PC";
            LocalDownload.Content = "Delete model";
        }
        else
        {
            var needs = new List<string>();
            if (!LocalWhisper.IsDownloaded(model)) needs.Add($"{model.Size} model");
            if (!LocalWhisper.RuntimeReady(device)) needs.Add($"{RuntimeSize(device, _hw)} {_hw.Describe(device).Split(" (").Last().TrimEnd(')')} runtime");
            LocalStatus.Text = "One-time download: " + string.Join(" + ", needs);
            LocalDownload.Content = "Download";
        }
        LocalTest.IsEnabled = ready;
        if (LocalWhisper.NeedsRestartFor(device))
            LocalStatus.Text += $"\nRestart SpeechToText to switch from {LocalWhisper.LoadedRuntime} to {device}.";
    }

    async void LocalDownload_Click(object sender, RoutedEventArgs e)
    {
        if (_download != null) { _download.Cancel(); return; } // button reads "Cancel" while downloading

        var model = SelectedLocalModel;
        var device = _hw.Resolve((LocalDevice)LocalDeviceBox.SelectedIndex);
        bool ready = LocalWhisper.IsDownloaded(model) && LocalWhisper.RuntimeReady(device);
        if (ready)
        {
            var answer = MessageBox.Show(this, $"Delete the {model.Id} model ({model.Size})? You can download it again later.",
                "Delete model · SpeechToText", MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (answer == MessageBoxResult.OK) LocalWhisper.DeleteModel(model);
            RefreshLocal();
            return;
        }

        _download = new CancellationTokenSource();
        LocalDownload.Content = "Cancel";
        LocalTest.IsEnabled = false;
        LocalProgress.Visibility = Visibility.Visible;
        var progress = new Progress<(string What, double Done)>(p =>
        {
            LocalProgress.Value = p.Done;
            LocalStatus.Text = $"Downloading {p.What}… {p.Done:P0}";
        });
        try
        {
            await LocalWhisper.EnsureRuntimeAsync(device, progress, _download.Token);
            if (!LocalWhisper.IsDownloaded(model)) await LocalWhisper.DownloadModelAsync(model, progress, _download.Token);
            TestResult.Text = "✓ Downloaded and verified. Press Test to try it.";
        }
        catch (OperationCanceledException) { TestResult.Text = "Download cancelled."; }
        catch (Exception ex) { TestResult.Text = "✗ " + ex.Message + " Check your connection and try again."; }
        finally
        {
            _download.Dispose();
            _download = null;
            LocalProgress.Visibility = Visibility.Collapsed;
            RefreshLocal();
        }
    }

    async void LocalTest_Click(object sender, RoutedEventArgs e)
    {
        Save();
        LocalTest.IsEnabled = false;
        TestResult.Text = "Loading the model and transcribing 1 s of silence… the first run takes longest.";
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await Task.Run(() => Transcriber.TranscribeAsync(_s, Recorder.ToWav(new byte[16000 * 2])));
            TestResult.Text = $"✓ Works on {LocalWhisper.LoadedRuntime} · {sw.Elapsed.TotalSeconds:0.0} s including loading the model. " +
                              "The model stays loaded for 5 minutes after each use, so later dictations are faster.";
        }
        catch (Exception ex) { TestResult.Text = "✗ " + ex.Message; }
        finally { LocalTest.IsEnabled = true; }
    }

    void LoadPolisher()
    {
        var was = _loading;
        _loading = true;
        var info = (ProviderInfo)PolishProvider.SelectedItem;
        var ps = _s.PolishFor(info.Id);
        PolishModel.ItemsSource = info.Models;
        PolishModel.Text = ps.Model.Length > 0 ? ps.Model : info.DefaultModel;
        PolishUrl.Text = ps.BaseUrl.Length > 0 ? ps.BaseUrl : info.BaseUrl;
        PolishUrl.Visibility = PolishUrlLabel.Visibility = info.CustomUrl ? Visibility.Visible : Visibility.Collapsed;
        PolishKey.Password = SecretStore.Get(Polisher.SecretName(info.Id)) ?? "";
        PolishKey.IsEnabled = info.Id != "ollama-local"; // local Ollama needs no key
        PolishResult.Text = "";
        PolishPanel.Visibility = StylePolished.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        _loading = was;
    }

    void Changed(object sender, RoutedEventArgs e)
    {
        if (_loading || e.OriginalSource == Nav) return;
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    void Save()
    {
        _s.DictateMode = (HotkeyMode)DictateMode.SelectedIndex;
        _s.DictateOutput = (DictateOutput)DictateOutput.SelectedIndex;
        _s.KeepInClipboard = KeepInClipboard.IsChecked == true;
        _s.NoteMode = (HotkeyMode)NoteMode.SelectedIndex;
        _s.Sounds = Sounds.IsChecked == true;
        Startup.Set(StartWithWindows.IsChecked == true);

        var info = (ProviderInfo)Provider.SelectedItem;
        _s.Provider = info.Id;
        var ps = _s.For(info.Id);
        ps.Model = ProviderInfo.ModelId(Model.Text);
        if (info.CustomUrl) ps.BaseUrl = BaseUrl.Text.Trim();
        SecretStore.Set(info.Id, ApiKey.Password.Trim());
        _s.Language = LanguageBox.Text.Trim().ToLowerInvariant();
        if (LocalDeviceBox.SelectedIndex >= 0) _s.LocalDevice = (LocalDevice)LocalDeviceBox.SelectedIndex;

        _s.TextStyle = StylePolished.IsChecked == true ? TextStyle.Polished : TextStyle.Raw;
        var pinfo = (ProviderInfo)PolishProvider.SelectedItem;
        _s.PolishProvider = pinfo.Id;
        var pps = _s.PolishFor(pinfo.Id);
        pps.Model = PolishModel.Text.Trim();
        if (pinfo.CustomUrl) pps.BaseUrl = PolishUrl.Text.Trim();
        if (pinfo.Id != "ollama-local") SecretStore.Set(Polisher.SecretName(pinfo.Id), PolishKey.Password.Trim());

        var n = _s.Notes;
        n.QuickNotesEnabled = QnEnabled.IsChecked == true; n.QuickNotesFile = QnFile.Text.Trim();
        n.MarkdownEnabled = MdEnabled.IsChecked == true; n.MarkdownPath = MdPath.Text.Trim(); n.MarkdownLine = MdLine.Text;
        n.NotionEnabled = NotionEnabled.IsChecked == true; n.NotionPageId = NotionPage.Text.Trim();
        SecretStore.Set("notion", NotionToken.Password.Trim());
        n.JoplinEnabled = JoplinEnabled.IsChecked == true; n.JoplinFolderId = JoplinFolder.Text.Trim();
        SecretStore.Set("joplin", JoplinToken.Password.Trim());
        n.WebhookEnabled = WebhookEnabled.IsChecked == true; n.WebhookUrl = WebhookUrl.Text.Trim();

        _s.MicDevice = Mic.SelectedIndex - 1;
        _s.CaptureSystemAudio = CaptureSystemAudio.IsChecked == true;
        _s.OutputDevice = (OutputDevice.SelectedItem as SystemAudio.OutputDevice)?.Id ?? "";
        _s.MaxSeconds = Lengths[Math.Max(0, MaxSeconds.SelectedIndex)].Seconds;

        _s.Save();
        _app.ApplySettings();
    }

    // ---- hotkey capture ----

    void WireHotkeyBox(TextBox box)
    {
        string before = "";
        box.GotKeyboardFocus += (_, _) => { before = box.Text; box.Text = "Press a key combination…"; _app.Hook.Suspended = true; };
        box.LostKeyboardFocus += (_, _) =>
        {
            _app.Hook.Suspended = false;
            if (!Hotkey.TryParse(box.Text, out _)) box.Text = before;
        };
        box.PreviewKeyDown += (_, e) =>
        {
            e.Handled = true;
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key == Key.Escape) { box.Text = before; Keyboard.ClearFocus(); Nav.Focus(); return; }
            if (Hotkey.IsModifier(key)) return;
            var hk = new Hotkey(Keyboard.Modifiers, key);
            var other = box == DictateHotkey ? _s.NoteHotkey : _s.DictateHotkey;
            if (hk.ToString() == other) { box.Text = "Already used by the other hotkey"; return; }
            box.Text = hk.ToString();
            if (box == DictateHotkey) _s.DictateHotkey = box.Text; else _s.NoteHotkey = box.Text;
            Nav.Focus(); // commit: leaves the box, re-enables hotkeys
            Changed(box, e);
        };
    }

    // ---- buttons ----

    void Nav_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (General == null || Nav.SelectedItem is not ListBoxItem { Tag: string tag }) return; // fires during InitializeComponent
        foreach (var p in new FrameworkElement[] { General, Transcription, Notes, Audio })
            p.Visibility = p.Name == tag ? Visibility.Visible : Visibility.Collapsed;
        if (tag != "Audio") MicTest.IsChecked = false;
    }

    async void TestKey_Click(object sender, RoutedEventArgs e)
    {
        Save();
        TestKey.IsEnabled = false;
        TestResult.Text = "Testing…";
        try
        {
            await Transcriber.TranscribeAsync(_s, Recorder.ToWav(new byte[16000 * 2])); // 1 s of silence
            TestResult.Text = "✓ Connected";
        }
        catch (Exception ex) { TestResult.Text = "✗ " + ex.Message; }
        finally { TestKey.IsEnabled = true; }
    }

    async void TestPolish_Click(object sender, RoutedEventArgs e)
    {
        Save();
        TestPolish.IsEnabled = false;
        PolishResult.Text = "Polishing a sample sentence…";
        try
        {
            var (text, ms) = await Polisher.TestAsync(_s);
            PolishResult.Text = $"✓ {ms / 1000.0:0.0} s → {text}";
        }
        catch (Exception ex) { PolishResult.Text = "✗ " + ex.Message; }
        finally { TestPolish.IsEnabled = true; }
    }

    async void TestNote_Click(object sender, RoutedEventArgs e)
    {
        Save();
        NoteTestResult.Visibility = Visibility.Visible;
        if (!NoteTargets.AnyEnabled(_s.Notes)) { NoteTestResult.Text = "Turn on at least one target below."; return; }
        NoteTestResult.Text = "Sending…";
        var failed = await NoteTargets.SendAsync(_s.Notes, new Note(DateTime.Now, "Test note from Speech to Text", NoteKind.Text));
        NoteTestResult.Text = failed.Count == 0 ? "✓ Sent to every enabled target" : "✗ " + string.Join("   ", failed);
    }

    void BrowseQn_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "Text files|*.txt|All files|*.*", CheckFileExists = false, FileName = QnFile.Text };
        if (dlg.ShowDialog(this) == true) QnFile.Text = dlg.FileName;
    }

    void MicTest_Changed(object sender, RoutedEventArgs e)
    {
        if (MicTest.IsChecked == true && !_app.IsBusy)
        {
            try { _testRecorder.Start(Mic.SelectedIndex - 1); }
            catch (Exception ex) { MicTest.IsChecked = false; MessageBox.Show(this, ex.Message, "Microphone"); }
        }
        else if (_testRecorder.IsRecording) { _ = _testRecorder.StopAsync(); MicLevel.Value = 0; }
    }
}
