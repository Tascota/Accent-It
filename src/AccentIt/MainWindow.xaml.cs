using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using AccentIt.Models;
using AccentIt.Services;

namespace AccentIt;

public partial class MainWindow : Window
{
    private readonly List<string> _selectedMarkIds = new();
    private readonly List<string> _selectedOperationIds = new();
    private readonly Dictionary<string, ToggleButton> _markButtons = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ToggleButton> _operationButtons = new(StringComparer.Ordinal);
    private AccentCatalog? _catalog;
    private CatalogLoadResult? _loadResult;
    private CompositionResult? _lastResult;
    private CompositionMode _outputMode = CompositionMode.Nfc;
    private SettingsStore? _settingsStore;
    private SettingsLoadResult? _settingsLoadResult;
    private GlobalHotkeyService? _hotkeyService;
    private HotkeyRegistrationResult? _hotkeyRegistrationResult;
    private CompactWindow? _compactWindow;
    private TrayIconService? _trayIcon;
    private bool _openingCompactWindow;

    // 从主窗口的“快速输入”进入时，关闭紧凑窗口应回到主窗口；
    // 用全局快捷键直接打开的紧凑窗口关闭后仍留在原目标应用。
    private bool _compactOpenedFromMain;
    private bool _isClosing;
    private bool _exitRequested;
    private bool _trayHintShown;
    private bool _uiReady;
    private bool _updatingMarkButtons;
    private bool _updatingOperationButtons;
    private bool _updatingOutputMode;

    public MainWindow()
    {
        InitializeComponent();
        ThemeManager.ThemeChanged += ThemeManager_ThemeChanged;
        UpdateThemeToggleUi();
        FitWindowToWorkArea();
        LoadSettings();
        LoadCatalog();

        _uiReady = true;
        BaseLetterBox.Text = "a";
        BaseLetterBox.SelectAll();
        InitializeHotkey();
        InitializeTrayIcon();

        Loaded += (_, _) =>
        {
            if (BaseLetterBox.IsEnabled)
            {
                BaseLetterBox.Focus();
                BaseLetterBox.SelectAll();
            }
        };
    }

    private void FitWindowToWorkArea()
    {
        var workArea = SystemParameters.WorkArea;
        Width = Math.Min(Width, Math.Max(MinWidth, workArea.Width - 32));
        Height = Math.Min(Height, Math.Max(MinHeight, workArea.Height - 32));
    }

    private void LoadCatalog()
    {
        _selectedMarkIds.Clear();
        _selectedOperationIds.Clear();
        _markButtons.Clear();
        _operationButtons.Clear();
        MarkGroupsPanel.Children.Clear();
        OperationGroupsPanel.Children.Clear();
        OtherLetterGroupsPanel.Children.Clear();
        SecondLetterBox.Text = string.Empty;

        try
        {
            _loadResult = AccentCatalogStore.CreateDefault().LoadEffectiveCatalog();
        }
        catch (Exception ex)
        {
            _loadResult = new CatalogLoadResult
            {
                Issues = new[]
                {
                    new CatalogIssue("加载器", $"未预期的加载错误：{ex.Message}")
                }
            };
        }

        _catalog = _loadResult.Catalog;
        _outputMode = _catalog?.CompositionMode ?? CompositionMode.Nfc;
        UpdateOutputModeUi();

        BuildMarkGroups();
        BuildOperationGroups();
        BuildOtherLetterList();

        UpdateConfigUi();
        UpdateSelectionUi();
        UpdatePreview();
    }

    private void UpdateConfigUi()
    {
        var result = _loadResult;
        var issues = result?.Issues ?? Array.Empty<CatalogIssue>();
        var failed = result?.Catalog is null;

        BaseLetterBox.IsEnabled = !failed;
        NfcOutputRadio.IsEnabled = !failed;
        NfdOutputRadio.IsEnabled = !failed;
        OtherLetterSearchBox.IsEnabled = !failed;

        if (failed)
        {
            ConfigStatusTextBlock.Text = "配置加载失败，详情见诊断";
            SetForeground(ConfigStatusTextBlock, "ErrorBrush");
        }
        else
        {
            var sourcePath = result?.SourcePath;
            var fileName = string.IsNullOrWhiteSpace(sourcePath)
                ? AccentCatalogStore.CatalogFileName
                : Path.GetFileName(sourcePath);

            ConfigStatusTextBlock.Text = $"配置：{fileName}";
            SetForeground(ConfigStatusTextBlock, "MutedBrush");
        }

        ConfigStatusTextBlock.ToolTip = result?.SourcePath;
        ConfigPathTextBlock.Text = string.IsNullOrWhiteSpace(result?.SourcePath)
            ? "未确定"
            : result.SourcePath;
        ConfigPathTextBlock.ToolTip = ConfigPathTextBlock.Text;

        if (issues.Count == 0)
        {
            IssuesTextBlock.Text = "没有配置问题。";
            SetForeground(IssuesTextBlock, "MutedBrush");
        }
        else
        {
            IssuesTextBlock.Text = string.Join(
                Environment.NewLine + Environment.NewLine,
                issues.Select(issue => "• " + issue));
            SetForeground(IssuesTextBlock, "ErrorBrush");
        }

        UpdateDiagnosticsSummary();
    }

    private void LoadSettings()
    {
        try
        {
            _settingsStore = SettingsStore.CreateDefault();
            _settingsLoadResult = _settingsStore.Load();
        }
        catch (Exception ex)
        {
            _settingsLoadResult = new SettingsLoadResult
            {
                Issues = new[]
                {
                    new SettingsIssue("设置", $"未预期的设置加载错误：{ex.Message}")
                },
                SourcePath = _settingsStore?.SettingsPath
            };
        }

        UpdateSettingsUi();
    }

    private void InitializeHotkey()
    {
        if (_settingsLoadResult?.Settings is not { } settings)
        {
            UpdateSettingsUi();
            return;
        }

        try
        {
            _hotkeyService = new GlobalHotkeyService();
            _hotkeyService.HotkeyPressed += GlobalHotkeyService_HotkeyPressed;
            _hotkeyRegistrationResult = _hotkeyService.Register(settings.Hotkey);
        }
        catch (Exception ex)
        {
            _hotkeyRegistrationResult = new HotkeyRegistrationResult(
                false,
                0,
                $"注册全局快捷键时发生异常：{ex.Message}");
        }

        if (_hotkeyRegistrationResult is { Success: false })
        {
            DisposeHotkeyService();
        }

        UpdateSettingsUi();
    }

