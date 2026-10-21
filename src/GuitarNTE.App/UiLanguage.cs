using System.Windows;
using GuitarNTE.Core;

namespace GuitarNTE.App;

internal static class UiLanguage
{
    static ResourceDictionary? dictionary;
    internal static void Apply(string? language)
    {
        string code = UiText.Resolve(language);
        var replacement = new ResourceDictionary
        {
            Source = new Uri($"/GuitarNTE;component/Strings/{code}.xaml", UriKind.Relative)
        };
        var dictionaries = Application.Current.Resources.MergedDictionaries;
        // Add first so every DynamicResource remains resolvable while switching.
        dictionaries.Add(replacement);
        if (dictionary != null) dictionaries.Remove(dictionary);
        dictionary = replacement;
        UiText.SetLanguage(code);
    }
}
