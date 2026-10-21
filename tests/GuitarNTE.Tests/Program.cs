using GuitarNTE.Core;
using GuitarNTE.App;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Common;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.IO;
using NoteEvent = GuitarNTE.Core.NoteEvent;

internal static class Program
{
    static int passed, failed;
    static void Check(bool condition, string reason) { if (!condition) throw new Exception(reason); }
    static void Near(double expected, double actual, double tolerance = 0.00001) => Check(Math.Abs(expected - actual) < tolerance, $"Expected {expected}, got {actual}");
    static void Throws<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
    static void Test(string name, Action action)
    {
        try { action(); passed++; Console.WriteLine("PASS " + name); }
        catch (Exception e) { failed++; Console.WriteLine("FAIL " + name + " :: " + e); }
    }
    static CompiledScore Compile(string text) => ScoreCompiler.Compile(TextScoreParser.Parse(text, Notation.NoteNames, 120));
    internal static byte[] Fixture(int melodyPitch = 64)
    {
        var tempo = new TrackChunk(new SequenceTrackNameEvent("速度"), new SetTempoEvent(500000), new SetTempoEvent(1000000) { DeltaTime = 480 });
        var melody = new TrackChunk(new SequenceTrackNameEvent("中文旋律"), new NoteOnEvent((SevenBitNumber)melodyPitch, (SevenBitNumber)100), new NoteOffEvent((SevenBitNumber)melodyPitch, (SevenBitNumber)0) { DeltaTime = 480 }, new NoteOnEvent((SevenBitNumber)67, (SevenBitNumber)100), new NoteOffEvent((SevenBitNumber)67, (SevenBitNumber)0) { DeltaTime = 480 }, new TextEvent("尾部休止") { DeltaTime = 480 });
        var bass = new TrackChunk(new NoteOnEvent((SevenBitNumber)28, (SevenBitNumber)80), new NoteOffEvent((SevenBitNumber)28, (SevenBitNumber)0) { DeltaTime = 480 });
        var percussion = new TrackChunk(new NoteOnEvent((SevenBitNumber)36, (SevenBitNumber)100) { Channel = (FourBitNumber)9 }, new NoteOffEvent((SevenBitNumber)36, (SevenBitNumber)0) { Channel = (FourBitNumber)9, DeltaTime = 480 });
        var midi = new MidiFile(tempo, melody, bass, percussion) { TimeDivision = new TicksPerQuarterNoteTimeDivision(480) };
        using var stream = new MemoryStream(); midi.Write(stream, MidiFileFormat.MultiTrack, new WritingSettings { TextEncoding = System.Text.Encoding.UTF8 }); return stream.ToArray();
    }
    [STAThread]
    static int Main(string[] args)
    {
        if (args.Contains("--receiver")) { Receiver.Run(args); return 0; }
        if (args.Contains("--ui-smoke")) { return UiSmoke.Run(); }
        if (args.Contains("--ui-headless")) { return UiSmoke.Run(true); }
        if (args.Contains("--diagnose-input")) { return DiagnoseInput(); }
        if (args.Contains("--validate-project")) { return ValidateProject(args[Array.IndexOf(args, "--validate-project") + 1]); }
        Test("36 notes and every screenshot key", () =>
        {
            string[] expected = "A S D F G H J K L ; ' Enter Q W E R T Y U I O P [ ] 1 2 3 4 5 6 7 8 9 0 - =".Split(' ');
            var map = KeyboardLayout.DefaultMap(); Check(map.Count == 36, "count");
            for (int i = 0; i < 36; i++) Check(map[i + 52] == expected[i], "mapping " + Pitches.Name(i + 52));
            Check(Pitches.Name(52) == "E3" && Pitches.Name(87) == "D#6", "range");
            KeyboardLayout.Validate(new());
            Check(KeyboardLayout.Keys[";"].ScanCode == 39 && KeyboardLayout.Keys["'"].ScanCode == 40 && KeyboardLayout.Keys["Enter"].ScanCode == 28, "scan codes");
        });
        Test("piano screenshot mapping and mode-specific pitch folding", () =>
        {
            var map = KeyboardLayout.DefaultMap(InstrumentMode.Piano);
            string[] expected = "Z Shift+Z X Ctrl+C C V Shift+V B Shift+B N Ctrl+M M A Shift+A S Ctrl+D D F Shift+F G Shift+G H Ctrl+J J Q Shift+Q W Ctrl+E E R Shift+R T Shift+T Y Ctrl+U U".Split(' ');
            Check(map.Count == 36 && Enumerable.Range(48, 36).All(p => map[p] == expected[p - 48]), "all 36 piano bindings");
            KeyboardLayout.Validate(new() { Instrument = InstrumentMode.Piano });
            var compiled = ScoreCompiler.Compile(new([new(36, 0, 1), new(48, 0, 2), new(84, 1, 1), new(83, 2, 1)], 3), instrument: InstrumentMode.Piano);
            Check(compiled.Adjusted == 2 && compiled.Merged == 1 && compiled.Notes.Select(n => n.Pitch).SequenceEqual([48, 72, 83]), "C3-B5 folding and merging");
            Check(KeyboardLayout.Binding("shift+q", InstrumentMode.Piano).Name == "Shift+Q", "normalized combo");
            Throws<FormatException>(() => KeyboardLayout.Binding("Shift+Q", InstrumentMode.Guitar));
            Throws<FormatException>(() => KeyboardLayout.Binding("Alt+Q", InstrumentMode.Piano));
            var s = new AppSettings { Instrument = InstrumentMode.Piano, ToggleKey = "Q" };
            Throws<FormatException>(() => KeyboardLayout.Validate(s));
            s = new() { Instrument = InstrumentMode.Piano }; s.PianoKeyMap[49] = "shift+z"; KeyboardLayout.Validate(s);
            s.PianoKeyMap[50] = "SHIFT+Z"; Throws<FormatException>(() => KeyboardLayout.Validate(s));
        });
        Test("piano chord modifier ordering, shared keys and failure cleanup", () =>
        {
            var strokes = new List<KeyStroke>(); var session = new KeyboardSession(batch => strokes.AddRange(batch));
            var map = KeyboardLayout.DefaultMap(InstrumentMode.Piano);
            session.KeyDown([60, 61, 63, 67], map, InstrumentMode.Piano);
            string Sequence() => string.Join(" ", strokes.Select(s => (s.Up ? "up:" : "down:") + s.Key.Name));
            Check(Sequence() == "down:A down:G up:A down:Shift down:A up:Shift down:Ctrl down:D up:Ctrl", "mixed chord batches and scoped modifiers");
            strokes.Clear(); session.KeyUp([60]); Check(strokes.Count == 0, "old C4 note-off does not release C#4");
            session.KeyUp([61]); Check(Sequence() == "up:A", "current note-off releases shared key");
            strokes.Clear(); session.ReleaseAll(); Check(Sequence() == "up:G up:D", "all remaining keys released");
            strokes.Clear(); session.ReleaseAll(); Check(strokes.Count == 0, "release idempotent");
            bool fail = true;
            var failedSession = new KeyboardSession(batch => { strokes.AddRange(batch); if (fail) throw new IOException("partial native input"); });
            Throws<IOException>(() => failedSession.KeyDown([61], map, InstrumentMode.Piano));
            fail = false; strokes.Clear(); failedSession.ReleaseAll();
            Check(Sequence() == "up:A up:Shift", "partial send releases letter and modifier");
            // Play all 36 notes through the actual scheduler and the native stroke planner.
            var physical = new HashSet<string>(); var sounding = new List<int>();
            var output = new PlannedPianoOutput(batch =>
            {
                foreach (var stroke in batch)
                {
                    if (stroke.Up) physical.Remove(stroke.Key.Name);
                    else
                    {
                        physical.Add(stroke.Key.Name);
                        if (stroke.Key.Name is "Shift" or "Ctrl") continue;
                        int basePitch = map.First(p => p.Value == stroke.Key.Name).Key;
                        sounding.Add(basePitch + (physical.Contains("Shift") ? 1 : physical.Contains("Ctrl") ? -1 : 0));
                    }
                }
            });
            var clock = new FakeClock(); using var engine = new PlaybackEngine(output, clock, false);
            var score = ScoreCompiler.Compile(new(Enumerable.Range(48, 36).Select((p, i) => new NoteEvent(p, i * 0.1, 0.08)).ToArray(), 3.6), instrument: InstrumentMode.Piano);
            engine.Start(score, 0, 40, 1, false, true);
            for (int i = 0; i <= 1800; i++) { clock.Now = i * 0.002; engine.Tick(); }
            Check(sounding.SequenceEqual(Enumerable.Range(48, 36)) && physical.Count == 0 && engine.Status.State == PlaybackState.Completed, "36 correct pitches and no residual physical keys");
        });
        Test("legacy settings and separate instrument maps persist", () =>
        {
            var legacy = System.Text.Json.JsonSerializer.Deserialize<AppSettings>("{\"Countdown\":3}")!;
            KeyboardLayout.Validate(legacy); Check(legacy.Instrument == InstrumentMode.Guitar && legacy.PianoKeyMap.Count == 36, "legacy defaults");
            legacy.KeyMap[52] = "Z"; legacy.PianoKeyMap[48] = "K"; legacy.Instrument = InstrumentMode.Piano;
            var saved = System.Text.Json.JsonSerializer.Serialize(legacy);
            var restored = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(saved)!;
            KeyboardLayout.Validate(restored);
            Check(restored.Instrument == InstrumentMode.Piano && restored.KeyMap[52] == "Z" && restored.PianoKeyMap[48] == "K" && !saved.Contains("ActiveKeyMap"), "independent custom maps round trip");
        });
        Test("mapping duplicates and hotkey overlaps rejected", () =>
        {
            var s = new AppSettings(); s.KeyMap[52] = "F8"; Throws<FormatException>(() => KeyboardLayout.Validate(s));
            s = new(); s.KeyMap[52] = "s"; Throws<FormatException>(() => KeyboardLayout.Validate(s));
            s = new(); s.StopKey = "F8"; Throws<FormatException>(() => KeyboardLayout.Validate(s));
            s = new(); s.KeyMap.Remove(52); Throws<FormatException>(() => KeyboardLayout.Validate(s));
            s = new(); s.Countdown = double.NaN; Throws<FormatException>(() => KeyboardLayout.Validate(s));
        });
        Test("game window detection ignores browser video titles", () =>
        {
            var browser = new WindowChoice(1, 1, "【异环】视频 - Google Chrome", "chrome");
            var game = new WindowChoice(2, 2, "异环  ", "HTGame");
            Check(Native.SelectGameWindow([browser, game]) == game, "game selected");
            Check(Native.SelectGameWindow([browser]) == null, "no fuzzy browser match");
        });
        Test("UIPI preflight blocks ordinary tool targeting elevated game", () =>
        {
            var blocked = new InputAccessReport(new(0x2000), new(0x3000));
            Check(blocked.Blocked && blocked.CanRestartAsAdmin && blocked.Message.Contains("管理员重启"), "actionable diagnosis");
            Throws<InvalidOperationException>(blocked.RequireCompatible);
            new InputAccessReport(new(0x3000), new(0x3000)).RequireCompatible();
            new InputAccessReport(new(0x3000), new(0x2000)).RequireCompatible();
            var unknown = new InputAccessReport(new(0x2000), new(null, 5));
            Check(!unknown.Known && !unknown.Message.Contains("权限匹配"), "unknown does not imply compatible");
            Check(!new InputAccessReport(new(0x3000), new(0x4000)).CanRestartAsAdmin, "admin restart cannot solve system-level target");
        });
        Test("process integrity query works on current process and handles missing PID", () =>
        {
            var current = InputAccess.Inspect(Environment.ProcessId);
            Check(current.Level.HasValue && current.Level.Value >= 0x1000, "real token queried");
            Check(InputAccess.Check((uint)Environment.ProcessId).Known && !InputAccess.Check((uint)Environment.ProcessId).Blocked, "self compatible");
            Check(!InputAccess.Inspect(int.MaxValue).Level.HasValue, "missing process returns unknown");
        });
        Test("admin restart preserves data path and song without shell text", () =>
        {
            var info = ElevatedRestart.CreateStartInfo(@"D:\音乐 工具\GuitarNTE.exe", @"D:\私人曲库\文件 & 草稿", "abc123", 123);
            Check(info.UseShellExecute && info.Verb == "runas", "uses Windows UAC");
            Check(info.ArgumentList.SequenceEqual(new[] { "--data-directory", @"D:\私人曲库\文件 & 草稿", "--wait-for-exit", "123", "--select-song", "abc123" }), "structured arguments retain exact paths");
        });
        Test("note names chords rest fraction and flats", () =>
        {
            var s = TextScoreParser.Parse("E4:1 F#4:0.5 [G4 B4]:2 R:1 Bb3:0.5", Notation.NoteNames, 120);
            Check(s.Notes.Count == 5, "note count"); Near(2.5, s.Duration); Near(0.75, s.Notes[2].Start); Near(0.75, s.Notes[3].Start); Check(s.Notes[4].Pitch == 58, "flat");
        });
        Test("numbered scale tonic octave accidental comments", () =>
        {
            var s = TextScoreParser.Parse("// intro\n1 2 #4 b7 1' 5, 0:2 | [1 3 5]:0.5", Notation.Numbered, 60, "D4");
            int[] expected = [62, 64, 68, 72, 74, 57, 62, 66, 69]; Check(s.Notes.Select(n => n.Pitch).SequenceEqual(expected), "pitches"); Near(8.5, s.Duration);
        });
        Test("parser gives line and column and rejects invalid inputs", () =>
        {
            try { TextScoreParser.Parse("E4\n  BAD", Notation.NoteNames, 120); throw new Exception("accepted invalid token"); }
            catch (ScoreFormatException e) { Check(e.Line == 2 && e.Column == 3, "location"); }
            foreach (string invalid in new[] { "[E4 G4", "[]", "E4:0", "E4:-2", "E4:NaN", "R R", "E4:0.5Q", "[E4]E4" }) Throws<FormatException>(() => TextScoreParser.Parse(invalid, Notation.NoteNames, 120));
            Throws<FormatException>(() => TextScoreParser.Parse("E4", Notation.NoteNames, double.NaN));
        });
        Test("transpose first then fold and deduplicate", () =>
        {
            var s = new ScoreData([new(28, 0, 1), new(40, 0, 2), new(100, 1, 1)], 2);
            var c = ScoreCompiler.Compile(s, 1, 2); Check(c.Adjusted == 3 && c.Merged == 1, "counts");
            Check(c.Notes[0].Pitch == 53 && c.Notes[1].Pitch == 77, "fold"); Near(1, c.Notes[0].Duration); Near(1, c.Duration);
        });
        Test("MIDI tracks global tempo changes trailing rest and percussion", () =>
        {
            var bytes = Fixture(); var info = MidiImporter.Inspect(bytes); Check(info.Count == 4 && !info[3].HasMelody && info[1].Name == "中文旋律", "tracks");
            var s = MidiImporter.Import(bytes, [1, 2, 3]); Check(s.Notes.Count == 3 && s.Notes.All(n => n.Pitch != 36), "percussion excluded");
            var e = s.Notes.Single(n => n.Pitch == 64); var g = s.Notes.Single(n => n.Pitch == 67); Near(0.5, e.Duration); Near(0.5, g.Start); Near(1, g.Duration); Near(2.5, s.Duration);
            Throws<FormatException>(() => MidiImporter.Import(bytes, [])); Throws<FormatException>(() => MidiImporter.Import(bytes, [3])); Throws<FormatException>(() => MidiImporter.Import(bytes, [99]));
            Throws<FormatException>(() => MidiImporter.Inspect([])); Throws<FormatException>(() => MidiImporter.Inspect([0, 1, 2, 3]));
            if (args.Contains("--fixtures")) { Directory.CreateDirectory("artifacts/fixtures/中文路径"); File.WriteAllBytes("artifacts/fixtures/中文路径/测试变速曲谱.mid", bytes); }
        });
        Test("folder MIDI import recurses, isolates failures and preserves source files", () =>
        {
            string root = Path.Combine(Path.GetTempPath(), "GuitarNTE-folder-" + Guid.NewGuid().ToString("N"));
            string? oldData = Environment.GetEnvironmentVariable("GUITAR_NTE_DATA");
            try
            {
                string source = Path.Combine(root, "中文 MIDI"), nested = Path.Combine(source, "子目录");
                Directory.CreateDirectory(nested);
                byte[] bytes = Fixture();
                File.WriteAllBytes(Path.Combine(source, "同名.MID"), bytes);
                File.WriteAllBytes(Path.Combine(nested, "同名.MiDi"), Fixture(65));
                File.WriteAllBytes(Path.Combine(source, "重复改名.mid"), bytes);
                File.WriteAllBytes(Path.Combine(source, "损坏.mid"), [1, 2, 3]);
                File.WriteAllBytes(Path.Combine(source, "空.mid"), []);
                File.WriteAllBytes(Path.Combine(source, "忽略.mid.bak"), bytes);
                using (var big = File.Create(Path.Combine(source, "超大.mid"))) big.SetLength(32 * 1024 * 1024 + 1);
                var hashes = Directory.GetFiles(source, "*", SearchOption.AllDirectories).ToDictionary(p => p,
                    p => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(p))));
                Environment.SetEnvironmentVariable("GUITAR_NTE_DATA", Path.Combine(root, "library"));
                var store = new LibraryStore(); var updates = new List<MidiImportProgress>();
                var result = MidiLibraryImport.ImportFolder(source, store.SaveSong, new InlineProgress<MidiImportProgress>(updates.Add));
                Check(result.FileCount == 6 && result.Songs.Count == 2 && result.DuplicateCount == 1 && result.Failures.Count == 3, "supported extensions, recursion, duplicate detection and isolated invalid files");
                Check(result.Songs.Select(s => s.Id).Distinct().Count() == 2 && result.Songs.All(s => s.Title == "同名" && s.SelectedTracks.SequenceEqual([1, 2])), "same names stay independent with melodic tracks selected");
                Check(store.LoadSongs().Count == 2 && store.Warnings.Count == 0, "all successful copies survive reload");
                foreach (var song in result.Songs) Check(MidiImporter.Import(Convert.FromBase64String(song.MidiBase64!), song.SelectedTracks).Notes.Count == 3, "saved MIDI playable");
                Check(updates.Count == 6 && updates[^1].Completed == 6 && updates.All(p => p.Total == 6), "per-file progress includes duplicates");
                var repeated = MidiLibraryImport.ImportFolder(source, store.SaveSong, existingSongs: store.LoadSongs());
                Check(repeated.Songs.Count == 0 && repeated.DuplicateCount == 3 && repeated.Failures.Count == 3 && store.LoadSongs().Count == 2, "repeat import skips all known MIDI");
                Check(hashes.All(p => File.Exists(p.Key) && p.Value == Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(p.Key)))), "source paths and bytes unchanged");
                var failedSave = MidiLibraryImport.ImportFolder(nested, _ => throw new IOException("write failed"));
                Check(failedSave.Songs.Count == 0 && failedSave.Failures.Single().Reason == "write failed", "save failure never counted as success");
                var empty = Path.Combine(root, "empty"); Directory.CreateDirectory(empty);
                Check(MidiLibraryImport.ImportFolder(empty, store.SaveSong).FileCount == 0, "empty folder");
                Check(MidiLibraryImport.ImportFolder(Path.Combine(root, "missing"), store.SaveSong).Failures.Count == 1, "unreadable folder reported");
                string longName = Path.Combine(source, new string('曲', 105) + ".mid"); File.WriteAllBytes(longName, bytes);
                Check(MidiLibraryImport.ReadSong(longName).Title.Length == 100, "long filenames stay valid for library reload");
            }
            finally
            {
                Environment.SetEnvironmentVariable("GUITAR_NTE_DATA", oldData);
                Directory.Delete(root, true);
            }
        });
        Test("duplicate cleanup archives exact MIDI copies and preserves playback variants", () =>
        {
            string root = Path.Combine(Path.GetTempPath(), "GuitarNTE-dedup-" + Guid.NewGuid().ToString("N"));
            string? oldData = Environment.GetEnvironmentVariable("GUITAR_NTE_DATA");
            try
            {
                Environment.SetEnvironmentVariable("GUITAR_NTE_DATA", root); var store = new LibraryStore();
                var original = new SongProject { Title = "原曲", Mode = Notation.Midi, MidiBase64 = Convert.ToBase64String(Fixture()), SelectedTracks = [1, 2], Text = "" };
                var duplicate = LibraryStore.Copy(original); duplicate.Id = Guid.NewGuid().ToString("N"); duplicate.Title = "改名后的重复曲"; duplicate.SelectedTracks = [2, 1];
                var speed = LibraryStore.Copy(original); speed.Id = Guid.NewGuid().ToString("N"); speed.Speed = 0.75;
                var transpose = LibraryStore.Copy(original); transpose.Id = Guid.NewGuid().ToString("N"); transpose.Transpose = 12;
                var tracks = LibraryStore.Copy(original); tracks.Id = Guid.NewGuid().ToString("N"); tracks.SelectedTracks = [1];
                var text = new SongProject { Title = "文本草稿" };
                foreach (var song in new[] { original, duplicate, speed, transpose, tracks, text }) store.SaveSong(song);
                File.SetLastWriteTimeUtc(Path.Combine(root, "Songs", original.Id + ".json"), DateTime.UtcNow.AddHours(-1));
                var loaded = store.LoadSongs();
                Check(store.CleanDuplicateSongs(loaded) == 1 && loaded.Count == 5 && loaded.Any(s => s.Id == duplicate.Id), "newest title retained and playback variants preserved");
                var archive = Directory.GetFiles(Path.Combine(root, "DuplicateBackups"), "*.json", SearchOption.AllDirectories).Single();
                Check(Path.GetFileNameWithoutExtension(archive) == original.Id && System.Text.Json.JsonSerializer.Deserialize<SongProject>(File.ReadAllText(archive))!.MidiBase64 == original.MidiBase64, "full old project recoverable from backup");
                Check(store.LoadSongs().Count == 5 && store.CleanDuplicateSongs(loaded) == 0, "cleanup persists and is idempotent");
                store.SaveSong(original); loaded = store.LoadSongs();
                Check(store.CleanDuplicateSongs(loaded, original.Id) == 1 && loaded.Any(s => s.Id == original.Id), "startup selected song preferred when duplicates exist");
            }
            finally { Environment.SetEnvironmentVariable("GUITAR_NTE_DATA", oldData); Directory.Delete(root, true); }
        });
        Test("scheduler batches chords and releases repeats", () =>
        {
            var clock = new FakeClock(); var output = new FakeOutput(); using var engine = new PlaybackEngine(output, clock, false);
            engine.Start(Compile("[E4 G4] E4"), 0, 40, 1, false); engine.Tick(); Check(output.Events[0] == "down:64,67", "batch");
            clock.Now = 0.04; engine.Tick(); Check(output.Events.Contains("up:64,67"), "release");
            clock.Now = 0.5; engine.Tick(); Check(output.Events.Last() == "down:64", "repeat"); clock.Now = 1.1; engine.Tick(); Check(engine.Status.State == PlaybackState.Completed && output.Held.Count == 0, "complete cleanup");
        });
        Test("countdown cancel and foreground guard", () =>
        {
            var clock = new FakeClock(); var output = new FakeOutput { IsTargetForeground = false }; using var e = new PlaybackEngine(output, clock, false);
            e.Start(Compile("E4"), 5, 40, 1, false); clock.Now = 4; e.Tick(); Check(output.Events.Count == 0, "early press");
            e.Stop(); clock.Now = 6; e.Tick(); Check(output.Events.Count == 0 && e.Status.State == PlaybackState.Idle, "cancel");
            e.Start(Compile("E4"), 0, 40, 1, false); e.Tick(); Check(e.Status.State == PlaybackState.Paused && output.Events.Count == 0, "focus guard");
        });
        Test("focus loss releases keys resume countdown preserves next note", () =>
        {
            var clock = new FakeClock(); var output = new FakeOutput(); using var e = new PlaybackEngine(output, clock, false);
            e.Start(Compile("E4 G4"), 1, 40, 1, false); clock.Now = 1; e.Tick(); Check(output.Held.Count == 1, "first press");
            clock.Now = 1.01; output.IsTargetForeground = false; e.Tick(); Check(e.Status.State == PlaybackState.Paused && output.Held.Count == 0, "loss cleanup");
            output.IsTargetForeground = true; e.Resume(); clock.Now = 2.01; e.Tick(); clock.Now = 2.51; e.Tick(); Check(output.Events.Count(x => x == "down:64") == 1 && output.Events.Contains("down:67"), "resume no replay");
        });
        Test("lag pauses without burst", () =>
        {
            var clock = new FakeClock(); var output = new FakeOutput(); using var e = new PlaybackEngine(output, clock, false);
            e.Start(Compile("E4 G4 B4"), 0, 40, 1, false); e.Tick(); clock.Now = 1.3; e.Tick(); Check(e.Status.State == PlaybackState.Paused && output.Events.Count(x => x.StartsWith("down")) == 1, "no burst");
            e.Resume(); e.Tick(); Check(output.Events.Last() == "down:67", "next note not skipped");
        });
        Test("preview never calls real keyboard even if no foreground", () =>
        {
            var clock = new FakeClock(); var output = new FakeOutput { IsTargetForeground = false }; using var e = new PlaybackEngine(output, clock, false);
            e.Start(Compile("[E4 G4]"), 0, 40, 1, true); e.Tick(); Check(e.Status.ActivePitches.Length == 2, "visual notes"); clock.Now = 1; e.Tick(); Check(e.Status.State == PlaybackState.Completed && output.Events.Count == 0, "no injection");
        });
        Test("loops and output failures and dispose cleanup", () =>
        {
            var clock = new FakeClock(); var output = new FakeOutput(); var e = new PlaybackEngine(output, clock, false);
            e.Start(Compile("E4"), 0, 40, 2, false); e.Tick(); clock.Now = 0.5; e.Tick(); e.Tick(); Check(e.Status.Loop == 2 && output.Events.Count(x => x == "down:64") == 2, "repeat count");
            e.Dispose(); Check(output.Held.Count == 0, "dispose");
            output = new FakeOutput { ThrowDown = true }; using var bad = new PlaybackEngine(output, clock, false); bad.Start(Compile("E4"), 0, 40, 1, false); bad.Tick(); Check(bad.Status.State == PlaybackState.Faulted && output.Held.Count == 0, "fault cleanup");
        });
        Test("five minute simulated timeline has no accumulated drift", () =>
        {
            var clock = new FakeClock(); var output = new FakeOutput(); using var e = new PlaybackEngine(output, clock, false);
            var notes = Enumerable.Range(0, 600).Select(i => new NoteEvent(64, i * 0.5, 0.5)).ToArray(); e.Start(new(notes, 300, 0, 0), 0, 40, 1, false);
            for (int tick = 0; tick <= 150000; tick++) { clock.Now = tick * 0.002; e.Tick(); }
            Check(e.Status.State == PlaybackState.Completed && output.Events.Count(x => x == "down:64") == 600, "all notes on timeline"); Near(300, e.Status.Position);
        });
        Test("sustain follows each note duration and leaves rests silent", () =>
        {
            var clock = new FakeClock(); var output = new FakeOutput(); using var e = new PlaybackEngine(output, clock, false);
            e.Start(Compile("E4:2 R:1 G4:1"), 0, 40, 1, false, true); e.Tick();
            clock.Now = 0.1; e.Tick(); Check(output.Held.SetEquals([64]), "long note held beyond 40 ms");
            clock.Now = 0.99; e.Tick(); Check(output.Held.Contains(64), "full duration");
            clock.Now = 1; e.Tick(); Check(output.Held.Count == 0, "release at rest");
            clock.Now = 1.5; e.Tick(); Check(output.Held.SetEquals([67]), "next onset after rest");
            e.Pause(); Check(output.Held.Count == 0, "pause releases sustained note");
        });
        Test("sustain independent chord endings and same-pitch reattack", () =>
        {
            var clock = new FakeClock(); var output = new FakeOutput(); using var e = new PlaybackEngine(output, clock, false);
            e.Start(new([new(64, 0, 2), new(67, 0, 0.5), new(64, 1, 1)], 2, 0, 0), 0, 40, 1, false, true); e.Tick();
            clock.Now = 0.5; e.Tick(); Check(output.Held.SetEquals([64]), "independent note endings");
            clock.Now = 0.997; e.Tick(); Check(output.Held.Count == 0, "release before repeated same pitch");
            clock.Now = 1; e.Tick(); Check(output.Events.Last() == "down:64", "reattack");
            clock.Now = 2; e.Tick(); Check(e.Status.State == PlaybackState.Completed && output.Held.Count == 0, "sustain complete cleanup");
        });
        Test("storage Chinese path round trip copy delete source unchanged", () =>
        {
            string root = Path.Combine(Path.GetTempPath(), "GuitarNTE-tests-" + Guid.NewGuid().ToString("N"), "中文曲库");
            Environment.SetEnvironmentVariable("GUITAR_NTE_DATA", root);
            var store = new LibraryStore(); var song = new SongProject { Title = "测试歌曲", Mode = Notation.Midi, MidiBase64 = Convert.ToBase64String(Fixture()), SelectedTracks = [1] };
            store.SaveSong(song); var settings = new AppSettings { Countdown = 3 }; store.SaveSettings(settings);
            var loaded = store.LoadSongs().Single(); Check(loaded.Title == song.Title && loaded.MidiBase64 == song.MidiBase64 && loaded.SelectedTracks.SequenceEqual([1]), "round trip"); Near(3, store.LoadSettings().Countdown);
            var copy = LibraryStore.Copy(loaded); copy.Id = Guid.NewGuid().ToString("N"); copy.Title += "副本"; store.SaveSong(copy); store.DeleteSong(copy); Check(store.LoadSongs().Single().Id == song.Id, "delete only copy");
            File.WriteAllText(Path.Combine(root, "Songs", "corrupt.json"), "{"); Check(store.LoadSongs().Count == 1 && store.Warnings.Count == 1, "corrupt preserves good songs");
            File.WriteAllText(Path.Combine(root, "settings.json"), "{\"Version\":99}"); Check(store.LoadSettings().Countdown == 5 && File.Exists(Path.Combine(root, "settings.json")), "bad settings preserved");
            Throws<FormatException>(() => store.SaveSong(new SongProject { Id = "../bad" }));
            Environment.SetEnvironmentVariable("GUITAR_NTE_DATA", null);
        });
        if (args.Contains("--realtime")) Test("three minute real worker timing", Realtime);
        if (args.Contains("--native")) Test("native SendInput 36 keys and game focus loss", NativeTest);
        Console.WriteLine($"RESULT {passed} passed, {failed} failed"); return failed == 0 ? 0 : 1;
    }
    static void Realtime()
    {
        var output = new TimingOutput(); using var e = new PlaybackEngine(output); var notes = Enumerable.Range(0, 360).Select(i => new NoteEvent(64, i * 0.5, 0.5)).ToArray();
        output.Watch.Start(); e.Start(new(notes, 180, 0, 0), 0, 40, 1, false);
        while (e.Status.State is PlaybackState.Playing or PlaybackState.Countdown) Thread.Sleep(50);
        Check(e.Status.State == PlaybackState.Completed && output.Times.Count == 360, e.Status.Message);
        double baseline = output.Times[0]; var errors = output.Times.Select((time, i) => Math.Abs(time - baseline - i * 0.5)).ToArray();
        Console.WriteLine($"REALTIME notes={output.Times.Count} max_error_ms={errors.Max() * 1000:0.00} last_error_ms={errors.Last() * 1000:0.00}"); Check(errors.Max() < 0.15, "timing >150ms");
    }
    static int DiagnoseInput()
    {
        var game = Native.SelectGameWindow(Native.Windows());
        if (game == null) { Console.WriteLine("No game window found; no input sent."); return 2; }
        var access = InputAccess.Check(game.ProcessId);
        var report = new { game.ProcessId, game.ProcessName, game.Title, SenderIntegrity = access.Sender.Level, SenderLabel = access.Sender.Label, TargetIntegrity = access.Target.Level, TargetLabel = access.Target.Label, access.Blocked, access.Message, InputStructSize = Marshal.SizeOf<Native.INPUT>(), ExpectedInputStructSize = IntPtr.Size == 8 ? 40 : 28, InputSent = false };
        string json = System.Text.Json.JsonSerializer.Serialize(report, new System.Text.Json.JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        Directory.CreateDirectory("artifacts"); File.WriteAllText("artifacts/input-access-diagnosis.json", json); Console.WriteLine(json); return 0;
    }
    static int ValidateProject(string path)
    {
        Test("generated polyphonic project imports and sustains end-to-end", () =>
        {
            var project = System.Text.Json.JsonSerializer.Deserialize<SongProject>(File.ReadAllText(path))!;
            byte[] midiBytes = Convert.FromBase64String(project.MidiBase64!);
            var info = MidiImporter.Inspect(midiBytes);
            var compiled = ScoreCompiler.Compile(MidiImporter.Import(midiBytes, project.SelectedTracks), project.Transpose, project.Speed);
            Check(compiled.Notes.Count > 0 && compiled.Notes.All(n => n.Pitch is >= 52 and <= 87 && n.Duration > 0), "all pitches and durations valid");
            var clock = new FakeClock(); var output = new FakeOutput(); using var engine = new PlaybackEngine(output, clock, false);
            engine.Start(compiled, 0, 40, 1, false, true);
            var timeline = new SortedSet<double> { 0, compiled.Duration + 0.51 };
            foreach (var note in compiled.Notes) { timeline.Add(note.Start); timeline.Add(note.Start + note.Duration); }
            // Advance in 2 ms ticks so the same focus/lag/release logic as production runs.
            for (int i = 0; i < (compiled.Duration + 0.51) * 500; i++) { clock.Now = i / 500d; engine.Tick(); }
            Check(engine.Status.State == PlaybackState.Completed && output.Held.Count == 0, engine.Status.Message);
            int downs = output.Events.Where(s => s.StartsWith("down:")).Sum(s => s[5..].Split(',').Length);
            int ups = output.Events.Where(s => s.StartsWith("up:")).Sum(s => s[3..].Split(',').Length);
            Check(downs == compiled.Notes.Count && ups == downs, $"note down/up count: {downs}/{ups}/{compiled.Notes.Count}");
            Console.WriteLine($"PROJECT title={project.Title} notes={compiled.Notes.Count} duration={compiled.Duration:0.000}s adjusted={compiled.Adjusted} selectedTracks={string.Join(',', project.SelectedTracks)} balancedKeyEvents={downs}");
            foreach (var t in info) Console.WriteLine($"TRACK {t.Index}: {t.Name} ({t.NoteCount} notes)");
        });
        return failed == 0 ? 0 : 1;
    }
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(nint hwnd);
    static void NativeTest()
    {
        string log = Path.Combine(Path.GetTempPath(), "GuitarNTE-native-" + Guid.NewGuid().ToString("N") + ".txt");
        using var proc = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, $"--receiver \"{log}\"") { UseShellExecute = false })!;
        try
        {
            nint handle = 0;
            for (int n = 0; n < 100; n++)
            {
                Thread.Sleep(50);
                if (File.Exists(log)) { var ready = File.ReadAllLines(log).FirstOrDefault(l => l.StartsWith("READY ")); if (ready != null) { handle = (nint)long.Parse(ready[6..]); break; } }
            }
            Check(handle != 0, "receiver window"); SetForegroundWindow(handle); Thread.Sleep(200);
            var output = new WindowsKeyboardOutput { Target = new(handle, (uint)proc.Id, "test") };
            Check(output.IsTargetForeground, "cannot focus receiver");
            using var engine = new PlaybackEngine(output);
            var notes = Enumerable.Range(52, 36).Select((p, i) => new NoteEvent(p, i * 0.09, 0.09)).ToArray(); engine.Start(new(notes, 3.3, 0, 0), 0, 20, 1, false);
            while (engine.Status.State is PlaybackState.Playing or PlaybackState.Countdown) Thread.Sleep(10);
            Check(engine.Status.State == PlaybackState.Completed, engine.Status.Message); Thread.Sleep(100);
            var lines = File.ReadAllLines(log); Check(lines.Count(l => l.StartsWith("DOWN")) == 36 && lines.Count(l => l.StartsWith("UP")) == 36, "36 native down/up events");
            Console.WriteLine("NATIVE_LOG " + log);
        }
        finally { if (!proc.HasExited) proc.Kill(); }
    }
}
internal sealed class FakeClock : IPlaybackClock { public double Now { get; set; } }
internal sealed class PlannedPianoOutput(Action<IReadOnlyList<KeyStroke>> send) : IKeyboardOutput
{
    readonly KeyboardSession session = new(send);
    readonly Dictionary<int, string> map = KeyboardLayout.DefaultMap(InstrumentMode.Piano);
    public bool IsTargetForeground => true;
    public void KeyDown(IReadOnlyList<int> pitches) => session.KeyDown(pitches, map, InstrumentMode.Piano);
    public void KeyUp(IReadOnlyList<int> pitches) => session.KeyUp(pitches);
    public void ReleaseAll() => session.ReleaseAll();
}
internal sealed class FakeOutput : IKeyboardOutput
{
    public bool IsTargetForeground { get; set; } = true;
    public bool ThrowDown { get; set; }
    public List<string> Events { get; } = [];
    public HashSet<int> Held { get; } = [];
    public void KeyDown(IReadOnlyList<int> p) { foreach (int x in p) Held.Add(x); if (ThrowDown) throw new InvalidOperationException("test input failure"); Events.Add("down:" + string.Join(',', p)); }
    public void KeyUp(IReadOnlyList<int> p) { Events.Add("up:" + string.Join(',', p)); foreach (int x in p) Held.Remove(x); }
    public void ReleaseAll() { if (Held.Count > 0) KeyUp(Held.ToArray()); }
}
internal sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}
internal sealed class TimingOutput : IKeyboardOutput
{
    public bool IsTargetForeground => true;
    public Stopwatch Watch { get; } = new();
    public List<double> Times { get; } = [];
    public void KeyDown(IReadOnlyList<int> pitches) => Times.Add(Watch.Elapsed.TotalSeconds);
    public void KeyUp(IReadOnlyList<int> pitches) { }
    public void ReleaseAll() { }
}
internal static class Receiver
{
    internal static void Run(string[] args)
    {
        string log = args[Array.IndexOf(args, "--receiver") + 1]; File.WriteAllText(log, "");
        var app = new System.Windows.Application(); var window = new System.Windows.Window { Title = "Guitar NTE · 本地按键验证接收器", Width = 520, Height = 240, WindowStartupLocation = System.Windows.WindowStartupLocation.CenterScreen, Content = new System.Windows.Controls.TextBlock { Text = "正在验证 36 个按键，不会向游戏发送输入。", Margin = new(24) } };
        window.PreviewKeyDown += (_, e) => { File.AppendAllText(log, "DOWN " + e.Key + "\n"); e.Handled = true; };
        window.PreviewKeyUp += (_, e) => { File.AppendAllText(log, "UP " + e.Key + "\n"); e.Handled = true; };
        window.Loaded += (_, _) => File.AppendAllText(log, "READY " + new System.Windows.Interop.WindowInteropHelper(window).Handle + "\n");
        app.Run(window);
    }
}