    private async void GlobalHotkeyService_HotkeyPressed(object? sender, EventArgs e)
    {
        if (_isClosing)
        {
            return;
        }

        // 紧凑窗口只保留一个。重复按快捷键只激活已有窗口，不重置当前输入，
        // 也不再重新捕获目标。
        if (_compactWindow is { } existing)
        {
            existing.BringToFront();
            SetStatus("已切回快速输入窗口");
            return;
        }

        if (_openingCompactWindow)
        {
            // UIA 备用定位最多等待约 250 ms；期间重复触发不创建第二个窗口。
            return;
        }

        _openingCompactWindow = true;

        try
        {
            // 关键顺序：先在目标应用仍是前台时捕获窗口/线程/焦点，
            // 再打开会抢走焦点的紧凑窗口。
            var target = ForegroundWindowService.Capture();

            if (target is { IsExternal: true, CaretRect: null, IsPasswordField: false })
            {
                // 没有 Win32 系统光标时才使用 UIA；它可能耗时，因此先捕获目标再定位。
                var location = await CaretLocator.LocateAsync(
                    target,
                    TimeSpan.FromMilliseconds(250));

                if (_isClosing)
                {
                    return;
                }

                target = ForegroundWindowService.ApplyCaretLocation(target, location);
            }

            if (_isClosing)
            {
                return;
            }

            // 定位期间如果另一个触发生成了窗口，只激活它。
            if (_compactWindow is { } openedDuringCapture)
            {
                openedDuringCapture.BringToFront();
                return;
            }

            OpenCompactWindow(target);
            SetStatus(target is { HasUsableTarget: true }
                ? $"已捕获目标：{target.DisplayName}。"
                : "无外部目标，仅复制结果");
        }
        catch (Exception ex)
        {
            SetStatus($"全局快捷键处理失败：{ex.Message}", isError: true);
        }
        finally
        {
            _openingCompactWindow = false;
        }
    }

    private void OpenCompactWindow(
        TargetContext? target,
        CompositionSnapshot? initial = null,
        bool returnToMain = false)
    {
        // 紧凑窗口只保留一个。重复按快捷键只激活已有窗口，不重置当前输入。
        if (_compactWindow is { } existing)
        {
            _compactOpenedFromMain |= returnToMain;
            existing.BringToFront();
            return;
        }

        var window = new CompactWindow(_catalog, target, initial);
        window.SwitchToMainRequested += CompactWindow_SwitchToMainRequested;
        window.Closed += CompactWindow_Closed;
        _compactWindow = window;
        _compactOpenedFromMain = returnToMain;

        try
        {
            window.Show();
            window.BringToFront();
        }
        catch
        {
            if (ReferenceEquals(_compactWindow, window))
            {
                _compactWindow = null;
                _compactOpenedFromMain = false;
            }

            try
            {
                window.Close();
            }
            catch
            {
                // 窗口创建失败时不再抛出第二个异常，保留原始错误。
            }

            throw;
        }
    }

    private void CompactWindow_Closed(object? sender, EventArgs e)
    {
        if (!ReferenceEquals(sender, _compactWindow))
        {
            return;
        }

        _compactWindow = null;

        if (_compactOpenedFromMain && !_isClosing)
        {
            _compactOpenedFromMain = false;

            // 等关闭流程结束后再显示主窗口，避免在 Closed 事件里重入。
            Dispatcher.BeginInvoke(new Action(ShowFromTray));
            return;
        }

        _compactOpenedFromMain = false;
    }

    private void CompactWindow_SwitchToMainRequested(object? sender, EventArgs e)
    {
        if (_isClosing
            || sender is not CompactWindow compact
            || !ReferenceEquals(compact, _compactWindow)
            || compact.IsBusy)
        {
            return;
        }

        var snapshot = compact.CreateSnapshot();

        // OnClosed 不应把前台焦点还给原目标应用；主窗口马上接管。
        compact.SuppressTargetFocusRestore();
        _compactOpenedFromMain = false;

        compact.Close();

        if (compact.IsVisible)
        {
            // 关闭被拒绝时保持原状，不要把主窗口和紧凑窗口同时显示出来。
            compact.BringToFront();
            _compactOpenedFromMain = true;
            return;
        }

        ShowFromTray();
        ApplyCompositionSnapshot(snapshot);
        SetStatus("已切换到完整编辑器");
    }

