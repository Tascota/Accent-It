using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using AccentIt.Models;

namespace AccentIt.Services;

/// <summary>
/// 在应用级资源中替换浅色/深色主题字典。窗口里的颜色都使用
/// DynamicResource，因此切换后已打开的窗口会立即更新。
/// </summary>
public static class ThemeManager
{
    private static readonly Uri LightThemeUri =
        new("pack://application:,,,/AccentIt;component/Themes/LightTheme.xaml");

    private static readonly Uri DarkThemeUri =
        new("pack://application:,,,/AccentIt;component/Themes/DarkTheme.xaml");

    private static readonly int DarkModeAttribute = 20;
    private static readonly int DarkModeAttributeBefore20H1 = 19;
    private static readonly int CaptionColorAttribute = 35;

    // DWM 的“恢复默认标题栏颜色”值，对应 0xFFFFFFFF。
    private const int DefaultCaptionColor = -1;

    private static ResourceDictionary? _activeDictionary;
    private static ThemeStore? _store;

    public static AppTheme Current { get; private set; } = AppTheme.Light;

    /// <summary>主题字典已经替换后触发；窗口用它更新按钮文字和标题栏。</summary>
    public static event EventHandler? ThemeChanged;

    /// <summary>启动时读取上次选择；文件缺失或损坏时使用浅色，不写文件。</summary>
    public static void Initialize(ThemeStore store)
    {
        _store = store;
        Apply(store.Load(), persist: false);
    }

    /// <summary>切换并记住选择。设置里的快捷键与主题互不影响。</summary>
    public static void Toggle()
        => Apply(Current == AppTheme.Light ? AppTheme.Dark : AppTheme.Light, persist: true);

    /// <summary>让 Win32 标题栏跟随主题；失败时只保留内容区配色，不影响使用。</summary>
    public static void ApplyWindowChrome(Window window)
    {
        if (window is null)
        {
            return;
        }

        try
        {
            var handle = new WindowInteropHelper(window).EnsureHandle();

            if (handle == IntPtr.Zero)
            {
                return;
            }

            var dark = Current == AppTheme.Dark ? 1 : 0;

            // Windows 10 2004+ 使用属性 20；更早版本使用 19。
            if (NativeMethods.DwmSetWindowAttribute(
                    handle,
                    DarkModeAttribute,
                    ref dark,
                    sizeof(int)) != 0)
            {
                NativeMethods.DwmSetWindowAttribute(
                    handle,
                    DarkModeAttributeBefore20H1,
                    ref dark,
                    sizeof(int));
            }

            // Windows 11 起可以单独给标题栏上色。用内容区背景色让蓝黑界面
            // 和标题栏连成一体；切回浅色或系统不支持时恢复 DWM 默认颜色。
            if (Current == AppTheme.Dark
                && Application.Current?.TryFindResource("AppBackgroundBrush") is SolidColorBrush background)
            {
                var captionColor = ToColorRef(background.Color);
                NativeMethods.DwmSetWindowAttribute(
                    handle,
                    CaptionColorAttribute,
                    ref captionColor,
                    sizeof(int));
            }
            else
            {
                var defaultCaptionColor = DefaultCaptionColor;
                NativeMethods.DwmSetWindowAttribute(
                    handle,
                    CaptionColorAttribute,
                    ref defaultCaptionColor,
                    sizeof(int));
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException
                                   or EntryPointNotFoundException
                                   or InvalidOperationException)
        {
            // 标题栏美化不是核心功能；系统不支持时静默跳过。
        }
    }

    private static int ToColorRef(Color color)
        => color.R | (color.G << 8) | (color.B << 16);

    private static void Apply(AppTheme theme, bool persist)
    {
        Current = theme;

        if (Application.Current is { } app)
        {
            var dictionary = new ResourceDictionary
            {
                Source = theme == AppTheme.Dark ? DarkThemeUri : LightThemeUri
            };

            // App.xaml 里预置了浅色字典作为设计/启动兜底；
            // 第一次切换时找到并替换它，避免两个字典同时生效。
            _activeDictionary ??= FindActiveThemeDictionary(app);

            if (_activeDictionary is not null)
            {
                app.Resources.MergedDictionaries.Remove(_activeDictionary);
            }

            app.Resources.MergedDictionaries.Add(dictionary);
            _activeDictionary = dictionary;
        }

        if (persist)
        {
            _store?.Save(theme);
        }

        ThemeChanged?.Invoke(null, EventArgs.Empty);
    }

    private static ResourceDictionary? FindActiveThemeDictionary(Application app)
    {
        foreach (var dictionary in app.Resources.MergedDictionaries)
        {
            var source = dictionary.Source?.OriginalString;

            if (source is not null
                && (source.EndsWith("LightTheme.xaml", StringComparison.OrdinalIgnoreCase)
                    || source.EndsWith("DarkTheme.xaml", StringComparison.OrdinalIgnoreCase)))
            {
                return dictionary;
            }
        }

        return null;
    }
}
