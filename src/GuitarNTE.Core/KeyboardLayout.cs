namespace GuitarNTE.Core;

public record KeyDefinition(string Name, ushort VirtualKey, ushort ScanCode, bool Extended = false);
public record KeyBinding(KeyDefinition Key, KeyDefinition? Modifier)
{
    public string Name => Modifier == null ? Key.Name : $"{Modifier.Name}+{Key.Name}";
}
public static class KeyboardLayout
{
    public static int MinimumPitch(InstrumentMode instrument) => instrument switch
    {
        InstrumentMode.Guitar => 52, InstrumentMode.Piano => 48,
        _ => throw new FormatException("乐器模式无效。")
    };
    public static KeyBinding Binding(string name, InstrumentMode instrument)
    {
        var parts = name.Split('+', StringSplitOptions.TrimEntries);
        if (parts.Length == 1 && Keys.TryGetValue(parts[0], out var single)) return new(single, null);
        if (instrument == InstrumentMode.Piano && parts.Length == 2 && Keys.TryGetValue(parts[1], out var key))
        {
            if (parts[0].Equals("Shift", StringComparison.OrdinalIgnoreCase)) return new(key, new("Shift", 0xA0, 42));
            if (parts[0].Equals("Ctrl", StringComparison.OrdinalIgnoreCase)) return new(key, new("Ctrl", 0xA2, 29));
        }
        throw new FormatException($"按键 {name} 无效。钢琴半音使用 Shift+字母 或 Ctrl+字母；吉他使用单键。");
    }
    public static readonly IReadOnlyDictionary<string, KeyDefinition> Keys = BuildKeys();
    static Dictionary<string, KeyDefinition> BuildKeys()
    {
        var d = new Dictionary<string, KeyDefinition>(StringComparer.OrdinalIgnoreCase);
        void Add(string name, int vk, int scan, bool extended = false) => d[name] = new(name, (ushort)vk, (ushort)scan, extended);
        string[] rows = ["1234567890", "QWERTYUIOP", "ASDFGHJKL", "ZXCVBNM"];
        int[] starts = [2, 16, 30, 44];
        for (int r = 0; r < rows.Length; r++) for (int j = 0; j < rows[r].Length; j++) Add(rows[r][j].ToString(), rows[r][j], starts[r] + j);
        Add("-", 0xBD, 12); Add("=", 0xBB, 13); Add("[", 0xDB, 26); Add("]", 0xDD, 27);
        Add(";", 0xBA, 39); Add("'", 0xDE, 40); Add("Enter", 13, 28); Add(",", 0xBC, 51); Add(".", 0xBE, 52); Add("/", 0xBF, 53);
        Add("Space", 32, 57); Add("Tab", 9, 15); Add("`", 0xC0, 41); Add("\\", 0xDC, 43);
        for (int i = 1; i <= 12; i++) Add($"F{i}", 111 + i, i <= 10 ? 58 + i : 76 + i);
        return d;
    }
    public static Dictionary<int, string> DefaultMap(InstrumentMode instrument = InstrumentMode.Guitar)
    {
        if (instrument == InstrumentMode.Piano)
        {
            string[] rows = ["ZXCVBNM", "ASDFGHJ", "QWERTYU"];
            var map = new Dictionary<int, string>();
            for (int r = 0; r < 3; r++)
            {
                var k = rows[r];
                string[] bindings = [$"{k[0]}", $"Shift+{k[0]}", $"{k[1]}", $"Ctrl+{k[2]}", $"{k[2]}", $"{k[3]}", $"Shift+{k[3]}", $"{k[4]}", $"Shift+{k[4]}", $"{k[5]}", $"Ctrl+{k[6]}", $"{k[6]}"];
                for (int i = 0; i < 12; i++) map[48 + r * 12 + i] = bindings[i];
            }
            return map;
        }
        _ = MinimumPitch(instrument);
        string[] keys = ["A", "S", "D", "F", "G", "H", "J", "K", "L", ";", "'", "Enter", "Q", "W", "E", "R", "T", "Y", "U", "I", "O", "P", "[", "]", "1", "2", "3", "4", "5", "6", "7", "8", "9", "0", "-", "="];
        return keys.Select((key, i) => (key, pitch: 52 + i)).ToDictionary(x => x.pitch, x => x.key);
    }
    public static void Validate(AppSettings settings)
    {
        _ = MinimumPitch(settings.Instrument);
        ValidateMap(settings.KeyMap, InstrumentMode.Guitar);
        ValidateMap(settings.PianoKeyMap, InstrumentMode.Piano);
        var used = settings.ActiveKeyMap.Values.Select(k => Binding(k, settings.Instrument).Key.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!Keys.ContainsKey(settings.ToggleKey) || !Keys.ContainsKey(settings.StopKey)) throw new FormatException("热键无效，请使用支持的单键名称。");
        if (settings.ToggleKey.Equals(settings.StopKey, StringComparison.OrdinalIgnoreCase) || used.Contains(settings.ToggleKey) || used.Contains(settings.StopKey)) throw new FormatException("两个热键必须不同，且不能与演奏键重叠（包括组合键的基础按键）。");
        if (!double.IsFinite(settings.Countdown) || settings.Countdown is < 1 or > 30 || settings.PressMilliseconds is < 10 or > 500 || settings.Loops is < 1 or > 100) throw new FormatException("倒计时 1–30 秒，按键 10–500 毫秒，循环 1–100 次。");
    }
    static void ValidateMap(Dictionary<int, string> map, InstrumentMode instrument)
    {
        int min = MinimumPitch(instrument);
        if (map == null || map.Count != 36 || Enumerable.Range(min, 36).Any(p => !map.ContainsKey(p))) throw new FormatException($"键位映射必须包含 {Pitches.Name(min)}–{Pitches.Name(min + 35)} 的全部 36 个音。");
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in map.Values)
            if (string.IsNullOrWhiteSpace(key) || !used.Add(Binding(key, instrument).Name)) throw new FormatException($"按键 {key} 无效或重复。");
    }
}