    private void QuickInputButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isClosing)
        {
            return;
        }

        if (_compactWindow is { IsBusy: true })
        {
            SetStatus("快速输入窗口正在插入，请稍候。", isError: true);
            return;
        }

        var snapshot = CreateCompositionSnapshot();
        Hide();

        try
        {
            // 从主窗口进入时没有外部目标，Enter 只复制结果；
            // 需要向其他应用插入时，仍从目标应用里按全局快捷键打开。
            OpenCompactWindow(null, snapshot, returnToMain: true);
        }
        catch (Exception ex)
        {
            ShowFromTray();
            SetStatus($"无法打开快速输入窗口：{ex.Message}", isError: true);
        }
    }

    private void ThemeToggleButton_Click(object sender, RoutedEventArgs e)
        => ThemeManager.Toggle();

    private void ThemeManager_ThemeChanged(object? sender, EventArgs e)
    {
        UpdateThemeToggleUi();
        ThemeManager.ApplyWindowChrome(this);
    }

    private void UpdateThemeToggleUi()
    {
        var dark = ThemeManager.Current == AppTheme.Dark;

        ThemeToggleButton.Content = dark ? "日间模式" : "深夜模式";
        ThemeToggleButton.ToolTip = dark
            ? "切换到日间模式"
            : "切换到深夜模式";
        AutomationProperties.SetName(
            ThemeToggleButton,
            dark ? "切换到日间模式" : "切换到深夜模式");
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.F2 || _isClosing)
        {
            return;
        }

        e.Handled = true;
        QuickInputButton_Click(this, new RoutedEventArgs());
    }

    private CompositionSnapshot CreateCompositionSnapshot()
        => new(
            BaseLetterBox.Text,
            _selectedMarkIds.ToArray(),
            _outputMode,
            _selectedOperationIds.ToArray(),
            RequiresSecondLetter() ? SecondLetterBox.Text : null);

    /// <summary>
    /// 接收紧凑窗口带回来的输入。目录里已经不存在的符号会被忽略，
    /// 和紧凑窗口使用同一套规则，避免切换后出现无法组合的选中项。
    /// </summary>
    private void ApplyCompositionSnapshot(CompositionSnapshot snapshot)
    {
        _uiReady = false;

        try
        {
            _selectedMarkIds.Clear();
            _selectedOperationIds.Clear();

            if (_catalog is not null)
            {
                foreach (var markId in snapshot.MarkIds.Distinct(StringComparer.Ordinal))
                {
                    if (_catalog.FindMark(markId) is not null)
                    {
                        _selectedMarkIds.Add(markId);
                    }
                }

                foreach (var operationId in (snapshot.OperationIds ?? Array.Empty<string>())
                             .Distinct(StringComparer.Ordinal))
                {
                    if (_catalog.LetterComposition?.FindOperation(operationId) is not null)
                    {
                        _selectedOperationIds.Add(operationId);
                    }
                }
            }

            BaseLetterBox.Text = snapshot.BaseLetterText ?? string.Empty;
            BaseLetterBox.SelectAll();

            SecondLetterBox.Text = RequiresSecondLetter()
                ? snapshot.SecondLetterText ?? string.Empty
                : string.Empty;

            _outputMode = snapshot.OutputMode;
            UpdateOutputModeUi();
        }
        finally
        {
            _uiReady = true;
        }

        UpdateSelectionUi();
        UpdatePreview();
    }

    private void EditorTab_Checked(object sender, RoutedEventArgs e)
    {
        // 初始 IsChecked 会在 XAML 加载期间触发，此时其他控件可能尚未创建。
        if (MarkGroupsScrollViewer is null
            || OperationGroupsScrollViewer is null
            || OtherLettersGrid is null)
        {
            return;
        }

        var showMarks = MarksTabRadio.IsChecked == true;
        var showOperations = OperationsTabRadio.IsChecked == true;
        var showOtherLetters = OtherLettersTabRadio.IsChecked == true;

        MarkGroupsScrollViewer.Visibility = showMarks
            ? Visibility.Visible
            : Visibility.Collapsed;
        OperationGroupsScrollViewer.Visibility = showOperations
            ? Visibility.Visible
            : Visibility.Collapsed;
        OtherLettersGrid.Visibility = showOtherLetters
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void OpenSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var path = _settingsStore?.SettingsPath;

        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            SetStatus("设置文件不存在，无法打开。", isError: true);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            SetStatus($"已打开设置文件：{path}");
        }
        catch (Exception ex)
        {
            SetStatus($"无法打开设置文件：{ex.Message}", isError: true);
        }
    }

    private void UpdateSettingsUi()
    {
        var result = _settingsLoadResult;
        var issues = result?.Issues ?? Array.Empty<SettingsIssue>();
        var settings = result?.Settings;
        var path = _settingsStore?.SettingsPath ?? result?.SourcePath;

        SettingsPathTextBlock.Text = string.IsNullOrWhiteSpace(path) ? "未确定" : path;
        SettingsPathTextBlock.ToolTip = SettingsPathTextBlock.Text;

        if (issues.Count == 0)
        {
            SettingsIssuesTextBlock.Text = "没有设置问题。";
            SetForeground(SettingsIssuesTextBlock, "MutedBrush");
        }
        else
        {
            SettingsIssuesTextBlock.Text = string.Join(
                Environment.NewLine + Environment.NewLine,
                issues.Select(issue => "• " + issue));
            SetForeground(SettingsIssuesTextBlock, "ErrorBrush");
        }

        OpenSettingsButton.IsEnabled = !string.IsNullOrWhiteSpace(path)
                                       && File.Exists(path);

        if (settings is null)
        {
            HotkeyStatusTextBlock.Text = "全局快捷键：设置加载失败";
            SetForeground(HotkeyStatusTextBlock, "ErrorBrush");
        }
        else if (_hotkeyRegistrationResult is null)
        {
            HotkeyStatusTextBlock.Text = $"全局快捷键：{settings.Hotkey}（未注册）";
            SetForeground(HotkeyStatusTextBlock, "ErrorBrush");
        }
        else if (!_hotkeyRegistrationResult.Success)
        {
            HotkeyStatusTextBlock.Text =
                $"全局快捷键：{settings.Hotkey}（未注册：{_hotkeyRegistrationResult.Message}）";
            SetForeground(HotkeyStatusTextBlock, "ErrorBrush");
        }
        else if (result?.UsedFallback == true)
        {
            HotkeyStatusTextBlock.Text =
                $"全局快捷键：{settings.Hotkey}（已注册；设置文件有问题，使用默认值）";
            SetForeground(HotkeyStatusTextBlock, "ErrorBrush");
        }
        else
        {
            HotkeyStatusTextBlock.Text = $"全局快捷键：{settings.Hotkey}（已注册）";
            SetForeground(HotkeyStatusTextBlock, "MutedBrush");
        }

        var tooltipLines = new List<string>();

        if (!string.IsNullOrWhiteSpace(path))
        {
            tooltipLines.Add("设置文件：" + path);
        }

        if (issues.Count > 0)
        {
            tooltipLines.AddRange(issues.Select(issue => "• " + issue));
        }

        if (_hotkeyRegistrationResult is { } registration)
        {
            tooltipLines.Add("注册结果：" + registration.Message);
        }

        HotkeyStatusTextBlock.ToolTip = tooltipLines.Count == 0
            ? null
            : string.Join(Environment.NewLine, tooltipLines);

        UpdateDiagnosticsSummary();
    }

    private void UpdateDiagnosticsSummary()
    {
        var catalogIssues = _loadResult?.Issues.Count ?? 0;
        var settingsIssues = _settingsLoadResult?.Issues.Count ?? 0;
        var totalIssues = catalogIssues + settingsIssues;

        DiagnosticsExpander.Header = totalIssues == 0
            ? "诊断详情"
            : $"诊断详情（{totalIssues} 条配置提示）";
        DiagnosticsExpander.IsExpanded = _loadResult?.Catalog is null || totalIssues > 0;
    }

    private void DisposeHotkeyService()
    {
        if (_hotkeyService is null)
        {
            return;
        }

        _hotkeyService.HotkeyPressed -= GlobalHotkeyService_HotkeyPressed;
        _hotkeyService.Dispose();
        _hotkeyService = null;
    }

    private void InitializeTrayIcon()
    {
        try
        {
            _trayIcon = new TrayIconService();
            _trayIcon.ShowRequested += (_, _) =>
                Dispatcher.BeginInvoke(new Action(ShowFromTray));
            _trayIcon.ExitRequested += (_, _) =>
                Dispatcher.BeginInvoke(new Action(ExitFromTray));
        }
        catch (Exception ex)
        {
            // 托盘不可用时退回旧行为：关闭主窗口即退出程序。
            _trayIcon = null;
            SetStatus($"托盘图标创建失败：{ex.Message}。关闭主窗口将退出程序。", isError: true);
        }
    }

    private void ShowFromTray()
    {
        if (_isClosing)
        {
            return;
        }

        Show();

        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();

        var handle = new WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero)
        {
            ForegroundWindowService.TryBringToForeground(handle);
        }

        if (BaseLetterBox.IsEnabled)
        {
            BaseLetterBox.Focus();
        }
    }

    private void ExitFromTray()
    {
        if (_isClosing)
        {
            return;
        }

        // 插入流程会临时改写剪贴板并发送按键，不能在中间状态直接退出。
        if (_compactWindow is { IsBusy: true })
        {
            _trayIcon?.ShowInfo(
                "正在完成插入",
                "请等待插入流程结束后再从托盘菜单退出。");
            return;
        }

        _exitRequested = true;

        // 退出流程中紧凑窗口的 Closed 事件不应再排队显示主窗口。
        _compactOpenedFromMain = false;

        if (_compactWindow is { } compact)
        {
            compact.Close();
        }

        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // ShutdownMode 已改为 OnExplicitShutdown，关闭按钮默认只隐藏窗口。
        // 托盘菜单触发 ExitFromTray 后，_exitRequested 为 true，才真正退出。
        if (!_exitRequested && _trayIcon is not null)
        {
            e.Cancel = true;
            Hide();

            if (!_trayHintShown)
            {
                _trayHintShown = true;
                _trayIcon.ShowInfo(
                    "Accent It 仍在运行",
                    "全局快捷键仍然可用。单击托盘图标打开主窗口，右键托盘菜单可以退出。");
            }

            return;
        }

        // 让尚在等待 UIA 定位的异步热键处理知道程序正在退出。
        _isClosing = true;
        base.OnClosing(e);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ThemeManager.ApplyWindowChrome(this);
    }

    protected override void OnClosed(EventArgs e)
    {
        ThemeManager.ThemeChanged -= ThemeManager_ThemeChanged;

        if (_compactWindow is { } compact)
        {
            _compactWindow = null;
            compact.Close();
        }

        _trayIcon?.Dispose();
        _trayIcon = null;
        DisposeHotkeyService();
        base.OnClosed(e);

        // 托盘模式下没有主窗口时程序仍会运行，因此必须显式结束应用。
        Application.Current.Shutdown();
    }

    private void OutputModeRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (!_uiReady || _updatingOutputMode)
        {
            return;
        }

        var mode = ReferenceEquals(sender, NfdOutputRadio)
            ? CompositionMode.Nfd
            : CompositionMode.Nfc;

        if (_outputMode == mode)
        {
            return;
        }

        _outputMode = mode;
        UpdatePreview();
    }

    private void UpdateOutputModeUi()
    {
        _updatingOutputMode = true;

        try
        {
            NfcOutputRadio.IsChecked = _outputMode == CompositionMode.Nfc;
            NfdOutputRadio.IsChecked = _outputMode == CompositionMode.Nfd;
        }
        finally
        {
            _updatingOutputMode = false;
        }
    }

    private void BuildMarkGroups()
    {
        MarkGroupsPanel.Children.Clear();
        _markButtons.Clear();

        if (_catalog is null)
        {
            var errorText = new TextBlock { Text = "符号目录未加载" };
            SetForeground(errorText, "ErrorBrush");
            MarkGroupsPanel.Children.Add(errorText);
            return;
        }

        foreach (var position in _catalog.Positions)
        {
            var marks = _catalog.GetMarksForPosition(position.Id);
            var group = new StackPanel
            {
                Margin = new Thickness(0, 0, 0, 18)
            };

            var title = new TextBlock
            {
                Text = position.DisplayName,
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 8)
            };
            SetForeground(title, "InkBrush");
            group.Children.Add(title);

            if (marks.Count == 0)
            {
                var emptyText = new TextBlock
                {
                    Text = "这个位置还没有配置符号。",
                    FontSize = 11,
                    FontStyle = FontStyles.Italic
                };
                SetForeground(emptyText, "MutedBrush");
                group.Children.Add(emptyText);
            }
            else
            {
                var wrap = new WrapPanel();

                foreach (var mark in marks)
                {
                    wrap.Children.Add(CreateMarkButton(mark, position));
                }

                group.Children.Add(wrap);
            }

            MarkGroupsPanel.Children.Add(group);
        }
    }

    private ToggleButton CreateMarkButton(AccentMark mark, AccentPosition position)
    {
        var glyph = new TextBlock
        {
            Text = char.ConvertFromUtf32(0x25CC) + mark.Symbol.ToString(),
            FontFamily = new FontFamily("Cambria Math"),
            FontSize = 22,
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 30
        };
        SetForeground(glyph, "InkBrush");

        var name = new TextBlock
        {
            Text = mark.Name,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 160
        };
        SetForeground(name, "InkBrush");

        var codePoint = new TextBlock
        {
            Text = FormatCodePoint(mark.Symbol.Value),
            FontFamily = new FontFamily("Consolas"),
            FontSize = 10
        };
        SetForeground(codePoint, "MutedBrush");

        var labels = new StackPanel
        {
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        labels.Children.Add(name);
        labels.Children.Add(codePoint);

        var content = new StackPanel
        {
            Orientation = Orientation.Horizontal
        };
        content.Children.Add(glyph);
        content.Children.Add(labels);

        var toolTip = $"{mark.Name}（{position.DisplayName}）\n{FormatCodePoint(mark.Symbol.Value)}";

        if (!string.IsNullOrWhiteSpace(mark.Description))
        {
            toolTip += $"\n{mark.Description}";
        }

        toolTip += "";

        var button = new ToggleButton
        {
            Content = content,
            Style = (Style)FindResource("MarkToggleStyle"),
            Tag = mark.Id,
            ToolTip = toolTip,
            IsChecked = _selectedMarkIds.Contains(mark.Id)
        };

        AutomationProperties.SetName(button, $"{position.DisplayName}：{mark.Name}");
        AutomationProperties.SetAutomationId(button, $"mark-{mark.Id}");
        button.Checked += MarkButton_Checked;
        button.Unchecked += MarkButton_Unchecked;
        _markButtons[mark.Id] = button;
        return button;
    }

    private void BuildOperationGroups()
    {
        OperationGroupsPanel.Children.Clear();
        _operationButtons.Clear();

        var composition = _catalog?.LetterComposition;

        if (composition is null)
        {
            AddEmptyPanelMessage(OperationGroupsPanel, "当前配置没有字母变形数据。");
            return;
        }

        if (composition.Operations.Count == 0)
        {
            AddEmptyPanelMessage(OperationGroupsPanel, "还没有配置变形操作。");
            return;
        }

        foreach (var group in composition.Operations.GroupBy(
                     operation => string.IsNullOrWhiteSpace(operation.Group)
                         ? "其他"
                         : operation.Group,
                     StringComparer.Ordinal))
        {
            var groupPanel = new StackPanel
            {
                Margin = new Thickness(0, 0, 0, 18)
            };

            var title = new TextBlock
            {
                Text = group.Key,
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 8)
            };
            SetForeground(title, "InkBrush");
            groupPanel.Children.Add(title);

            var wrap = new WrapPanel();

            foreach (var operation in group)
            {
                wrap.Children.Add(CreateOperationButton(operation));
            }

            groupPanel.Children.Add(wrap);
            OperationGroupsPanel.Children.Add(groupPanel);
        }
    }

    private ToggleButton CreateOperationButton(LetterOperation operation)
    {
        var name = new TextBlock
        {
            Text = operation.Name,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold
        };
        SetForeground(name, "InkBrush");

        var labels = new StackPanel
        {
            MinWidth = 72
        };
        labels.Children.Add(name);

        if (!string.IsNullOrWhiteSpace(operation.Description))
        {
            var description = new TextBlock
            {
                Text = operation.Description,
                FontSize = 10,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 168,
                Margin = new Thickness(0, 2, 0, 0)
            };
            SetForeground(description, "MutedBrush");
            labels.Children.Add(description);
        }

        var toolTip = operation.Name;

        if (!string.IsNullOrWhiteSpace(operation.Group))
        {
            toolTip += $"（{operation.Group}）";
        }

        if (!string.IsNullOrWhiteSpace(operation.Description))
        {
            toolTip += Environment.NewLine + operation.Description;
        }

        if (operation.RequiresSecondLetter)
        {
            toolTip += Environment.NewLine + "选择后需要输入第二个字母。";
        }

        var button = new ToggleButton
        {
            Content = labels,
            Style = (Style)FindResource("MarkToggleStyle"),
            Tag = operation.Id,
            ToolTip = toolTip,
            IsChecked = _selectedOperationIds.Contains(operation.Id)
        };

        AutomationProperties.SetName(button, $"字母变形：{operation.Name}");
        AutomationProperties.SetAutomationId(button, $"operation-{operation.Id}");
        button.Checked += OperationButton_Checked;
        button.Unchecked += OperationButton_Unchecked;
        _operationButtons[operation.Id] = button;
        return button;
    }

    private void BuildOtherLetterList()
    {
        OtherLetterGroupsPanel.Children.Clear();

        var query = OtherLetterSearchBox.Text.Trim();
        OtherLetterSearchHintTextBlock.Visibility = string.IsNullOrWhiteSpace(OtherLetterSearchBox.Text)
            ? Visibility.Visible
            : Visibility.Collapsed;

        var composition = _catalog?.LetterComposition;

        if (composition is null)
        {
            OtherLetterCountTextBlock.Text = "当前配置没有“其他字符”数据。";
            return;
        }

        var matches = composition.OtherLetters
            .Where(letter => MatchesOtherLetter(letter, query, composition))
            .ToArray();

        OtherLetterCountTextBlock.Text = query.Length == 0
            ? $"共 {matches.Length} 个字符"
            : $"找到 {matches.Length} 个字符";

        if (matches.Length == 0)
        {
            AddEmptyPanelMessage(OtherLetterGroupsPanel, "没有匹配的字符。");
            return;
        }

        foreach (var block in composition.Blocks)
        {
            var blockLetters = matches
                .Where(letter => string.Equals(letter.BlockId, block.Id, StringComparison.Ordinal))
                .ToArray();

            if (blockLetters.Length > 0)
            {
                AddOtherLetterGroup(block.DisplayName, blockLetters);
            }
        }

        var knownBlockIds = new HashSet<string>(
            composition.Blocks.Select(block => block.Id),
            StringComparer.Ordinal);
        var ungrouped = matches
            .Where(letter => letter.BlockId is null
                             || !knownBlockIds.Contains(letter.BlockId))
            .ToArray();

        if (ungrouped.Length > 0)
        {
            AddOtherLetterGroup("未分组", ungrouped);
        }
    }

    private void AddOtherLetterGroup(string titleText, IReadOnlyList<OtherLetter> letters)
    {
        var group = new StackPanel
        {
            Margin = new Thickness(0, 0, 0, 14)
        };

        var title = new TextBlock
        {
            Text = $"{titleText}（{letters.Count}）",
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 7)
        };
        SetForeground(title, "InkBrush");
        group.Children.Add(title);

        var wrap = new WrapPanel();

        foreach (var letter in letters)
        {
            wrap.Children.Add(CreateOtherLetterButton(letter));
        }

        group.Children.Add(wrap);
        OtherLetterGroupsPanel.Children.Add(group);
    }

    private Button CreateOtherLetterButton(OtherLetter letter)
    {
        var glyph = new TextBlock
        {
            Text = letter.Letter.ToString(),
            FontFamily = new FontFamily("Cambria Math"),
            FontSize = 22,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        SetForeground(glyph, "InkBrush");

        var label = new TextBlock
        {
            Text = letter.Name,
            FontSize = 10,
            MaxWidth = 138,
            TextTrimming = TextTrimming.CharacterEllipsis,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 1, 0, 0)
        };
        SetForeground(label, "MutedBrush");

        var content = new StackPanel
        {
            Width = 138
        };
        content.Children.Add(glyph);
        content.Children.Add(label);

        var blockName = letter.BlockId is null
            ? null
            : _catalog?.LetterComposition?.FindBlock(letter.BlockId)?.DisplayName;
        var toolTip = $"{letter.Letter}  {letter.Name}"
                      + Environment.NewLine + FormatCodePoint(letter.Letter.Value);

        if (!string.IsNullOrWhiteSpace(blockName))
        {
            toolTip += Environment.NewLine + blockName;
        }

        if (letter.Aliases.Count > 0)
        {
            toolTip += Environment.NewLine + "别名：" + string.Join("、", letter.Aliases);
        }

        toolTip += Environment.NewLine + "点击设为当前字母";

        var button = new Button
        {
            Content = content,
            Style = (Style)FindResource("ChipButtonStyle"),
            Tag = letter,
            Padding = new Thickness(6, 5, 6, 5),
            Margin = new Thickness(0, 0, 6, 6),
            ToolTip = toolTip
        };

        AutomationProperties.SetName(button, $"{letter.Letter} {letter.Name}");
        AutomationProperties.SetAutomationId(button, $"other-letter-{letter.Letter.Value:X4}");
        button.Click += OtherLetterButton_Click;
        return button;
    }

    private static bool MatchesOtherLetter(
        OtherLetter letter,
        string query,
        LetterCompositionCatalog composition)
    {
        if (query.Length == 0)
        {
            return true;
        }

        if (letter.Letter.ToString().Contains(query, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (letter.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (letter.Aliases.Any(alias => alias.Contains(query, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        if (FormatCodePoint(letter.Letter.Value)
            .Contains(query, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var blockName = letter.BlockId is null
            ? null
            : composition.FindBlock(letter.BlockId)?.DisplayName;
        return !string.IsNullOrWhiteSpace(blockName)
               && blockName.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private void OtherLetterSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (OtherLetterGroupsPanel is null || OtherLetterSearchHintTextBlock is null)
        {
            return;
        }

        BuildOtherLetterList();
    }

    private void OtherLetterButton_Click(object sender, RoutedEventArgs e)
    {
        if (_catalog is null || sender is not Button { Tag: OtherLetter letter })
        {
            return;
        }

        _uiReady = false;

        try
        {
            _selectedMarkIds.Clear();
            _selectedOperationIds.Clear();
            SecondLetterBox.Text = string.Empty;
            BaseLetterBox.Text = letter.Letter.ToString();
            BaseLetterBox.SelectAll();
        }
        finally
        {
            _uiReady = true;
        }

        UpdateSelectionUi();
        UpdatePreview();
        SetStatus($"已选择 {letter.Letter}（{FormatCodePoint(letter.Letter.Value)}），可直接复制或继续组合。");
        BaseLetterBox.Focus();
    }

    private static void AddEmptyPanelMessage(Panel panel, string message)
    {
        var text = new TextBlock
        {
            Text = message,
            FontSize = 11,
            FontStyle = FontStyles.Italic,
            TextWrapping = TextWrapping.Wrap
        };
        SetForeground(text, "MutedBrush");
        panel.Children.Add(text);
    }

    private void MarkButton_Checked(object sender, RoutedEventArgs e)
        => ApplyMarkToggle(sender, isSelected: true);

    private void MarkButton_Unchecked(object sender, RoutedEventArgs e)
        => ApplyMarkToggle(sender, isSelected: false);

    private void ApplyMarkToggle(object sender, bool isSelected)
    {
        if (_updatingMarkButtons
            || _catalog is null
            || sender is not ToggleButton button
            || button.Tag is not string markId)
        {
            return;
        }

        if (isSelected)
        {
            if (!_selectedMarkIds.Contains(markId))
            {
                _selectedMarkIds.Add(markId);
            }
        }
        else
        {
            _selectedMarkIds.Remove(markId);
        }

        UpdateSelectionUi();
        UpdatePreview();
    }

    private void OperationButton_Checked(object sender, RoutedEventArgs e)
        => ApplyOperationToggle(sender, isSelected: true);

    private void OperationButton_Unchecked(object sender, RoutedEventArgs e)
        => ApplyOperationToggle(sender, isSelected: false);

    private void ApplyOperationToggle(object sender, bool isSelected)
    {
        if (_updatingOperationButtons
            || _catalog?.LetterComposition is null
            || sender is not ToggleButton button
            || button.Tag is not string operationId)
        {
            return;
        }

        if (isSelected)
        {
            if (!_selectedOperationIds.Contains(operationId))
            {
                _selectedOperationIds.Add(operationId);
            }
        }
        else
        {
            _selectedOperationIds.Remove(operationId);
        }

        UpdateSelectionUi();
        UpdatePreview();
    }

    private void RemoveSelectedOperation_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string operationId)
        {
            return;
        }

        _selectedOperationIds.Remove(operationId);
        UpdateSelectionUi();
        UpdatePreview();
    }

    private void SecondLetterBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_uiReady)
        {
            return;
        }

        UpdateUndoClearState();
        UpdatePreview();
    }

    private bool RequiresSecondLetter()
        => _selectedOperationIds.Any(operationId =>
            _catalog?.LetterComposition?.FindOperation(operationId)?.RequiresSecondLetter == true);

    private bool HasSecondLetterInput
        => RequiresSecondLetter() && !string.IsNullOrEmpty(SecondLetterBox.Text);

    private void RemoveSelectedMark_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string markId)
        {
            return;
        }

        _selectedMarkIds.Remove(markId);
        UpdateSelectionUi();
        UpdatePreview();
    }

    private void UndoButton_Click(object sender, RoutedEventArgs e)
    {
        if (HasSecondLetterInput)
        {
            SecondLetterBox.Clear();
            return;
        }

        if (_selectedOperationIds.Count > 0)
        {
            _selectedOperationIds.RemoveAt(_selectedOperationIds.Count - 1);
            UpdateSelectionUi();
            UpdatePreview();
            return;
        }

        if (_selectedMarkIds.Count > 0)
        {
            _selectedMarkIds.RemoveAt(_selectedMarkIds.Count - 1);
            UpdateSelectionUi();
            UpdatePreview();
        }
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedMarkIds.Count == 0
            && _selectedOperationIds.Count == 0
            && !HasSecondLetterInput)
        {
            return;
        }

        _uiReady = false;

        try
        {
            _selectedMarkIds.Clear();
            _selectedOperationIds.Clear();
            SecondLetterBox.Text = string.Empty;
        }
        finally
        {
            _uiReady = true;
        }

        UpdateSelectionUi();
        UpdatePreview();
    }

    private void UpdateSelectionUi()
    {
        _updatingMarkButtons = true;

        try
        {
            foreach (var pair in _markButtons)
            {
                pair.Value.IsChecked = _selectedMarkIds.Contains(pair.Key);
            }
        }
        finally
        {
            _updatingMarkButtons = false;
        }

        _updatingOperationButtons = true;

        try
        {
            foreach (var pair in _operationButtons)
            {
                pair.Value.IsChecked = _selectedOperationIds.Contains(pair.Key);
            }
        }
        finally
        {
            _updatingOperationButtons = false;
        }

        SelectedOperationsPanel.Children.Clear();
        SelectedMarksPanel.Children.Clear();

        if (_catalog is null)
        {
            SelectedOperationsHeaderTextBlock.Visibility = Visibility.Collapsed;
            SelectedOperationsPanel.Visibility = Visibility.Collapsed;
            SecondLetterPanel.Visibility = Visibility.Collapsed;
            UndoButton.IsEnabled = false;
            ClearButton.IsEnabled = false;
            return;
        }

        if (_selectedOperationIds.Count == 0)
        {
            SelectedOperationsHeaderTextBlock.Visibility = Visibility.Collapsed;
            SelectedOperationsPanel.Visibility = Visibility.Collapsed;
        }
        else
        {
            SelectedOperationsHeaderTextBlock.Text =
                $"已选变形（{_selectedOperationIds.Count}）";
            SelectedOperationsHeaderTextBlock.Visibility = Visibility.Visible;
            SelectedOperationsPanel.Visibility = Visibility.Visible;

            foreach (var operationId in _selectedOperationIds)
            {
                var operation = _catalog.LetterComposition?.FindOperation(operationId);

                if (operation is null)
                {
                    continue;
                }

                var chip = new Button
                {
                    Content = $"{operation.Name}  ×",
                    Tag = operation.Id,
                    Style = (Style)FindResource("ChipButtonStyle"),
                    ToolTip = $"移除 {operation.Name}"
                };
                AutomationProperties.SetName(chip, $"移除变形 {operation.Name}");
                AutomationProperties.SetAutomationId(chip, $"remove-operation-{operation.Id}");
                chip.Click += RemoveSelectedOperation_Click;
                SelectedOperationsPanel.Children.Add(chip);
            }
        }

        var requiresSecondLetter = RequiresSecondLetter();

        if (!requiresSecondLetter && SecondLetterBox.Text.Length > 0)
        {
            // 没选合字时不应让隐藏的第二个字母影响预览或下次切换。
            var wasReady = _uiReady;
            _uiReady = false;

            try
            {
                SecondLetterBox.Text = string.Empty;
            }
            finally
            {
                _uiReady = wasReady;
            }
        }

        SecondLetterPanel.Visibility = requiresSecondLetter
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (_selectedMarkIds.Count == 0)
        {
            SelectedHeaderTextBlock.Text = "已选符号";
        }
        else
        {
            SelectedHeaderTextBlock.Text = $"已选符号（{_selectedMarkIds.Count}）";

            for (var i = 0; i < _selectedMarkIds.Count; i++)
            {
                var mark = _catalog.FindMark(_selectedMarkIds[i]);

                if (mark is null)
                {
                    continue;
                }

                var chip = new Button
                {
                    Content = $"{i + 1}. {mark.Name}  ×",
                    Tag = mark.Id,
                    Style = (Style)FindResource("ChipButtonStyle"),
                    ToolTip = $"移除 {mark.Name}"
                };
                AutomationProperties.SetName(chip, $"移除 {mark.Name}");
                AutomationProperties.SetAutomationId(chip, $"remove-{mark.Id}");
                chip.Click += RemoveSelectedMark_Click;
                SelectedMarksPanel.Children.Add(chip);
            }
        }

        UpdateUndoClearState();
    }

    private void UpdateUndoClearState()
    {
        var hasInput = _selectedMarkIds.Count > 0
                       || _selectedOperationIds.Count > 0
                       || HasSecondLetterInput;

        UndoButton.IsEnabled = hasInput;
        ClearButton.IsEnabled = hasInput;
    }

    private void BaseLetterBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_uiReady)
        {
            return;
        }

        UpdatePreview();
    }

    private void UpdatePreview()
    {
        _lastResult = null;

        if (_catalog is null)
        {
            InputHintTextBlock.Text = "符号目录加载失败";
            SetForeground(InputHintTextBlock, "ErrorBrush");
            ShowPlaceholder("符号目录未加载", "请查看诊断详情中的配置问题。", isError: true);
            return;
        }

        if (!UnicodeComposer.TryParseBaseLetter(BaseLetterBox.Text, out var baseLetter, out var inputError))
        {
            InputHintTextBlock.Text = inputError;
            SetForeground(InputHintTextBlock, "ErrorBrush");
            ShowPlaceholder("等待有效的基础字母", inputError, isError: true);
            return;
        }

        InputHintTextBlock.Text = string.Empty;
        SetForeground(InputHintTextBlock, "MutedBrush");

        var secondLetterText = RequiresSecondLetter()
            ? SecondLetterBox.Text
            : null;
        var result = UnicodeComposer.Compose(
            baseLetter,
            _selectedMarkIds,
            _catalog,
            _outputMode,
            _selectedOperationIds,
            secondLetterText);

        if (!result.IsValid)
        {
            ShowPlaceholder(
                DescribeFailure(result.Error),
                result.Error ?? "组合失败",
                isError: true);
            return;
        }

        _lastResult = result;
        PreviewTextBlock.Text = result.Output;
        OutputKindTextBlock.Text = DescribeKind(result);
        OutputCodePointsTextBlock.Text = FormatCodePoints(result.CodePoints);
        RawTextBlock.Text = FormatCodePointsFromText(result.RawSequence);
        NfcTextBlock.Text = FormatCodePointsFromText(result.Nfc);
        NfdTextBlock.Text = FormatCodePointsFromText(result.Nfd);
        UpdatePathStatus(result);

        var appliedRules = new List<string>();

        if (result.AppliedPathRule is { } pathRule)
        {
            appliedRules.Add("变形路径：" + DescribePathRule(pathRule));
        }

        if (result.AppliedMarkPathRule is { } markPathRule)
        {
            appliedRules.Add("符号例外：" + DescribeMarkPathRule(markPathRule));
        }

        if (result.AppliedRule is { } rule)
        {
            appliedRules.Add("例外规则：" + DescribeRule(rule));
        }

        if (appliedRules.Count > 0)
        {
            AppliedRuleTextBlock.Text = string.Join(
                Environment.NewLine,
                appliedRules);
            AppliedRuleTextBlock.Visibility = Visibility.Visible;
        }
        else
        {
            AppliedRuleTextBlock.Visibility = Visibility.Collapsed;
        }

        CopyButton.IsEnabled = true;
        SetStatus(string.Empty);
    }

    private string DescribeFailure(string? error)
    {
        if (string.Equals(error, "没有这条组合路径", StringComparison.Ordinal))
        {
            return "没有这条组合路径";
        }

        if (_selectedOperationIds.Count > 0
            && error is not null
            && error.Contains("第二个字母", StringComparison.Ordinal))
        {
            return error.Contains("无效", StringComparison.Ordinal)
                ? "第二个字母无效"
                : "需要第二个字母";
        }

        return "无法组合";
    }

    private void UpdatePathStatus(CompositionResult result)
    {
        if (result.AppliedPathRule is { } pathRule)
        {
            PathStatusTextBlock.Text = "变形路径：" + DescribePathRule(pathRule);
            SetForeground(PathStatusTextBlock, "AccentBrush");
            PathStatusTextBlock.Visibility = Visibility.Visible;
            return;
        }

        if (result.AppliedMarkPathRule is { } markPathRule)
        {
            PathStatusTextBlock.Text = "符号例外：" + DescribeMarkPathRule(markPathRule);
            SetForeground(PathStatusTextBlock, "AccentBrush");
            PathStatusTextBlock.Visibility = Visibility.Visible;
            return;
        }

        PathStatusTextBlock.Visibility = Visibility.Collapsed;
    }

    private string DescribePathRule(LetterPathRule rule)
    {
        var parts = new List<string> { rule.BaseLetter.ToString() };

        if (rule.SecondLetter is { } secondLetter)
        {
            parts.Add(secondLetter.ToString());
        }

        foreach (var operationId in rule.OperationIds)
        {
            var operation = _catalog?.LetterComposition?.FindOperation(operationId);
            parts.Add(operation?.Name ?? operationId);
        }

        var text = $"{string.Join(" + ", parts)} → "
                   + $"{rule.Result}（{FormatCodePoint(rule.Result.Value)}）";

        if (!string.IsNullOrWhiteSpace(rule.Note)
            && !rule.Note.Contains('→'))
        {
            text += "；" + rule.Note;
        }

        return text;
    }

    private string DescribeMarkPathRule(LetterPathRule rule)
    {
        var parts = new List<string> { rule.BaseLetter.ToString() };
        parts.AddRange(rule.MarkIds.Select(markId =>
            _catalog?.FindMark(markId)?.Name ?? markId));
        var text = $"{string.Join(" + ", parts)} → "
                   + $"{rule.Result}（{FormatCodePoint(rule.Result.Value)}）";

        if (!string.IsNullOrWhiteSpace(rule.Note)
            && !rule.Note.Contains('→'))
        {
            text += "；" + rule.Note;
        }

        return text;
    }

    private void ShowPlaceholder(string kind, string message, bool isError)
    {
        PreviewTextBlock.Text = "—";
        OutputKindTextBlock.Text = kind;
        OutputCodePointsTextBlock.Text = string.Empty;
        RawTextBlock.Text = "—";
        NfcTextBlock.Text = "—";
        NfdTextBlock.Text = "—";
        PathStatusTextBlock.Visibility = Visibility.Collapsed;
        AppliedRuleTextBlock.Visibility = Visibility.Collapsed;
        CopyButton.IsEnabled = false;
        SetStatus(message, isError);
    }

    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        if (_lastResult is not { IsValid: true } result)
        {
            return;
        }

        try
        {
            Clipboard.SetText(result.Output);
            SetStatus("已复制到剪贴板");
        }
        catch (Exception ex)
        {
            SetStatus($"复制失败：{ex.Message}", isError: true);
        }
    }

    private void SetStatus(string message, bool isError = false)
    {
        StatusTextBlock.Text = message;
        SetForeground(StatusTextBlock, isError ? "ErrorBrush" : "MutedBrush");
    }

    // 使用动态资源引用，切换主题时已经在显示的文字颜色也会自动更新。
    private static void SetForeground(TextBlock element, string resourceKey)
        => element.SetResourceReference(TextBlock.ForegroundProperty, resourceKey);

    private static string DescribeKind(CompositionResult result)
    {
        if (result.AppliedPathRule is not null)
        {
            if (result.AppliedMarkPathRule is not null)
            {
                return "变形路径 + 符号例外";
            }

            return result.AppliedRule is not null
                ? "变形路径 + 例外映射"
                : "字母变形路径";
        }

        if (result.AppliedMarkPathRule is not null)
        {
            return "符号例外映射";
        }

        return DescribeKind(result.Kind);
    }

    private static string DescribeKind(CompositionKind kind) => kind switch
    {
        CompositionKind.BaseOnly => "仅基础字母",
        CompositionKind.Precomposed => "完整预组合字符",
        CompositionKind.NfcSequence => "NFC 部分预组合",
        CompositionKind.NfdSequence => "NFD 完全分解",
        CompositionKind.SpecialRule => "显式例外规则",
        _ => "无效"
    };

    private static string DescribeRule(SpecialCompositionRule rule)
    {
        var text = $"{rule.BaseLetter} + {string.Join(" + ", rule.MarkIds)} → "
                   + $"{rule.Result}（{FormatCodePoint(rule.Result.Value)}）";

        return string.IsNullOrWhiteSpace(rule.Note)
            ? text
            : $"{text}；{rule.Note}";
    }

    private static string FormatCodePoint(int value) => $"U+{value:X4}";

    private static string FormatCodePoints(IEnumerable<int> values)
        => string.Join(" ", values.Select(FormatCodePoint));

    private static string FormatCodePointsFromText(string text)
        => FormatCodePoints(text.EnumerateRunes().Select(rune => rune.Value));
}
