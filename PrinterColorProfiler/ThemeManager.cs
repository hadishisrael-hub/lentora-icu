using System.Text.Json;
using System.Windows;
using Microsoft.Win32;
using PrinterColorProfiler.Core;

namespace PrinterColorProfiler;

public enum AppTheme { Light, Dim }

// The theme dictionary is always MergedDictionaries[0]; Styles.xaml reads it through DynamicResource.
public static class ThemeManager
{
    private static string SettingsPath => Path.Combine(Store.AppDataRoot, "settings.json");
    public static AppTheme Current { get; private set; } = AppTheme.Light;
    public static bool Persist { get; set; } = true;

    public static void Initialize()
    {
        AppTheme theme = SystemPrefersLight() ? AppTheme.Light : AppTheme.Dim;
        try
        {
            if (File.Exists(SettingsPath) && JsonSerializer.Deserialize<Settings>(File.ReadAllText(SettingsPath)) is { } saved
                && Enum.TryParse(saved.Theme, out AppTheme stored)) theme = stored;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        Apply(theme);
    }

    public static void Apply(AppTheme theme)
    {
        Current = theme;
        var uri = new Uri(theme == AppTheme.Light ? "Theme/Light.xaml" : "Theme/Dim.xaml", UriKind.Relative);
        Application.Current.Resources.MergedDictionaries[0] = new ResourceDictionary { Source = uri };
        if (!Persist) return;
        try
        {
            Directory.CreateDirectory(Store.AppDataRoot);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(new Settings(theme.ToString())));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public static void Toggle() => Apply(Current == AppTheme.Light ? AppTheme.Dim : AppTheme.Light);

    private static bool SystemPrefersLight()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is not int value || value != 0;
    }

    private sealed record Settings(string Theme);
}
