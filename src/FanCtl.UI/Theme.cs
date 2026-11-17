using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace FanCtl.UI;

/// <summary>Follows the Windows app theme (Settings › Personalization › Colors), live.</summary>
public static class Theme
{
    public static bool Light { get; private set; }

    public static Color Accent => Light ? Color.FromRgb(0x00, 0x5F, 0xB8) : Color.FromRgb(0x4C, 0xC2, 0xFF);
    public static Color Fore => Light ? Color.FromRgb(0x1B, 0x1B, 0x1B) : Colors.White;
    public static Color Surface => Light ? Color.FromRgb(0xF3, 0xF3, 0xF3) : Color.FromRgb(0x20, 0x20, 0x20);

    public static event Action? Changed;

    static bool ReadSystem()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return k?.GetValue("AppsUseLightTheme") is int v && v != 0;
        }
        catch { return false; }
    }

    public static void Init(ResourceDictionary res)
    {
        Light = ReadSystem();
        Apply(res);
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category != UserPreferenceCategory.General) return;
            bool l = ReadSystem();
            if (l == Light) return;
            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                Light = l;
                Apply(res);
                Changed?.Invoke();
            });
        };
    }

    static void Apply(ResourceDictionary res)
    {
        string[] p = Light
            ? ["#005FB8", "#FFFFFF", "#1B1B1B", "#5C5C5C", "#8A8A8A", "#B3FFFFFF", "#14000000", "#0A000000",
               "#14000000", "#0F000000", "#08000000", "#F9F9F9", "#40FFB900", "#FFFFFF", "#26000000"]
            : ["#4CC2FF", "#0B1A22", "#F3F3F3", "#A6A6A6", "#707070", "#0DFFFFFF", "#17FFFFFF", "#0FFFFFFF",
               "#18FFFFFF", "#14FFFFFF", "#0AFFFFFF", "#2B2B2B", "#33FFB900", "#454545", "#30FFFFFF"];
        string[] keys = ["Accent", "OnAccent", "Text", "Text2", "Text3", "Card", "Stroke", "Control",
                         "ControlHover", "Selected", "Hover", "Popup", "Warn", "ThumbBg", "ThumbStroke"];
        for (int i = 0; i < keys.Length; i++)
        {
            var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(p[i]));
            b.Freeze();
            res[keys[i]] = b;
        }
        CurveEditor.RefreshPalette();
    }
}
