using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GuitarNTE.App;

// Component tests operate on this process's own WPF controls and visual tree.
internal static class UiSmoke
{
    static MainWindow window = null!;
    static string root = "";
    static bool headless;
    static void Layout()
    {
        if (!headless) { window.Show(); return; }
        var content = (FrameworkElement)window.Content;
        // Render in a zero-margin host so the root's window margin is included exactly once.
        window.Content = null;
        host = new Border { Background = window.Background, Child = content };
        window.Content = host;
        host.Measure(new Size(1232, 880)); host.Arrange(new Rect(0, 0, 1232, 880)); host.UpdateLayout();
    }
    static Border host = null!;
    static T Find<T>(string name) where T : class => (window.FindName(name) as T) ?? throw new Exception("Control missing: " + name);
    static void Assert(bool ok, string reason) { if (!ok) throw new Exception(reason); }
    static void Click(string content)
    {
        Console.WriteLine("UI click " + content);
        var button = Descendants(headless ? (DependencyObject)window.Content : window).OfType<Button>().Single(b => b.Content?.ToString() == content);
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
    }
    static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i); yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
    static void Pump()
    {
        var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false)); Dispatcher.PushFrame(frame);
    }
    static void Wait(double seconds)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < until) { Pump(); Thread.Sleep(10); }
    }
    static void Capture(string name)
    {
        Pump(); if (headless) host.UpdateLayout(); else window.UpdateLayout();
        var visual = headless ? (FrameworkElement)host : window;
        var bitmap = new RenderTargetBitmap((int)visual.ActualWidth, (int)visual.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var file = File.Create(Path.Combine(root, name + ".png")); encoder.Save(file);
    }
    internal static int Run(bool withoutDesktop = false)
    {
        headless = withoutDesktop;
        root = Path.GetFullPath(headless ? "artifacts/ui-headless/中文路径" : "artifacts/ui-smoke/中文路径"); Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable("GUITAR_NTE_DATA", Path.Combine(root, "data-" + Guid.NewGuid().ToString("N")));
        new LibraryStore().SaveSettings(new GuitarNTE.Core.AppSettings { Language = "zh-CN" });
        // The harness creates its own window and must not register production global hotkeys.
        var app = new GuitarNTE.App.App { SuppressStartupWindow = true }; app.InitializeComponent(); app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        try
        {
            Console.WriteLine("UI initialize"); window = new MainWindow(!headless); Layout(); Pump(); Console.WriteLine("UI ready");
            var chineseResources = new ResourceDictionary { Source = new Uri("/GuitarNTE;component/Strings/zh-CN.xaml", UriKind.Relative) };
            var englishResources = new ResourceDictionary { Source = new Uri("/GuitarNTE;component/Strings/en.xaml", UriKind.Relative) };
            Assert(chineseResources.Keys.Cast<string>().Order().SequenceEqual(englishResources.Keys.Cast<string>().Order()), "both language dictionaries contain the same keys");
            Assert(englishResources.Values.Cast<string>().All(value => !value.Any(c => c is >= '\u4e00' and <= '\u9fff')), "English resource values contain no untranslated Chinese labels");
            Assert(((SolidColorBrush)window.Background).Color == Color.FromRgb(12, 19, 32), "dark window background");
            Assert(((SolidColorBrush)window.Foreground).Color == Color.FromRgb(231, 238, 248), "readable inherited foreground");
            Assert(Find<TextBox>("ScoreEditor").Text.Length > 10, "initial score loaded");
            Assert(Find<CheckBox>("SustainBox").IsChecked == true && !Find<TextBox>("PressBox").IsEnabled, "sustain default with short press inactive");
            Assert(Find<TextBlock>("AccessText").Text.Length > 0, "input access status displayed");
            Click("校验乐谱"); Assert(Find<TextBlock>("StatusText").Text.Contains("通过"), "initial validation"); Capture("01-editor");
            Click("＋ 新建"); Find<TextBox>("TitleBox").Text = "Bach BWV 846 · 中文烟测曲谱"; Find<TextBox>("ScoreEditor").Text = "1 2 3 [1 3 5]:2 0:1";
            Click("保存曲谱"); Assert(Find<ListBox>("SongList").Items.Count == 3, "create save"); Click("复制"); Assert(Find<ListBox>("SongList").Items.Count == 4, "copy");
            var search = Find<TextBox>("SongSearchBox"); var list = Find<ListBox>("SongList");
            string draft = Find<TextBox>("ScoreEditor").Text + "\n// 未保存的编辑";
            Find<TextBox>("ScoreEditor").Text = draft;
            search.Text = "   bach   846  "; Pump();
            Assert(list.Items.Count == 2 && Find<TextBlock>("SongCountText").Text.Contains("2 / 4"), "case-insensitive multi-keyword name search");
            search.Text = "完全不存在的曲名"; Pump();
            Assert(list.Items.Count == 0 && list.SelectedItem == null && Find<StackPanel>("EmptyLibraryHint").Visibility == Visibility.Visible, "empty search state");
            Assert(Find<TextBox>("ScoreEditor").Text == draft, "filter does not replace unsaved editor");
            Capture("06-search-empty"); Click("×");
            Assert(list.Items.Count == 4 && list.SelectedItem != null && Find<TextBox>("ScoreEditor").Text == draft, "clear search restores selection and draft");
            search.Text = "两只"; Pump(); Assert(list.Items.Count == 1, "Chinese partial title search");
            search.Clear(); Click("保存曲谱");
            string oldTitle = Find<TextBox>("TitleBox").Text;
            search.Text = "bach 846"; Find<TextBox>("TitleBox").Text = "Debussy · 重命名测试"; Click("保存曲谱");
            Assert(list.Items.Count == 1 && Find<TextBox>("TitleBox").Text == "Debussy · 重命名测试", "saved rename refreshes filtered list without replacing editor");
            search.Clear(); Find<TextBox>("TitleBox").Text = oldTitle; Click("保存曲谱");
            Find<Expander>("KeyboardExpander").IsExpanded = true; Pump();
            TextBox[] Keys() => Descendants(Find<StackPanel>("KeyboardPanel")).OfType<TextBox>().ToArray();
            Keys()[24].Text = "Z";
            Find<ComboBox>("InstrumentBox").SelectedIndex = 1; Pump();
            Assert(Keys()[0].Text == "Q" && Keys()[1].Text == "Shift+Q" && Keys()[3].Text == "Ctrl+E" && Keys()[24].Text == "Z", "piano screenshot keys");
            Keys()[24].Text = "K";
            Find<ComboBox>("InstrumentBox").SelectedIndex = 0; Pump(); Assert(Keys()[24].Text == "Z", "guitar custom key preserved");
            Find<ComboBox>("InstrumentBox").SelectedIndex = 1; Pump(); Assert(Keys()[24].Text == "K", "piano custom key preserved");
            Click("恢复默认键位"); Assert(Keys()[24].Text == "Z", "reset only piano map");
            Click("校验乐谱"); Assert(Find<TextBlock>("ValidationText").Text.Contains("钢琴 C3–B5"), "piano compiler selected");
            Find<TextBox>("CountdownBox").Text = "1";
            Click("▷ 可视预演"); Wait(1.15); Assert(Find<TextBlock>("StatusText").Text.Contains("预演"), "preview running"); Assert(!Find<TextBox>("ScoreEditor").IsEnabled && !Find<ComboBox>("InstrumentBox").IsEnabled && !search.IsEnabled, "editor, instrument and search locked during playback"); Capture("02-preview");
            Click("暂停"); Assert(Find<TextBlock>("StatusText").Text.Contains("暂停"), "pause"); Click("继续"); Assert(Find<TextBlock>("StatusText").Text.Contains("倒计时"), "resume countdown"); Click("■ 急停"); Assert(Find<TextBox>("ScoreEditor").IsEnabled, "stop unlock");
            Find<Expander>("KeyboardExpander").IsExpanded = true; Pump();
            var scroll = Find<ScrollViewer>("EditorScroll"); scroll.ScrollToBottom(); Pump(); Capture("03-keyboard");
            Assert(Find<StackPanel>("KeyboardPanel").Children.OfType<System.Windows.Controls.Primitives.UniformGrid>().Count() == 3 && Keys().Length == 36, "3 keyboard rows and 36 bindings");
            scroll.ScrollToVerticalOffset(220); Pump(); Capture("04-piano-mode");
            Assert(Find<Button>("ImportFolderButton").Content.ToString() == "导入文件夹", "folder import entry visible");
            string midiFolder = Path.Combine(root, "批量 MIDI"), childFolder = Path.Combine(midiFolder, "子文件夹");
            Directory.CreateDirectory(childFolder);
            File.WriteAllBytes(Path.Combine(midiFolder, "批量一.MID"), Program.Fixture());
            File.WriteAllBytes(Path.Combine(childFolder, "批量二.midi"), Program.Fixture(65));
            File.WriteAllBytes(Path.Combine(childFolder, "批量一的重复副本.mid"), Program.Fixture());
            search.Text = "不会匹配新曲";
            var import = window.ImportMidiFolderAsync(midiFolder);
            Assert(!Find<Grid>("WorkspaceGrid").IsEnabled, "workspace locked while importing");
            window.Close(); Assert(!Find<Grid>("WorkspaceGrid").IsEnabled, "close deferred while import saves");
            var importTimeout = DateTime.UtcNow.AddSeconds(30);
            while (!import.IsCompleted && DateTime.UtcNow < importTimeout) { Pump(); Thread.Sleep(10); }
            Assert(import.IsCompleted, "background import finishes");
            var imported = import.GetAwaiter().GetResult(); Pump();
            Assert(imported.Songs.Count == 2 && imported.DuplicateCount == 1 && imported.Failures.Count == 0 && Find<Grid>("WorkspaceGrid").IsEnabled, "batch deduplicates and completion unlocks workspace");
            Assert(Find<ListBox>("SongList").Items.Count == 6 && Find<ComboBox>("ModeBox").SelectedIndex == 2, "batch appended and first MIDI selected");
            Assert(search.Text == "" && Find<TextBlock>("StatusText").Text.Contains("重复 1"), "batch reveals new songs and duplicate summary");
            Assert(!window.ImportMidiFile(Path.Combine(midiFolder, "批量一.MID")) && list.Items.Count == 6, "single file duplicate skipped");
            Capture("05-folder-import"); Click("校验乐谱");
            Assert(Find<TextBlock>("StatusText").Text.Contains("通过"), "imported MIDI validates");
            Find<Expander>("KeyboardExpander").IsExpanded = false; scroll.ScrollToTop(); Pump(); Capture("07-redesigned-library");
            search.Text = "bach 846"; Pump(); Capture("08-search-results"); search.Clear();
            if (headless)
            {
                host.Measure(new Size(1004, 681)); host.Arrange(new Rect(0, 0, 1004, 681)); host.UpdateLayout(); Pump();
                foreach (var name in new[] { "SongSearchBox", "PlayButton", "PreviewButton", "StatusText" })
                {
                    var control = Find<FrameworkElement>(name); Point origin = control.TransformToAncestor(host).Transform(new Point());
                    Assert(origin.X >= 0 && origin.Y >= 0 && origin.X + control.ActualWidth <= host.ActualWidth + 1 && origin.Y + control.ActualHeight <= host.ActualHeight + 1, "minimum window keeps control visible: " + name);
                }
                Capture("09-compact-layout");
            }
            window.Close(); Pump(); window = new MainWindow(!headless); Layout(); Pump();
            Assert(Find<ListBox>("SongList").Items.Count == 6, "library including batch survives reopen");
            Assert(Find<TextBox>("CountdownBox").Text == "1", "settings survive reopen");
            Assert(Find<ComboBox>("InstrumentBox").SelectedIndex == 1 && Keys()[24].Text == "Z", "piano mode survives reopen");
            Find<ComboBox>("InstrumentBox").SelectedIndex = 0; Pump(); Assert(Keys()[24].Text == "Z", "custom guitar map survives reopen and piano reset");
            // Verify startup cleanup through the actual window constructor with a recoverable backup.
            window.Close(); Pump();
            var store = new LibraryStore(); var duplicate = LibraryStore.Copy(store.LoadSongs().First(s => s.Mode == GuitarNTE.Core.Notation.Midi));
            duplicate.Id = Guid.NewGuid().ToString("N"); duplicate.Title = "历史重复导入"; store.SaveSong(duplicate);
            window = new MainWindow(!headless); Layout(); Pump();
            Assert(Find<ListBox>("SongList").Items.Count == 6 && Find<TextBlock>("StatusText").Text.Contains("清理 1"), "startup duplicate cleanup and summary");
            Assert(Directory.GetFiles(Path.Combine(store.Root, "DuplicateBackups"), "*.json", SearchOption.AllDirectories).Length == 1, "startup duplicates backed up");
            // Live translation must preserve invalid drafts and un-applied keyboard edits.
            list = Find<ListBox>("SongList");
            list.SelectedItem = list.Items.OfType<GuitarNTE.Core.SongProject>().First(s => s.Mode == GuitarNTE.Core.Notation.Numbered);
            Find<TextBox>("TitleBox").Text = "Bilingual draft · 双语草稿";
            Find<TextBox>("ScoreEditor").Text = "1 2 3 [1 3 5]:2 0:1\n// Unchanged user content";
            Find<TextBox>("BpmBox").Text = "invalid numeric draft";
            Keys()[24].Text = "unapplied binding";
            string unchangedDraft = Find<TextBox>("ScoreEditor").Text;
            Find<ComboBox>("LanguageBox").SelectedIndex = 1; Pump();
            Assert(window.Title == "NTE Piano & Guitar Player" && Find<Button>("ImportFolderButton").Content.ToString() == "Import folder", "window and static controls switch to English");
            Assert(Find<TextBox>("ScoreEditor").Text == unchangedDraft && Find<TextBox>("BpmBox").Text == "invalid numeric draft" && Keys()[24].Text == "unapplied binding", "language switching preserves invalid editor and keyboard drafts");
            Assert(new LibraryStore().LoadSettings().Language == "en", "English preference saved independently of invalid numeric fields");
            Assert(Find<TextBlock>("SyntaxHint").Text.Contains("Example") && Find<TextBlock>("InstrumentHint").Text.Contains("three rows"), "dynamic hints in English");
            Find<ComboBox>("LanguageBox").SelectedIndex = 0; Pump();
            Assert(window.Title == "异环钢琴吉他演奏工具" && Find<Button>("ImportFolderButton").Content.ToString() == "导入文件夹", "switch back to exact Chinese title");
            Find<Expander>("KeyboardExpander").IsExpanded = true; Pump();
            Find<TextBox>("BpmBox").Text = "120"; Click("恢复默认键位");
            Find<ComboBox>("LanguageBox").SelectedIndex = 1; Pump();
            Click("Validate"); Assert(Find<TextBlock>("StatusText").Text == "Score validated", "English validation result");
            Find<TextBox>("CountdownBox").Text = "1"; Click("▷ Preview"); Wait(1.15);
            Assert(!Find<ComboBox>("LanguageBox").IsEnabled && Find<TextBlock>("StatusText").Text.Contains("preview"), "language selection disabled during playback and preview message translated");
            Click("■ Stop"); Assert(Find<ComboBox>("LanguageBox").IsEnabled, "stop restores language selection");
            Find<Expander>("KeyboardExpander").IsExpanded = false; Find<ScrollViewer>("EditorScroll").ScrollToTop(); Pump(); Capture("10-english-main");
            if (headless)
            {
                host.Measure(new Size(1004, 681)); host.Arrange(new Rect(0, 0, 1004, 681)); host.UpdateLayout(); Pump();
                foreach (var name in new[] { "LanguageBox", "ImportFolderButton", "PlayButton", "PreviewButton" })
                {
                    var control = Find<FrameworkElement>(name); Point origin = control.TransformToAncestor(host).Transform(new Point());
                    Assert(origin.X >= 0 && origin.Y >= 0 && origin.X + control.ActualWidth <= host.ActualWidth + 1 && origin.Y + control.ActualHeight <= host.ActualHeight + 1, "English compact layout keeps controls visible: " + name);
                }
                var brand = Find<TextBlock>("BrandTitle"); var languageControls = Find<StackPanel>("LanguageControls");
                double brandRight = brand.TransformToAncestor(host).Transform(new Point()).X + brand.DesiredSize.Width;
                double languageLeft = languageControls.TransformToAncestor(host).Transform(new Point()).X;
                Assert(brandRight + 12 <= languageLeft, "full English title does not overlap language controls in compact layout");
                Capture("11-english-compact");
            }
            window.Close(); Pump(); window = new MainWindow(!headless); Layout(); Pump();
            Assert(window.Title == "NTE Piano & Guitar Player" && Find<ComboBox>("LanguageBox").SelectedIndex == 1, "English survives reopening");
            Find<ListBox>("SongList").SelectedItem = Find<ListBox>("SongList").Items.OfType<GuitarNTE.Core.SongProject>().Single(s => s.Title == "Bilingual draft · 双语草稿"); Pump();
            Assert(Find<TextBox>("ScoreEditor").Text == unchangedDraft, "user score text survives language switch and reopening");
            Find<ComboBox>("LanguageBox").SelectedIndex = 0; Pump(); Capture("12-chinese-main");
            Console.WriteLine("UI_SMOKE PASS: library, playback, import, deduplication, live bilingual UI, invalid draft preservation, language persistence and compact layouts");
            Console.WriteLine("UI_ARTIFACTS " + root); return 0;
        }
        catch (Exception e) { Console.WriteLine("UI_SMOKE FAIL " + e); return 1; }
        finally { window?.Close(); app.Shutdown(); Environment.SetEnvironmentVariable("GUITAR_NTE_DATA", null); }
    }
}
