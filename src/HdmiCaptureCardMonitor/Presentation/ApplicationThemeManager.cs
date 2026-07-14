using System.IO;
using System.Security;
using System.Windows;
using Microsoft.Win32;

namespace HdmiCaptureCardMonitor.Presentation;

internal static class ApplicationThemeManager
{
    internal const string LightThemePath = "Resources/Themes/Light.xaml";
    internal const string DarkThemePath = "Resources/Themes/Dark.xaml";

    internal static ApplicationTheme DetectPreferredTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return ResolveTheme(key?.GetValue("AppsUseLightTheme") as int?);
        }
        catch (Exception exception) when (exception is SecurityException or UnauthorizedAccessException or IOException)
        {
            return ApplicationTheme.Light;
        }
    }

    internal static ApplicationTheme ResolveTheme(int? appsUseLightTheme) =>
        appsUseLightTheme == 0 ? ApplicationTheme.Dark : ApplicationTheme.Light;

    internal static void ApplyPreferredTheme(ResourceDictionary resources)
    {
        ArgumentNullException.ThrowIfNull(resources);
        var dictionaries = resources.MergedDictionaries;
        var existing = dictionaries.FirstOrDefault(IsColorThemeDictionary);
        var replacement = new ResourceDictionary
        {
            Source = new Uri(DetectPreferredTheme() == ApplicationTheme.Dark ? DarkThemePath : LightThemePath, UriKind.Relative)
        };

        if (existing is null)
        {
            dictionaries.Insert(Math.Min(1, dictionaries.Count), replacement);
            return;
        }

        dictionaries[dictionaries.IndexOf(existing)] = replacement;
    }

    private static bool IsColorThemeDictionary(ResourceDictionary dictionary)
    {
        var source = dictionary.Source?.OriginalString;
        return string.Equals(source, LightThemePath, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(source, DarkThemePath, StringComparison.OrdinalIgnoreCase);
    }
}
