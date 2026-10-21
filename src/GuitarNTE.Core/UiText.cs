using System.Globalization;

namespace GuitarNTE.Core;

/// <summary>Application language shared by UI, validation and background playback.</summary>
public static class UiText
{
    static string language = Resolve(null);
    public static string Language => Volatile.Read(ref language);
    public const string ChineseTitle = "异环钢琴吉他演奏工具";
    public const string EnglishTitle = "NTE Piano & Guitar Player";
    public static string Title => T(ChineseTitle, EnglishTitle);
    public static string Resolve(string? value) => value switch
    {
        "zh-CN" => "zh-CN",
        "en" => "en",
        _ => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "zh" ? "zh-CN" : "en"
    };
    public static void SetLanguage(string? value) => Volatile.Write(ref language, Resolve(value));
    public static string T(string chinese, string english) => Language == "zh-CN" ? chinese : english;
}
