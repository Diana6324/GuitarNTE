using System.Globalization;
using System.Text.RegularExpressions;

namespace GuitarNTE.Core;

public record NoteEvent(int Pitch, double Start, double Duration);
public record ScoreData(IReadOnlyList<NoteEvent> Notes, double Duration);
public record CompiledScore(IReadOnlyList<NoteEvent> Notes, double Duration, int Adjusted, int Merged);
public enum Notation { Numbered, NoteNames, Midi }
public enum InstrumentMode { Guitar, Piano }
public sealed class SongProject
{
    public int Version { get; set; } = 1;
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = UiText.T("新曲谱", "New score");
    public Notation Mode { get; set; }
    public string Text { get; set; } = "// 两只老虎 · 1=C4\n1 2 3 1 | 1 2 3 1\n3 4 5:2 | 3 4 5:2\n5:0.5 6:0.5 5:0.5 4:0.5 3 1\n5:0.5 6:0.5 5:0.5 4:0.5 3 1\n1 5, 1:2 | 1 5, 1:2";
    public string Tonic { get; set; } = "C4";
    public double Bpm { get; set; } = 120;
    public double Speed { get; set; } = 1;
    public int Transpose { get; set; }
    public string? MidiBase64 { get; set; }
    public List<int> SelectedTracks { get; set; } = [];
}
public sealed class AppSettings
{
    public int Version { get; set; } = 1;
    // Empty means follow Windows UI language; legacy settings retain all other values.
    public string Language { get; set; } = "";
    public double Countdown { get; set; } = 5;
    public int PressMilliseconds { get; set; } = 40;
    public bool SustainNotes { get; set; } = true;
    public int Loops { get; set; } = 1;
    public string ToggleKey { get; set; } = "F8";
    public string StopKey { get; set; } = "F9";
    public Dictionary<int, string> KeyMap { get; set; } = KeyboardLayout.DefaultMap();
    public InstrumentMode Instrument { get; set; }
    public Dictionary<int, string> PianoKeyMap { get; set; } = KeyboardLayout.DefaultMap(InstrumentMode.Piano);
    [System.Text.Json.Serialization.JsonIgnore]
    public Dictionary<int, string> ActiveKeyMap => Instrument == InstrumentMode.Piano ? PianoKeyMap : KeyMap;
}
public static class Pitches
{
    static readonly string[] Names = ["C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"];
    public static string Name(int pitch) => Names[((pitch % 12) + 12) % 12] + (pitch / 12 - 1);
    public static int Parse(string text)
    {
        var m = Regex.Match(text, @"^([A-Ga-g])([#b]?)(-?\d+)$");
        if (!m.Success) throw new FormatException(UiText.T("音名应为 C4、F#4 或 Bb3。", "Use note names such as C4, F#4 or Bb3."));
        int semitone = char.ToUpperInvariant(m.Groups[1].Value[0]) switch { 'C' => 0, 'D' => 2, 'E' => 4, 'F' => 5, 'G' => 7, 'A' => 9, _ => 11 };
        int octave = int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
        if (octave is < -1 or > 9) throw new FormatException(UiText.T("八度范围为 -1 到 9。", "Octave must be between -1 and 9."));
        return (octave + 1) * 12 + semitone + (m.Groups[2].Value switch { "#" => 1, "b" => -1, _ => 0 });
    }
}
public sealed class ScoreFormatException(string message, int line, int column) : FormatException(UiText.T($"第 {line} 行，第 {column} 列：{message}", $"Line {line}, column {column}: {message}"))
{
    public int Line { get; } = line;
    public int Column { get; } = column;
}
public static class TextScoreParser
{
    public static ScoreData Parse(string text, Notation mode, double bpm, string tonic = "C4")
    {
        if (!double.IsFinite(bpm) || bpm is < 20 or > 400) throw new FormatException(UiText.T("BPM 范围为 20–400。", "BPM must be between 20 and 400."));
        if (mode == Notation.Midi) throw new ArgumentException(UiText.T("请选择文本乐谱模式。", "Select a text score format."));
        int root = Pitches.Parse(tonic);
        var notes = new List<NoteEvent>();
        int i = 0; double cursor = 0;
        ScoreFormatException Error(string message, int index)
        {
            int line = 1, column = 1;
            for (int n = 0; n < index; n++) { if (text[n] == '\n') { line++; column = 1; } else column++; }
            return new(message, line, column);
        }
        int ParsePitch(string token, int at)
        {
            try
            {
                if (mode == Notation.NoteNames) return Pitches.Parse(token);
                var m = Regex.Match(token, @"^([#b]?)([1-7])([',]*)$");
                if (!m.Success) throw new FormatException(UiText.T("简谱音符应为 1–7，可加 #、b 及八度符号 ' 或 ,。", "Use numbered notes 1–7, optional # / b, and ' / , for octaves."));
                int[] scale = [0, 2, 4, 5, 7, 9, 11];
                return root + scale[m.Groups[2].Value[0] - '1'] + (m.Groups[1].Value switch { "#" => 1, "b" => -1, _ => 0 }) + 12 * (m.Groups[3].Value.Count(c => c == '\'') - m.Groups[3].Value.Count(c => c == ','));
            }
            catch (FormatException e) { throw Error(e.Message, at); }
        }
        while (i < text.Length)
        {
            if (char.IsWhiteSpace(text[i]) || text[i] == '|') { i++; continue; }
            if (text[i] == '/' && i + 1 < text.Length && text[i + 1] == '/') { while (i < text.Length && text[i] != '\n') i++; continue; }
            int start = i; var pitches = new List<int>();
            if (text[i] == '[')
            {
                i++;
                while (true)
                {
                    while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
                    if (i >= text.Length) throw Error(UiText.T("和弦缺少 ]。", "Missing ] at the end of the chord."), start);
                    if (text[i] == ']') { i++; break; }
                    int at = i;
                    while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] != ']') i++;
                    pitches.Add(ParsePitch(text[at..i], at));
                }
                if (pitches.Count == 0) throw Error(UiText.T("和弦不能为空。", "A chord cannot be empty."), start);
            }
            else
            {
                while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] != ':' && text[i] != '|') i++;
                string token = text[start..i];
                if (!(mode == Notation.Numbered ? token == "0" : token.Equals("R", StringComparison.OrdinalIgnoreCase))) pitches.Add(ParsePitch(token, start));
            }
            double beats = 1;
            if (i < text.Length && text[i] == ':')
            {
                int at = ++i;
                while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] != '|') i++;
                if (!double.TryParse(text[at..i], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out beats) || !double.IsFinite(beats) || beats is <= 0 or > 1024) throw Error(UiText.T("拍数须大于 0 且不超过 1024。", "Beat duration must be greater than 0 and no more than 1024."), at);
            }
            if (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] != '|') throw Error(UiText.T("音符之间请使用空格。", "Separate notes with spaces."), i);
            double duration = beats * 60 / bpm;
            foreach (int pitch in pitches.Distinct()) notes.Add(new(pitch, cursor, duration));
            cursor += duration;
            if (cursor > 86400 || notes.Count > 200000) throw Error(UiText.T("曲谱过长，最多支持 24 小时、20 万个音符。", "Scores support up to 24 hours and 200,000 notes."), start);
        }
        if (notes.Count == 0) throw new FormatException(UiText.T("曲谱没有可演奏音符。", "The score contains no playable notes."));
        return new(notes, cursor);
    }
}
public static class ScoreCompiler
{
    public static CompiledScore Compile(ScoreData score, int transpose = 0, double speed = 1, InstrumentMode instrument = InstrumentMode.Guitar)
    {
        if (!double.IsFinite(speed) || speed is < 0.25 or > 4) throw new FormatException(UiText.T("速度倍率范围为 0.25–4。", "Speed must be between 0.25 and 4."));
        if (transpose is < -48 or > 48) throw new FormatException(UiText.T("移调范围为 -48–48 个半音。", "Transpose must be between -48 and 48 semitones."));
        int adjusted = 0, min = KeyboardLayout.MinimumPitch(instrument), max = min + 35;
        var transformed = score.Notes.Select(n =>
        {
            int p = n.Pitch + transpose;
            if (p < min || p > max) { adjusted++; p = p < min ? p + (int)Math.Ceiling((min - p) / 12d) * 12 : p - (int)Math.Ceiling((p - max) / 12d) * 12; }
            return new NoteEvent(p, n.Start / speed, n.Duration / speed);
        }).ToList();
        var merged = transformed.GroupBy(n => (n.Pitch, n.Start)).Select(g => g.OrderByDescending(n => n.Duration).First()).OrderBy(n => n.Start).ThenBy(n => n.Pitch).ToList();
        return new(merged, score.Duration / speed, adjusted, transformed.Count - merged.Count);
    }
}
