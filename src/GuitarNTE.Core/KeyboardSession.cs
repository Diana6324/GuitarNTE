namespace GuitarNTE.Core;

public record KeyStroke(KeyDefinition Key, bool Up);

// Track physical key ownership: a stale note-off must not release a newer
// semitone played using the same letter. Modifier state is scoped to each onset.
public sealed class KeyboardSession(Action<IReadOnlyList<KeyStroke>> send)
{
    readonly Dictionary<string, (KeyDefinition Key, int Pitch)> held = [];
    readonly Dictionary<string, KeyDefinition> modifiers = [];

    public void KeyDown(IReadOnlyList<int> pitches, IReadOnlyDictionary<int, string> map, InstrumentMode instrument)
    {
        var bindings = pitches.Distinct().Select(p => (Pitch: p, Binding: KeyboardLayout.Binding(map[p], instrument))).ToArray();
        foreach (var group in bindings.GroupBy(x => x.Binding.Modifier))
        {
            var events = new List<KeyStroke>();
            foreach (var item in group)
                if (held.ContainsKey(item.Binding.Key.Name)) events.Add(new(item.Binding.Key, true));
            if (group.Key is { } modifier)
            {
                modifiers[modifier.Name] = modifier;
                events.Add(new(modifier, false));
            }
            foreach (var item in group)
            {
                held[item.Binding.Key.Name] = (item.Binding.Key, item.Pitch);
                events.Add(new(item.Binding.Key, false));
            }
            if (group.Key is { } releaseModifier) events.Add(new(releaseModifier, true));
            // Retain cleanup state if a native batch was only partially accepted.
            send(events);
            if (group.Key != null) modifiers.Remove(group.Key.Name);
        }
    }
    public void KeyUp(IReadOnlyList<int> pitches)
    {
        var notes = held.Where(x => pitches.Contains(x.Value.Pitch)).ToArray();
        if (notes.Length == 0) return;
        send(notes.Select(x => new KeyStroke(x.Value.Key, true)).ToArray());
        foreach (var note in notes) held.Remove(note.Key);
    }
    public void ReleaseAll()
    {
        var events = held.Values.Select(x => new KeyStroke(x.Key, true)).Concat(modifiers.Values.Select(k => new KeyStroke(k, true))).ToArray();
        if (events.Length > 0) send(events);
        held.Clear(); modifiers.Clear();
    }
}
