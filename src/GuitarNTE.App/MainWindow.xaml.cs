using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Data;
using System.Windows.Media;
using GuitarNTE.Core;
using Microsoft.Win32;

namespace GuitarNTE.App;
public partial class MainWindow : Window
{
    readonly LibraryStore store = new();
    readonly WindowsKeyboardOutput output = new();
    readonly PlaybackEngine engine;
    readonly ObservableCollection<SongProject> songs;
    readonly ICollectionView songView;
    readonly Dictionary<int, TextBox> keyBoxes = [];
    readonly Dictionary<int, Border> keyTiles = [];
    AppSettings settings;
    SongProject? current;
    Hotkeys? hotkeys;
    string[] searchTerms = [];
    bool loading = true, dirty, shuttingDown, importing;
    bool IsBusy => engine.Status.State is PlaybackState.Playing or PlaybackState.Countdown or PlaybackState.Paused;
    public MainWindow() : this(true) { }
    internal MainWindow(bool enableGlobalHotkeys)
    {
        settings = store.LoadSettings();
        UiLanguage.Apply(settings.Language);
        InitializeComponent();
        LanguageBox.SelectedIndex = UiText.Language == "zh-CN" ? 0 : 1;
        var loadedSongs = store.LoadSongs();
        int duplicatesRemoved = store.CleanDuplicateSongs(loadedSongs, App.StartupSongId);
        songs = new(loadedSongs);
        if (songs.Count == 0)
        {
            songs.Add(new() { Title = UiText.T("两只老虎 · 简谱入门", "Frere Jacques · numbered notation") });
            songs.Add(new() { Title = UiText.T("和弦与休止 · 音名示例", "Chords and rests · note names"), Mode = Notation.NoteNames, Text = "// 和弦同时触发，R 表示休止\n[E4 G4 B4]:2 R:0.5 E4:0.5 F#4:1 G4:1\n[A3 C4 E4]:2 R:1 E4:1" });
            foreach (var song in songs) store.SaveSong(song);
        }
        engine = new(output);
        engine.Changed += s => { if (!shuttingDown) Dispatcher.BeginInvoke(() => ShowStatus(s)); };
        InstrumentBox.SelectedIndex = (int)settings.Instrument;
        BuildKeyboard();
        CountdownBox.Text = F(settings.Countdown); PressBox.Text = settings.PressMilliseconds.ToString(); LoopsBox.Text = settings.Loops.ToString();
        SustainBox.IsChecked = settings.SustainNotes;
        PressBox.IsEnabled = !settings.SustainNotes;
        ToggleKeyBox.Text = settings.ToggleKey; StopKeyBox.Text = settings.StopKey;
        RefreshWindows();
        var selectedSong = songs.FirstOrDefault(s => s.Id == App.StartupSongId) ?? songs[0];
        songView = CollectionViewSource.GetDefaultView(songs);
        songView.Filter = song => SearchMatches(((SongProject)song).Title);
        SongList.ItemsSource = songView;
        SongList.SelectedItem = selectedSong; LoadSong(selectedSong); RefreshSongs();
        loading = false;
        if (duplicatesRemoved > 0)
        {
            StatusText.Text = UiText.T($"已自动清理 {duplicatesRemoved} 首重复曲谱", $"Archived {duplicatesRemoved} duplicate scores");
            ValidationText.Text = UiText.T("保留最新保存的版本；重复副本已移至曲库 DuplicateBackups 目录，可随时恢复。", "The most recently saved versions were retained. Duplicate copies are recoverable from DuplicateBackups.");
        }
        SourceInitialized += (_, _) =>
        {
            if (enableGlobalHotkeys)
            {
                hotkeys = new(new System.Windows.Interop.WindowInteropHelper(this).Handle);
                hotkeys.Pressed += stop => Guard(() => { if (stop) engine.Stop(); else Toggle(); });
                Guard(() => hotkeys.Register(settings.ToggleKey, settings.StopKey));
            }
            if (store.Warnings.Count > 0) MessageBox.Show(this, string.Join("\n", store.Warnings), UiText.T("读取提示", "Library warnings"), MessageBoxButton.OK, MessageBoxImage.Warning);
        };
        Closing += OnClosing;
    }
    static string F(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
    void Language_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (loading || LanguageBox.SelectedIndex < 0) return;
        string language = LanguageBox.SelectedIndex == 0 ? "zh-CN" : "en";
        if (language == UiText.Language) return;
        Guard(() =>
        {
            try
            {
                RequireStopped();
                // Switch even when a numeric field or score draft is invalid.
                var candidate = LibraryStore.Copy(settings);
                candidate.Language = language;
                var pendingKeys = keyBoxes.ToDictionary(pair => pair.Key, pair => pair.Value.Text);
                store.SaveSettings(candidate);
                settings = candidate;
                UiLanguage.Apply(language);
                BuildKeyboard();
                foreach (var pair in pendingKeys) keyBoxes[pair.Key].Text = pair.Value;
                if (current?.MidiBase64 != null)
                {
                    var tracks = MidiImporter.Inspect(Convert.FromBase64String(current.MidiBase64));
                    foreach (var check in TrackPanel.Children.OfType<CheckBox>())
                    {
                        var track = tracks.Single(track => track.Index == (int)check.Tag);
                        check.Content = UiText.T($"{track.Index + 1}. {track.Name} · {track.NoteCount} 音", $"{track.Index + 1}. {track.Name} · {track.NoteCount} notes");
                    }
                }
                RefreshSongs(); UpdateMode(); RefreshAccess();
                PlayButton.Content = UiText.T("开始演奏", "Play");
                StatusText.Text = UiText.T("已切换中文界面", "Switched to English");
                ProgressText.Text = UiText.T("先校验乐谱，再切回游戏开始演奏", "Validate, then switch to the game to start playing");
                ValidationText.Text = UiText.T("语言偏好已保存；当前编辑内容和键位草稿保持不变。", "Language saved; score edits and pending key bindings were preserved.");
            }
            finally
            {
                bool previous = loading; loading = true;
                LanguageBox.SelectedIndex = UiText.Language == "zh-CN" ? 0 : 1;
                loading = previous;
            }
        });
    }
    bool SearchMatches(string title) => searchTerms.All(term => title.Contains(term, StringComparison.OrdinalIgnoreCase));
    void RefreshSongs()
    {
        bool wasLoading = loading; loading = true;
        try
        {
            songView.Refresh();
            SongList.SelectedItem = current != null && SongList.Items.Contains(current) ? current : null;
            SongCountText.Text = string.IsNullOrWhiteSpace(SongSearchBox.Text)
                ? UiText.T($"{songs.Count} 首曲谱", $"{songs.Count} scores") : UiText.T($"找到 {SongList.Items.Count} / {songs.Count} 首", $"{SongList.Items.Count} / {songs.Count} scores");
            EmptyLibraryHint.Visibility = SongList.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            ClearSearchButton.Visibility = SongSearchBox.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        }
        finally { loading = wasLoading; }
    }
    void SongSearch_Changed(object sender, TextChangedEventArgs e)
    {
        searchTerms = SongSearchBox.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (songs == null || SongList.ItemsSource == null) return;
        RefreshSongs();
    }
    void ClearSearch_Click(object sender, RoutedEventArgs e) { SongSearchBox.Clear(); SongSearchBox.Focus(); }
    void SongSearch_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { SongSearchBox.Clear(); e.Handled = true; }
    }
    void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control && SongSearchBox.IsEnabled)
        { SongSearchBox.Focus(); SongSearchBox.SelectAll(); e.Handled = true; }
    }
    void Guard(Action action)
    {
        try { action(); }
        catch (Exception e)
        {
            StatusText.Text = UiText.T("需要检查", "Please check"); ValidationText.Text = e.Message;
            if (e is ScoreFormatException scoreError)
            {
                ScoreEditor.Focus();
                int offset = ScoreEditor.GetCharacterIndexFromLineIndex(scoreError.Line - 1);
                if (offset >= 0) { ScoreEditor.Select(Math.Min(ScoreEditor.Text.Length, offset + scoreError.Column - 1), 0); ScoreEditor.ScrollToLine(scoreError.Line - 1); }
            }
            MessageBox.Show(this, e.Message, UiText.Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
    void RequireStopped()
    {
        if (importing) throw new InvalidOperationException(UiText.T("正在批量导入 MIDI，请等待完成。", "MIDI import is in progress. Please wait."));
        if (IsBusy) throw new InvalidOperationException(UiText.T("请先急停当前演奏，再修改曲谱、目标窗口或键位。", "Stop playback before editing the score, target window or key bindings."));
    }
    void BuildKeyboard()
    {
        KeyboardPanel.Children.Clear(); keyBoxes.Clear(); keyTiles.Clear();
        bool piano = settings.Instrument == InstrumentMode.Piano;
        InstrumentHint.Text = piano ? UiText.T("中音 1 = C4 · Shift 升半音 / Ctrl 降半音", "Middle 1 = C4 · Shift raises / Ctrl lowers a semitone") : UiText.T("E3—D♯6 · 三排独立按键", "E3–D#6 · three rows of separate keys");
        KeyboardHint.Text = piano ? UiText.T("C3—B5 · 高 / 中 / 低三排；半音可填写 Shift+Q、Ctrl+E 等。共用字母的音重触发时会截短前音。", "C3–B5 · high / middle / low rows. Use Shift+Q or Ctrl+E for semitones. Shared base keys retrigger and shorten the previous note.") : UiText.T("E3—D♯6 · 可直接编辑按键名称，应用后生效", "E3–D#6 · edit key names, then apply changes");
        int min = KeyboardLayout.MinimumPitch(settings.Instrument);
        string[] degrees = ["1", "#1", "2", "♭3", "3", "4", "#4", "5", "#5", "6", "♭7", "7"];
        foreach (int first in new[] { min + 24, min + 12, min })
        {
            KeyboardPanel.Children.Add(new TextBlock { Text = first == min + 24 ? UiText.T("高音", "High") : first == min + 12 ? UiText.T("中音", "Middle") : UiText.T("低音", "Low"), Foreground = Brushes.LightSteelBlue, Margin = new(0, 3, 0, 5) });
            var row = new System.Windows.Controls.Primitives.UniformGrid { Columns = 12, Margin = new(0, 0, 0, 6) };
            for (int p = first; p < first + 12; p++)
            {
                var box = new TextBox { Text = settings.ActiveKeyMap[p], Padding = new(0, 5, 0, 5), HorizontalContentAlignment = HorizontalAlignment.Center, FontSize = piano ? 10 : 12, ToolTip = UiText.T("编辑按键后点击「应用热键与键位」", "Edit a key, then click Apply keys") };
                var panel = new StackPanel();
                panel.Children.Add(new TextBlock { Text = piano ? $"{degrees[p - first]} · {Pitches.Name(p)}" : Pitches.Name(p), HorizontalAlignment = HorizontalAlignment.Center, Foreground = Brushes.White, FontSize = piano ? 11 : 13, Margin = new(0, 4, 0, 7), FontWeight = FontWeights.SemiBold });
                panel.Children.Add(box);
                var tile = new Border { Background = new SolidColorBrush(Color.FromRgb(34, 49, 70)), CornerRadius = new(6), Margin = new(0, 0, 4, 0), Padding = new(3), Child = panel };
                keyBoxes[p] = box; keyTiles[p] = tile; row.Children.Add(tile);
            }
            KeyboardPanel.Children.Add(row);
        }
    }
    void RefreshWindows()
    {
        var old = WindowBox.SelectedItem as WindowChoice;
        var windows = Native.Windows(); WindowBox.ItemsSource = windows;
        WindowBox.SelectedItem = windows.FirstOrDefault(w => w.Handle == old?.Handle && w.ProcessId == old.ProcessId) ?? Native.SelectGameWindow(windows);
        RefreshAccess();
    }
    void Window_Changed(object sender, SelectionChangedEventArgs e) => RefreshAccess();
    void RefreshAccess()
    {
        if (AccessText == null) return;
        if (WindowBox.SelectedItem is not WindowChoice target)
        {
            AccessText.Text = UiText.T("请选择游戏窗口以检查输入权限。", "Select a game window to check input permissions."); ElevateButton.Visibility = Visibility.Collapsed; return;
        }
        var access = InputAccess.Check(target.ProcessId);
        AccessText.Text = access.Message;
        AccessBanner.Background = new SolidColorBrush(access.Blocked ? Color.FromRgb(91, 51, 52) : Color.FromRgb(32, 50, 71));
        ElevateButton.Visibility = access.CanRestartAsAdmin ? Visibility.Visible : Visibility.Collapsed;
    }
    void Elevate_Click(object sender, RoutedEventArgs e) => Guard(() =>
    {
        engine.Stop(); SaveCurrent();
        var candidate = CaptureSettings(); store.SaveSettings(candidate); settings = candidate;
        if (ElevatedRestart.Start(store.Root, current?.Id)) Close();
        else { StatusText.Text = UiText.T("管理员授权已取消，当前窗口和曲谱已保留", "Elevation cancelled; the current window and score were retained"); RefreshAccess(); }
    });
    void CaptureSong()
    {
        if (current == null) return;
        string title = TitleBox.Text.Trim();
        if (title.Length is < 1 or > 100) throw new FormatException(UiText.T("曲谱名称需要 1–100 个字符。", "Score titles must contain 1–100 characters."));
        double bpm = Number(BpmBox.Text, "BPM", 20, 400), speed = Number(SpeedBox.Text, UiText.T("速度倍率", "Speed"), 0.25, 4);
        int transpose = Integer(TransposeBox.Text, UiText.T("移调", "Transpose"), -48, 48);
        if (ModeBox.SelectedIndex < 0) throw new FormatException(UiText.T("请选择乐谱格式。", "Select a score format."));
        var mode = (Notation)ModeBox.SelectedIndex;
        if (mode == Notation.Midi && current.MidiBase64 == null) throw new FormatException(UiText.T("请使用「导入 MIDI」创建 MIDI 曲谱。", "Use Import MIDI to create a MIDI score."));
        string tonic = TonicBox.Text.Trim(); _ = Pitches.Parse(tonic);
        current.Title = title; current.Text = ScoreEditor.Text; current.Mode = mode; current.Bpm = bpm; current.Speed = speed; current.Transpose = transpose; current.Tonic = tonic;
        if (mode == Notation.Midi) current.SelectedTracks = TrackPanel.Children.OfType<CheckBox>().Where(c => c.IsChecked == true).Select(c => (int)c.Tag).ToList();
    }
    static double Number(string text, string name, double min, double max)
    {
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double result) || !double.IsFinite(result) || result < min || result > max) throw new FormatException(UiText.T($"{name} 的范围为 {min}–{max}，小数请使用小数点。", $"{name} must be between {min} and {max}. Use a decimal point."));
        return result;
    }
    static int Integer(string text, string name, int min, int max)
    {
        double n = Number(text, name, min, max);
        if (n != Math.Truncate(n)) throw new FormatException(name + UiText.T("必须为整数。", " must be an integer."));
        return (int)n;
    }
    AppSettings CaptureSettings()
    {
        var map = keyBoxes.ToDictionary(x => x.Key, x => x.Value.Text.Trim());
        var value = new AppSettings { Language = settings.Language, Instrument = settings.Instrument, KeyMap = settings.Instrument == InstrumentMode.Guitar ? map : new(settings.KeyMap), PianoKeyMap = settings.Instrument == InstrumentMode.Piano ? map : new(settings.PianoKeyMap), Countdown = Number(CountdownBox.Text, UiText.T("倒计时", "Countdown"), 1, 30), PressMilliseconds = Integer(PressBox.Text, UiText.T("按键时间", "Key press"), 10, 500), SustainNotes = SustainBox.IsChecked == true, Loops = Integer(LoopsBox.Text, UiText.T("循环次数", "Loops"), 1, 100), ToggleKey = ToggleKeyBox.Text.Trim(), StopKey = StopKeyBox.Text.Trim() };
        KeyboardLayout.Validate(value);
        value.ToggleKey = KeyboardLayout.Keys[value.ToggleKey].Name; value.StopKey = KeyboardLayout.Keys[value.StopKey].Name;
        foreach (int pitch in value.ActiveKeyMap.Keys.ToArray()) value.ActiveKeyMap[pitch] = KeyboardLayout.Binding(value.ActiveKeyMap[pitch], value.Instrument).Name;
        return value;
    }
    void ApplySettings()
    {
        RequireStopped();
        var candidate = CaptureSettings(); hotkeys?.Register(candidate.ToggleKey, candidate.StopKey);
        store.SaveSettings(candidate); settings = candidate; output.KeyMap = new(settings.ActiveKeyMap); output.Instrument = settings.Instrument;
    }
    void Instrument_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (loading || InstrumentBox.SelectedIndex < 0 || (int)settings.Instrument == InstrumentBox.SelectedIndex) return;
        Guard(() =>
        {
            try
            {
                RequireStopped();
                var candidate = CaptureSettings();
                candidate.Instrument = (InstrumentMode)InstrumentBox.SelectedIndex;
                KeyboardLayout.Validate(candidate);
                hotkeys?.Register(candidate.ToggleKey, candidate.StopKey);
                store.SaveSettings(candidate); settings = candidate;
                output.KeyMap = new(settings.ActiveKeyMap); output.Instrument = settings.Instrument;
                BuildKeyboard();
                ValidationText.Text = UiText.T("已切换乐器，请重新校验；演奏前进入游戏对应乐器界面。", "Instrument changed. Validate the score again and open the matching instrument in the game.");
                StatusText.Text = settings.Instrument == InstrumentMode.Piano ? UiText.T("已切换钢琴模式", "Switched to piano") : UiText.T("已切换吉他模式", "Switched to guitar");
            }
            catch { loading = true; InstrumentBox.SelectedIndex = (int)settings.Instrument; loading = false; throw; }
        });
    }
    CompiledScore Compile()
    {
        CaptureSong();
        if (current == null) throw new FormatException(UiText.T("请先选择曲谱。", "Select a score first."));
        var data = current.Mode == Notation.Midi ? MidiImporter.Import(Convert.FromBase64String(current.MidiBase64!), current.SelectedTracks) : TextScoreParser.Parse(current.Text, current.Mode, current.Bpm, current.Tonic);
        var compiled = ScoreCompiler.Compile(data, current.Transpose, current.Speed, settings.Instrument);
        string instrument = settings.Instrument == InstrumentMode.Piano ? UiText.T("钢琴 C3–B5", "Piano C3–B5") : UiText.T("吉他 E3–D♯6", "Guitar E3–D#6");
        ValidationText.Text = UiText.T($"{instrument} · {compiled.Notes.Count} 个音符 · {TimeLabel(compiled.Duration)} · 八度折回 {compiled.Adjusted} 个 · 同音合并 {compiled.Merged} 个", $"{instrument} · {compiled.Notes.Count} notes · {TimeLabel(compiled.Duration)} · Folded {compiled.Adjusted} · Merged {compiled.Merged}");
        return compiled;
    }
    void SaveCurrent()
    {
        CaptureSong(); if (current == null) return;
        store.SaveSong(current); dirty = false; RefreshSongs();
    }
    void LoadSong(SongProject song)
    {
        loading = true;
        try
        {
            current = song;
            TitleBox.Text = song.Title; ScoreEditor.Text = song.Text; ModeBox.SelectedIndex = (int)song.Mode; TonicBox.Text = song.Tonic;
            BpmBox.Text = F(song.Bpm); SpeedBox.Text = F(song.Speed); TransposeBox.Text = song.Transpose.ToString();
            TrackPanel.Children.Clear();
            if (song.MidiBase64 != null)
            {
                foreach (var track in MidiImporter.Inspect(Convert.FromBase64String(song.MidiBase64)))
                {
                    var cb = new CheckBox { Content = UiText.T($"{track.Index + 1}. {track.Name} · {track.NoteCount} 音", $"{track.Index + 1}. {track.Name} · {track.NoteCount} notes"), Tag = track.Index, IsChecked = song.SelectedTracks.Contains(track.Index), IsEnabled = track.NoteCount > 0 };
                    cb.Checked += Editor_Changed; cb.Unchecked += Editor_Changed; TrackPanel.Children.Add(cb);
                }
            }
            UpdateMode(); dirty = false; ValidationText.Text = UiText.T("可先校验乐谱或进行可视预演。", "Validate the score or try visual preview.");
        }
        finally { loading = false; }
    }
    void UpdateMode()
    {
        if (ScoreEditor == null || MidiPanel == null) return;
        bool midi = ModeBox.SelectedIndex == 2;
        ScoreEditor.Visibility = midi ? Visibility.Collapsed : Visibility.Visible; MidiPanel.Visibility = midi ? Visibility.Visible : Visibility.Collapsed;
        TemplateButton.IsEnabled = !midi; TonicBox.IsEnabled = ModeBox.SelectedIndex == 0; BpmBox.IsEnabled = !midi;
        SyntaxHint.Text = midi ? UiText.T("使用原 MIDI 速度变化；速度倍率与移调仍然生效。", "Uses the MIDI tempo map; speed and transpose still apply.") : ModeBox.SelectedIndex == 0 ? UiText.T("示例 1:1 2:0.5 [1 3 5]:2 0:1  |  ' 升八度，, 降八度；// 注释", "Example: 1:1 2:0.5 [1 3 5]:2 0:1 | ' up an octave, , down; // comment") : UiText.T("示例 E4:1 F#4:0.5 [G4 B4]:2 R:1  |  冒号后为拍数；// 注释", "Example: E4:1 F#4:0.5 [G4 B4]:2 R:1 | beats after :; // comment");
    }
    void AddSong(SongProject song)
    {
        SaveCurrent(); store.SaveSong(song); songs.Add(song);
        SongSearchBox.Clear(); RefreshSongs(); SongList.SelectedItem = song;
        SongList.ScrollIntoView(song);
    }
    void SongList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (loading || SongList.SelectedItem is not SongProject next || next == current) return;
        Guard(() =>
        {
            try
            {
                RequireStopped(); if (current != null && dirty) SaveCurrent(); LoadSong(next);
                RefreshSongs();
            }
            catch { loading = true; SongList.SelectedItem = current; loading = false; throw; }
        });
    }
    void Editor_Changed(object sender, RoutedEventArgs e)
    {
        if (!loading && sender == SustainBox) PressBox.IsEnabled = SustainBox.IsChecked != true && !IsBusy;
        if (!loading) { dirty = true; if (ValidationText != null) ValidationText.Text = UiText.T("乐谱或参数已修改，请重新校验。", "Score or settings changed. Validate again."); }
    }
    void Mode_Changed(object sender, SelectionChangedEventArgs e) { UpdateMode(); Editor_Changed(sender, e); }
    void New_Click(object sender, RoutedEventArgs e) => Guard(() => { RequireStopped(); AddSong(new()); });
    void Copy_Click(object sender, RoutedEventArgs e) => Guard(() => { RequireStopped(); CaptureSong(); if (current == null) return; var copy = LibraryStore.Copy(current); copy.Id = Guid.NewGuid().ToString("N"); copy.Title = current.Title.Length > 90 ? current.Title[..90] + UiText.T(" · 副本", " · Copy") : current.Title + UiText.T(" · 副本", " · Copy"); AddSong(copy); });
    void Delete_Click(object sender, RoutedEventArgs e) => Guard(() =>
    {
        RequireStopped(); if (current == null) return;
        if (MessageBox.Show(this, UiText.T($"删除工具内的曲谱「{current.Title}」？原始导入文件不受影响。", $"Delete the library copy of {current.Title}? The original imported file is unaffected."), UiText.T("删除曲谱副本", "Delete library copy"), MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        var removed = current; store.DeleteSong(removed); loading = true;
        songs.Remove(removed); if (songs.Count == 0) { var fresh = new SongProject(); songs.Add(fresh); store.SaveSong(fresh); }
        current = null; RefreshSongs();
        if (SongList.Items.Count == 0) SongSearchBox.Clear();
        loading = false; SongList.SelectedIndex = 0;
    });
    void ImportMidi_Click(object sender, RoutedEventArgs e) => Guard(() =>
    {
        RequireStopped();
        var dialog = new OpenFileDialog { Filter = UiText.T("MIDI 乐谱|*.mid;*.midi", "MIDI score|*.mid;*.midi"), Title = UiText.T("导入 MIDI（保留原始文件）", "Import MIDI (original file is retained)") };
        if (dialog.ShowDialog(this) != true) return;
        ImportMidiFile(dialog.FileName);
    });
    internal bool ImportMidiFile(string path)
    {
        RequireStopped();
        var song = MidiLibraryImport.ReadSong(path);
        string key = SongDuplicates.MidiKey(song)!;
        var existing = songs.FirstOrDefault(s => SongDuplicates.MidiKey(s) == key);
        if (existing != null)
        {
            SaveCurrent(); SongSearchBox.Clear(); RefreshSongs(); SongList.SelectedItem = existing;
            SongList.ScrollIntoView(existing); StatusText.Text = UiText.T("曲库已有相同 MIDI，已跳过重复导入", "Identical MIDI already exists. Duplicate import skipped.");
            return false;
        }
        AddSong(song); return true;
    }
    async void ImportMidiFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            RequireStopped();
            var dialog = new OpenFolderDialog { Title = UiText.T("导入文件夹中的全部 MIDI（包含子文件夹）", "Import all MIDI files in a folder (including subfolders)") };
            if (dialog.ShowDialog(this) != true) return;
            var result = await ImportMidiFolderAsync(dialog.FolderName);
            if (result.Failures.Count > 0)
            {
                var report = new TextBox
                {
                    Text = UiText.T($"成功导入 {result.Songs.Count} 首；跳过重复 {result.DuplicateCount} 首；文件或目录失败 {result.Failures.Count} 项。\n\n", $"Imported {result.Songs.Count}; skipped {result.DuplicateCount} duplicates; {result.Failures.Count} file or folder failures.\n\n") +
                        string.Join("\n\n", result.Failures.Select(f => $"{f.Path}\n{f.Reason}")),
                    IsReadOnly = true, TextWrapping = TextWrapping.Wrap, AcceptsReturn = true,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(16)
                };
                new Window { Owner = this, Title = UiText.T("MIDI 批量导入结果", "MIDI folder import results"), Width = 640, Height = 440,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = report,
                    Style = (Style)FindResource("AppWindow") }.ShowDialog();
            }
        }
        catch (Exception error) { Guard(() => throw error); }
    }
    internal async Task<MidiFolderImportResult> ImportMidiFolderAsync(string folder)
    {
        RequireStopped(); SaveCurrent();
        importing = true; WorkspaceGrid.IsEnabled = false;
        StatusText.Text = UiText.T("正在扫描 MIDI 文件夹…", "Scanning MIDI folder…");
        ValidationText.Text = UiText.T("包含子文件夹；导入完成后可分别选择音轨。", "Includes subfolders. Choose tracks for each score after import.");
        var progress = new Progress<MidiImportProgress>(p =>
        {
            if (!importing) return;
            StatusText.Text = UiText.T($"正在导入 MIDI · {p.Completed}/{p.Total}", $"Importing MIDI · {p.Completed}/{p.Total}");
            ProgressText.Text = p.FileName;
            SongProgress.Value = (double)p.Completed / p.Total;
        });
        try
        {
            var result = await Task.Run(() => MidiLibraryImport.ImportFolder(folder, store.SaveSong, progress, songs));
            loading = true;
            try
            {
                foreach (var song in result.Songs) songs.Add(song);
                if (result.Songs.Count > 0) SongSearchBox.Clear();
                RefreshSongs();
                if (result.Songs.Count > 0) { SongList.SelectedItem = result.Songs[0]; LoadSong(result.Songs[0]); }
            }
            finally { loading = false; }
            StatusText.Text = UiText.T($"导入完成 · 新增 {result.Songs.Count} · 重复 {result.DuplicateCount} · 失败 {result.Failures.Count}", $"Import complete · Added {result.Songs.Count} · Duplicates {result.DuplicateCount} · Failed {result.Failures.Count}");
            ProgressText.Text = result.FileCount == 0 ? UiText.T("未找到 .mid / .midi 文件。", "No .mid / .midi files found.") : UiText.T($"共找到 {result.FileCount} 个 MIDI 文件，包含子文件夹。", $"Found {result.FileCount} MIDI files, including subfolders.");
            ValidationText.Text = UiText.T("原始文件保持不变；已导入的曲谱可分别选择音轨、校验或预演。", "Original files are unchanged. Select tracks, validate or preview imported scores.");
            SongProgress.Value = result.FileCount == 0 ? 0 : 1;
            return result;
        }
        finally { importing = false; WorkspaceGrid.IsEnabled = true; }
    }
    void Save_Click(object sender, RoutedEventArgs e) => Guard(() => { RequireStopped(); SaveCurrent(); StatusText.Text = UiText.T("曲谱已保存", "Score saved"); });
    void Template_Click(object sender, RoutedEventArgs e) => Guard(() =>
    {
        RequireStopped();
        if (MessageBox.Show(this, UiText.T("用示例替换当前编辑内容？已保存的其他曲谱不受影响。", "Replace the current editor content with an example? Other saved scores are unaffected."), UiText.T("载入示例", "Load example"), MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        ScoreEditor.Text = ModeBox.SelectedIndex == 0 ? "// 两只老虎 · 1=C4\n1 2 3 1 | 1 2 3 1\n3 4 5:2 | 3 4 5:2\n5:0.5 6:0.5 5:0.5 4:0.5 3 1\n5:0.5 6:0.5 5:0.5 4:0.5 3 1\n1 5, 1:2 | 1 5, 1:2" : "[E4 G4 B4]:2 R:1 E4:0.5 F#4:0.5\nG4:1 A4:1 B4:2 R:1 [A3 C4 E4]:2";
    });
    void RefreshWindows_Click(object sender, RoutedEventArgs e) => Guard(() => { RequireStopped(); RefreshWindows(); });
    void ApplySettings_Click(object sender, RoutedEventArgs e) => Guard(() => { ApplySettings(); StatusText.Text = UiText.T($"已应用 · {settings.ToggleKey} 开始/暂停 · {settings.StopKey} 急停", $"Applied · {settings.ToggleKey} Play/Pause · {settings.StopKey} Stop"); });
    void ResetMap_Click(object sender, RoutedEventArgs e) => Guard(() => { RequireStopped(); foreach (var pair in KeyboardLayout.DefaultMap(settings.Instrument)) keyBoxes[pair.Key].Text = pair.Value; StatusText.Text = UiText.T("已填入当前乐器默认键位，请点击应用", "Default keys filled in for this instrument. Click Apply keys."); });
    void Validate_Click(object sender, RoutedEventArgs e) => Guard(() => { RequireStopped(); _ = Compile(); StatusText.Text = UiText.T("曲谱校验通过", "Score validated"); });
    void Begin(bool preview)
    {
        RequireStopped();
        if (!preview)
        {
            if (WindowBox.SelectedItem is not WindowChoice target) throw new InvalidOperationException(UiText.T("请先刷新并选择游戏窗口。", "Refresh and select the game window first."));
            RefreshAccess(); InputAccess.Check(target.ProcessId).RequireCompatible();
        }
        var compiled = Compile(); ApplySettings(); SaveCurrent();
        if (!preview)
        {
            if (WindowBox.SelectedItem is not WindowChoice target) throw new InvalidOperationException(UiText.T("请先刷新并选择游戏窗口。", "Refresh and select the game window first."));
            output.Target = target;
        }
        engine.Start(compiled, settings.Countdown, settings.PressMilliseconds, settings.Loops, preview, settings.SustainNotes);
    }
    void Toggle()
    {
        switch (engine.Status.State)
        {
            case PlaybackState.Playing: case PlaybackState.Countdown: engine.Pause(UiText.T($"已暂停；{settings.ToggleKey} 继续", $"Paused; {settings.ToggleKey} to resume")); break;
            case PlaybackState.Paused: engine.Resume(); break;
            default: Begin(false); break;
        }
    }
    void Preview_Click(object sender, RoutedEventArgs e) => Guard(() => Begin(true));
    void Play_Click(object sender, RoutedEventArgs e) => Guard(Toggle);
    void Stop_Click(object sender, RoutedEventArgs e) => engine.Stop();
    static string TimeLabel(double seconds) => seconds >= 3600 ? TimeSpan.FromSeconds(seconds).ToString(@"h\:mm\:ss") : TimeSpan.FromSeconds(seconds).ToString(@"m\:ss");
    void ShowStatus(PlaybackStatus s)
    {
        if (importing) return;
        StatusText.Text = s.State == PlaybackState.Countdown ? UiText.T($"{s.Message} · {Math.Ceiling(s.Countdown)} 秒", $"{s.Message} · {Math.Ceiling(s.Countdown)} s") : s.Message;
        ProgressText.Text = UiText.T($"{TimeLabel(s.Position)} / {TimeLabel(s.Duration)}   ·   第 {s.Loop} / {s.Loops} 次   ·   {settings.ToggleKey} 开始/暂停  {settings.StopKey} 急停", $"{TimeLabel(s.Position)} / {TimeLabel(s.Duration)} · Loop {s.Loop}/{s.Loops} · {settings.ToggleKey} Play/Pause · {settings.StopKey} Stop");
        SongProgress.Value = s.Duration == 0 ? 0 : Math.Clamp(s.Position / s.Duration, 0, 1);
        bool busy = s.State is PlaybackState.Playing or PlaybackState.Countdown or PlaybackState.Paused;
        foreach (var control in new Control[] { LanguageBox, InstrumentBox, TitleBox, ModeBox, TonicBox, BpmBox, SpeedBox, TransposeBox, CountdownBox, PressBox, LoopsBox, ScoreEditor, ToggleKeyBox, StopKeyBox, WindowBox, SongList, SongSearchBox, ClearSearchButton }) control.IsEnabled = !busy;
        if (!busy) { TonicBox.IsEnabled = ModeBox.SelectedIndex == 0; BpmBox.IsEnabled = ModeBox.SelectedIndex != 2; }
        TrackPanel.IsEnabled = !busy; PreviewButton.IsEnabled = !busy; SustainBox.IsEnabled = !busy;
        ElevateButton.IsEnabled = !busy;
        PressBox.IsEnabled = !busy && SustainBox.IsChecked != true;
        foreach (var pair in keyBoxes) pair.Value.IsEnabled = !busy;
        foreach (var pair in keyTiles) pair.Value.Background = new SolidColorBrush(s.ActivePitches.Contains(pair.Key) ? Color.FromRgb(43, 130, 114) : Color.FromRgb(34, 49, 70));
        PlayButton.Content = s.State is PlaybackState.Playing or PlaybackState.Countdown ? UiText.T("暂停", "Pause") : s.State == PlaybackState.Paused ? UiText.T("继续", "Resume") : UiText.T("开始演奏", "Play");
        if (s.ActivePitches.Length > 0) ValidationText.Text = string.Join("   ", s.ActivePitches.Where(settings.ActiveKeyMap.ContainsKey).Select(p => $"{Pitches.Name(p)} → {settings.ActiveKeyMap[p]}"));
        else if (s.State is PlaybackState.Completed or PlaybackState.Idle) ValidationText.Text = UiText.T("可选择曲谱、校验或预演；实际演奏前请进入游戏对应乐器界面。", "Select, validate or preview a score. Open the matching instrument in the game before playing.");
    }
    void OpenLibrary_Click(object sender, RoutedEventArgs e) => Guard(() => Process.Start(new ProcessStartInfo(store.Root) { UseShellExecute = true }));
    void OnClosing(object? sender, CancelEventArgs e)
    {
        if (importing) { e.Cancel = true; StatusText.Text = UiText.T("正在保存导入曲谱，请等待完成后关闭。", "Saving imported scores. Wait until import completes before closing."); return; }
        engine.Stop();
        if (dirty)
        {
            try { SaveCurrent(); }
            catch (Exception error)
            {
                if (MessageBox.Show(this, UiText.T("无法保存当前编辑：", "Cannot save the current edits: ") + error.Message + UiText.T("\n仍然关闭并放弃未保存编辑？", "\nClose anyway and discard unsaved edits?"), UiText.T("关闭前保存", "Save before closing"), MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) { e.Cancel = true; return; }
            }
        }
        shuttingDown = true; engine.Dispose(); hotkeys?.Dispose();
    }
}
