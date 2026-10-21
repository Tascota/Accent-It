using System.ComponentModel;
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

public partial class CompactWindow : Window
{
    private readonly List<string> _selectedMarkIds = new();
    private readonly List<string> _selectedOperationIds = new();
    private readonly Dictionary<string, ToggleButton> _markButtons = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ToggleButton> _operationButtons = new(StringComparer.Ordinal);
    private readonly AccentCatalog? _catalog;
    private readonly TargetContext? _target;
    private CompositionResult? _lastResult;
    private CompositionMode _outputMode = CompositionMode.Nfc;
    private bool _uiReady;
    private bool _updatingMarkButtons;
    private bool _updatingOperationButtons;
    private bool _updatingOutputMode;
    private bool _busy;
    private bool _hasInserted;
    private ToolPanelMode _toolPanelMode;

    // 窗口不在前台时，Esc/Enter 会落到目标应用。
    // 此时按钮文字不显示快捷键提示，等窗口真正被激活后再恢复。
    private bool _compactKeysUnavailable;

    // 自动插入会临时改写剪贴板。_keepClipboard 表示用户主动点了“复制结果”，
    // 此时关闭窗口保留结果，不自动恢复旧剪贴板。
    private bool _keepClipboard;
    private bool _clipboardBackupAttempted;
    private ClipboardBackup? _clipboardBackup;
    private DataObject? _ourClipboardData;
    private long _ourClipboardSequence;
    private string? _clipboardWarning;

    // 切换到完整编辑器时，主窗口会主动处理焦点；关闭紧凑窗口时
    // 不应再把焦点还给原来的目标应用。
    private bool _suppressTargetFocusRestore;

    /// <summary>插入流程进行中时，主窗口不能直接退出，否则剪贴板可能停在临时状态。</summary>
    public bool IsBusy => _busy;

    /// <summary>用户要求切换到完整编辑器；由主窗口负责显示和状态传递。</summary>
    public event EventHandler? SwitchToMainRequested;

    public CompactWindow(
        AccentCatalog? catalog,
        TargetContext? target,
        CompositionSnapshot? initial = null)
    {
        _catalog = catalog;
        _target = target;

        InitializeComponent();
        ThemeManager.ThemeChanged += ThemeManager_ThemeChanged;
        UpdateThemeToggleUi();

        _outputMode = _catalog?.CompositionMode ?? CompositionMode.Nfc;
        UpdateOutputModeUi();
        BuildMarkLayout();
        BuildOperationGroups();
        BuildOtherLetterTiles();
        UpdateCatalogBanner();
        UpdateTargetUi();
        ApplyCatalogAvailability();

        _uiReady = true;

        if (initial is null)
        {
            ResetComposition();
        }
        else
        {
            ApplyCompositionSnapshot(initial);
        }

        // 在 Show 之前创建 HWND，才能用 SetWindowPos 按物理像素放置；
        // 否则 WPF 要等窗口显示后才有原生句柄。
        new WindowInteropHelper(this).EnsureHandle();
        ThemeManager.ApplyWindowChrome(this);
        PositionWindow();
    }

    private bool CanInsert
        => _catalog is not null
           && _target is { HasUsableTarget: true }
           && !_target.IsPasswordField;

    /// <summary>
    /// 重新触发全局快捷键时调用：只把已有的紧凑窗口带到前台，不重置正在输入的内容。
    /// </summary>
    public void BringToFront()
    {
        if (!IsVisible)
        {
            Show();
        }

        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();

        // 与完整编辑器使用相同的前台激活补充；热键消息本身不会自动把窗口带到前台。
        var helper = new WindowInteropHelper(this);
        helper.EnsureHandle();
        ForegroundWindowService.TryBringToForeground(helper.Handle);
        _compactKeysUnavailable = !ForegroundWindowService.IsForeground(helper.Handle);
        UpdateActionState();

        if (_busy || !BaseLetterBox.IsEnabled)
        {
            CancelButton.Focus();
        }
        else
        {
            BaseLetterBox.Focus();
        }
    }

    private void UpdateTargetUi()
    {
        if (_catalog is null)
        {
            TargetHintTextBlock.Text = "符号目录不可用";
            return;
        }

        if (_target is null || !_target.HasUsableTarget)
        {
            TargetHintTextBlock.Text = "无外部目标，仅复制";
            return;
        }

        if (_target.IsPasswordField)
        {
            TargetHintTextBlock.Text = $"目标：{_target.DisplayName}（密码框）";
            return;
        }

        TargetHintTextBlock.Text = $"目标：{_target.DisplayName}";
    }

    private void PositionWindow()
    {
        var handle = new WindowInteropHelper(this).Handle;

        if (handle == IntPtr.Zero)
        {
            return;
        }

        var size = PopupPlacement.GetWindowSize(handle);

        if (size is null)
        {
            var dpi = NativeMethods.GetDpiForWindow(handle);
            var scale = (dpi == 0 ? 96u : dpi) / 96.0;
            var width = (int)Math.Round((ActualWidth > 0 ? ActualWidth : Width) * scale);
            var height = (int)Math.Round((ActualHeight > 0 ? ActualHeight : Height) * scale);

            if (width <= 0 || height <= 0)
            {
                return;
            }

            size = new PhysicalSize(width, height);
        }

        var position = PopupPlacement.ComputePosition(
            _target,
            size.Value.Width,
            size.Value.Height);
        ApplyPhysicalPosition(handle, position);
    }

    /// <summary>
    /// 先用 WPF 的 Left/Top 跟随位置（减少显示时的跳动），再用 SetWindowPos
    /// 按物理像素做最终定位。WPF 的坐标单位是 DIP，不能直接拿来和屏幕像素比较。
    /// </summary>
    private void ApplyPhysicalPosition(IntPtr handle, PhysicalPoint position)
    {
        var dpi = NativeMethods.GetDpiForWindow(handle);
        var scale = (dpi == 0 ? 96u : dpi) / 96.0;

        if (scale > 0)
        {
            Left = position.X / scale;
            Top = position.Y / scale;
        }

        PopupPlacement.MoveWindow(handle, position);
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        // 显示后窗口已有真实尺寸，再按物理像素校正一次位置。
        PositionWindow();
        Activate();

        var helper = new WindowInteropHelper(this);
        ForegroundWindowService.TryBringToForeground(helper.Handle);

        if (_busy || !BaseLetterBox.IsEnabled)
        {
            CancelButton.Focus();
            return;
        }

        BaseLetterBox.Focus();
        BaseLetterBox.SelectAll();
    }

    private void ResetComposition()
    {
        _selectedMarkIds.Clear();
        _selectedOperationIds.Clear();
        SetSecondLetterTextQuietly(string.Empty);
        BaseLetterBox.Text = "a";
        BaseLetterBox.SelectAll();

        _outputMode = _catalog?.CompositionMode ?? CompositionMode.Nfc;
        UpdateOutputModeUi();
        UpdateSelectionUi();
        UpdatePreview();
    }

    /// <summary>读取当前输入，供“完整编辑器（F2）”切换时带过去。</summary>
    public CompositionSnapshot CreateSnapshot()
        => new(
            BaseLetterBox.Text,
            _selectedMarkIds.ToArray(),
            _outputMode,
            _selectedOperationIds.ToArray(),
            RequiresSecondLetter() && !string.IsNullOrEmpty(SecondLetterBox.Text)
                ? SecondLetterBox.Text
                : null);

    /// <summary>
    /// 应用从完整编辑器带过来的输入。快照里的符号 id 可能已被用户
    /// 从目录中删除，因此只保留当前目录仍能识别的符号。
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
            SecondLetterBox.Text = snapshot.SecondLetterText ?? string.Empty;

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

    /// <summary>切换回完整编辑器时，由主窗口接管焦点。</summary>
    public void SuppressTargetFocusRestore()
        => _suppressTargetFocusRestore = true;

    /// <summary>
    /// 插入成功后清空当前字母和符号，让窗口可以直接开始下一次组合。
    /// 不重置 NFC/NFD 选择，也不改动目标或剪贴板备份。
    /// </summary>
    private void ClearInputAfterInsert()
    {
        _uiReady = false;

        try
        {
            _selectedMarkIds.Clear();
            _selectedOperationIds.Clear();
            SecondLetterBox.Text = string.Empty;
            BaseLetterBox.Text = string.Empty;
        }
        finally
        {
            _uiReady = true;
        }

        _lastResult = null;
        PreviewTextBlock.Text = "—";
        UpdateToolPreviews(null);
        HideToolPath();
        CloseToolPanel(focusBase: false);
        UpdateSelectionUi();
        UpdateActionState();
    }

    private void UpdateCatalogBanner()
    {
        if (_catalog is null)
        {
            CatalogBanner.Visibility = Visibility.Visible;
            CatalogBanner.SetResourceReference(Border.BackgroundProperty, "ErrorSurfaceBrush");
            CatalogBanner.SetResourceReference(Border.BorderBrushProperty, "ErrorBrush");
            SetForeground(CatalogBannerTextBlock, "ErrorBrush");
            CatalogBannerTextBlock.Text = "符号目录加载失败，无法组合";
            CatalogBannerTextBlock.ToolTip = "请查看完整编辑器的“诊断详情”";
            return;
        }

        CatalogBanner.Visibility = Visibility.Collapsed;
    }

    private void ApplyCatalogAvailability()
    {
        UpdateActionState();
    }

    private void BuildMarkLayout()
    {
        AboveMarksPanel.Children.Clear();
        BelowMarksPanel.Children.Clear();
        LeftMarksPanel.Children.Clear();
        RightMarksPanel.Children.Clear();
        _markButtons.Clear();

        if (_catalog is null)
        {
            var errorText = new TextBlock
            {
                Text = "符号目录未加载",
                FontSize = 12,
                FontStyle = FontStyles.Italic,
                Margin = new Thickness(2)
            };
            SetForeground(errorText, "ErrorBrush");
            AboveMarksPanel.Children.Add(errorText);
            return;
        }

        var layout = MarkLayout.Create(_catalog);

        foreach (var mark in layout.Above)
        {
            AboveMarksPanel.Children.Add(CreateMarkButton(mark));
        }

        foreach (var mark in layout.Below)
        {
            BelowMarksPanel.Children.Add(CreateMarkButton(mark));
        }

        foreach (var mark in layout.Left)
        {
            LeftMarksPanel.Children.Add(CreateMarkButton(mark));
        }

        foreach (var mark in layout.Right)
        {
            RightMarksPanel.Children.Add(CreateMarkButton(mark));
        }
    }

    private ToggleButton CreateMarkButton(AccentMark mark)
    {
        var positionName = GetPositionName(mark.PositionId);

        var glyph = new TextBlock
        {
            Text = char.ConvertFromUtf32(0x25CC) + mark.Symbol.ToString(),
            FontFamily = new FontFamily("Cambria Math"),
            FontSize = 18,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        SetForeground(glyph, "InkBrush");

        var toolTip = $"{mark.Name}（{positionName}）\n{FormatCodePoint(mark.Symbol.Value)}";

        if (!string.IsNullOrWhiteSpace(mark.Description))
        {
            toolTip += $"\n{mark.Description}";
        }

        toolTip += "";

        var button = new ToggleButton
        {
            Content = glyph,
            Style = (Style)FindResource("CompactMarkToggleStyle"),
            Tag = mark.Id,
            ToolTip = toolTip,
            IsChecked = _selectedMarkIds.Contains(mark.Id)
        };

        AutomationProperties.SetName(button, $"{positionName}：{mark.Name}");
        AutomationProperties.SetAutomationId(button, $"compact-mark-{mark.Id}");
        button.Checked += MarkButton_Checked;
        button.Unchecked += MarkButton_Unchecked;
        _markButtons[mark.Id] = button;
        return button;
    }

    /// <summary>
    /// 变形操作按目录中的分组顺序排列。紧凑窗口只显示操作名，
    /// 说明和“需要第二字母”放在悬停提示里，避免挤占预览区域。
    /// </summary>
    private void BuildOperationGroups()
    {
        OperationGroupsPanel.Children.Clear();
        _operationButtons.Clear();

        var operations = _catalog?.LetterComposition?.Operations;

        if (operations is null || operations.Count == 0)
        {
            var emptyText = new TextBlock
            {
                Text = "此目录没有变形操作",
                FontSize = 12,
                FontStyle = FontStyles.Italic,
                Margin = new Thickness(2)
            };
            SetForeground(emptyText, "MutedBrush");
            OperationGroupsPanel.Children.Add(emptyText);
            return;
        }

        foreach (var group in operations.GroupBy(operation =>
                     string.IsNullOrWhiteSpace(operation.Group) ? "其他" : operation.Group))
        {
            var groupTitle = new TextBlock
            {
                Text = group.Key,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(2, 3, 2, 2)
            };
            SetForeground(groupTitle, "MutedBrush");

            var wrap = new WrapPanel();
            foreach (var operation in group)
            {
                wrap.Children.Add(CreateOperationButton(operation));
            }

            var groupPanel = new StackPanel
            {
                Margin = new Thickness(0, 0, 0, 2)
            };
            groupPanel.Children.Add(groupTitle);
            groupPanel.Children.Add(wrap);
            OperationGroupsPanel.Children.Add(groupPanel);
        }
    }

    private ToggleButton CreateOperationButton(LetterOperation operation)
    {
        var toolTip = operation.Description ?? operation.Name;

        if (operation.RequiresSecondLetter)
        {
            toolTip += "\n需要输入第二字母";
        }

        var button = new ToggleButton
        {
            Content = operation.Name,
            Style = (Style)FindResource("CompactOperationToggleStyle"),
            Tag = operation.Id,
            ToolTip = toolTip,
            IsChecked = _selectedOperationIds.Contains(operation.Id)
        };

        AutomationProperties.SetName(button, operation.Name);
        AutomationProperties.SetAutomationId(button, $"compact-operation-{operation.Id}");
        button.Checked += OperationButton_Checked;
        button.Unchecked += OperationButton_Unchecked;
        _operationButtons[operation.Id] = button;
        return button;
    }

    private void OperationButton_Checked(object sender, RoutedEventArgs e)
        => ApplyOperationToggle(sender, isSelected: true);

    private void OperationButton_Unchecked(object sender, RoutedEventArgs e)
        => ApplyOperationToggle(sender, isSelected: false);

    private void ApplyOperationToggle(object sender, bool isSelected)
    {
        if (_updatingOperationButtons
            || _busy
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

        // 刚选中“合字”时直接进入第二字母输入框，省去再点一次。
        if (isSelected
            && RequiresSecondLetter()
            && string.IsNullOrEmpty(SecondLetterBox.Text))
        {
            SecondLetterBox.Focus();
            SecondLetterBox.SelectAll();
        }
    }

    private void BuildOtherLetterTiles()
    {
        OtherLetterTilesPanel.Children.Clear();

        var composition = _catalog?.LetterComposition;

        if (composition is null)
        {
            OtherLetterSearchHintTextBlock.Visibility = Visibility.Collapsed;
            OtherLettersCountTextBlock.Text = "符号目录不可用";
            return;
        }

        var query = OtherLetterSearchBox.Text.Trim();
        OtherLetterSearchHintTextBlock.Visibility = string.IsNullOrEmpty(query)
            ? Visibility.Visible
            : Visibility.Collapsed;

        var matches = composition.OtherLetters
            .Where(letter => MatchesOtherLetterSearch(letter, composition, query))
            .ToArray();

        OtherLettersCountTextBlock.Text = string.IsNullOrEmpty(query)
            ? $"共 {matches.Length} 个字符"
            : $"找到 {matches.Length} 个字符";

        if (matches.Length == 0)
        {
            var emptyText = new TextBlock
            {
                Text = "没有匹配的字符。",
                FontSize = 12,
                FontStyle = FontStyles.Italic,
                Margin = new Thickness(2, 4, 2, 2)
            };
            SetForeground(emptyText, "MutedBrush");
            OtherLetterTilesPanel.Children.Add(emptyText);
            return;
        }

        foreach (var letter in matches)
        {
            OtherLetterTilesPanel.Children.Add(CreateOtherLetterButton(letter, composition));
        }
    }

    private static bool MatchesOtherLetterSearch(
        OtherLetter letter,
        LetterCompositionCatalog composition,
        string query)
    {
        if (string.IsNullOrEmpty(query))
        {
            return true;
        }

        var blockName = letter.BlockId is null
            ? null
            : composition.FindBlock(letter.BlockId)?.DisplayName;

        return letter.Letter.ToString().Contains(query, StringComparison.OrdinalIgnoreCase)
               || letter.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
               || letter.Aliases.Any(alias =>
                   alias.Contains(query, StringComparison.OrdinalIgnoreCase))
               || FormatCodePoint(letter.Letter.Value)
                   .Contains(query, StringComparison.OrdinalIgnoreCase)
               || (blockName?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    private Button CreateOtherLetterButton(
        OtherLetter letter,
        LetterCompositionCatalog composition)
    {
        var blockName = letter.BlockId is null
            ? null
            : composition.FindBlock(letter.BlockId)?.DisplayName;
        var codePoint = FormatCodePoint(letter.Letter.Value);

        var glyph = new TextBlock
        {
            Text = letter.Letter.ToString(),
            FontFamily = new FontFamily("Cambria Math"),
            FontSize = 18,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        SetForeground(glyph, "InkBrush");

        var toolTip = $"{letter.Name}\n{codePoint}";

        if (!string.IsNullOrWhiteSpace(blockName))
        {
            toolTip += $"\n{blockName}";
        }

        var button = new Button
        {
            Content = glyph,
            Style = (Style)FindResource("CompactSecondaryButtonStyle"),
            Width = 32,
            Height = 32,
            Padding = new Thickness(0),
            Margin = new Thickness(2),
            Tag = letter,
            ToolTip = toolTip
        };

        AutomationProperties.SetName(button, $"{letter.Name}（{codePoint}）");
        AutomationProperties.SetAutomationId(
            button,
            $"compact-other-letter-{letter.Letter.Value:X4}");
        button.Click += OtherLetterButton_Click;
        return button;
    }

    private void OtherLetterButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || sender is not Button { Tag: OtherLetter letter })
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

        CloseToolPanel(focusBase: false);
        UpdateSelectionUi();
        UpdatePreview();
        SetStatus($"已选择 {letter.Letter}");
        BaseLetterBox.Focus();
        BaseLetterBox.SelectAll();
    }

    private string GetPositionName(string positionId)
        => _catalog?.Positions
            .FirstOrDefault(position => string.Equals(position.Id, positionId, StringComparison.Ordinal))
            ?.DisplayName ?? positionId;

    private void MarkButton_Checked(object sender, RoutedEventArgs e)
        => ApplyMarkToggle(sender, isSelected: true);

    private void MarkButton_Unchecked(object sender, RoutedEventArgs e)
        => ApplyMarkToggle(sender, isSelected: false);

    private void ApplyMarkToggle(object sender, bool isSelected)
    {
        if (_updatingMarkButtons
            || _busy
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

    private void UpdateSelectionUi()
    {
        // 紧凑窗口不显示独立的已选符号列表；按钮高亮和中央预览就是选中状态。
        // 变形操作额外保留一条“已选”摘要，方便确认当前路径。
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

        if (_selectedOperationIds.Count > 0)
        {
            var names = _selectedOperationIds
                .Select(operationId =>
                    _catalog?.LetterComposition?.FindOperation(operationId)?.Name
                    ?? operationId);
            SelectedOperationsTextBlock.Text = "已选：" + string.Join(" + ", names);
            SelectedOperationsTextBlock.Visibility = Visibility.Visible;
            ClearOperationsButton.Visibility = Visibility.Visible;
        }
        else
        {
            SelectedOperationsTextBlock.Visibility = Visibility.Collapsed;
            ClearOperationsButton.Visibility = Visibility.Collapsed;
        }

        var requiresSecondLetter = RequiresSecondLetter();
        SecondLetterPanel.Visibility = requiresSecondLetter
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (!requiresSecondLetter && !string.IsNullOrEmpty(SecondLetterBox.Text))
        {
            SetSecondLetterTextQuietly(string.Empty);
        }
    }

    private void SetSecondLetterTextQuietly(string text)
    {
        var wasReady = _uiReady;
        _uiReady = false;

        try
        {
            SecondLetterBox.Text = text;
        }
        finally
        {
            _uiReady = wasReady;
        }
    }

    private void BaseLetterBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_uiReady)
        {
            return;
        }

        UpdatePreview();
    }

    private void SecondLetterBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_uiReady)
        {
            return;
        }

        UpdatePreview();
    }

    private void OtherLetterSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_uiReady)
        {
            return;
        }

        BuildOtherLetterTiles();
    }

    private void OperationsButton_Click(object sender, RoutedEventArgs e)
        => ToggleToolPanel(ToolPanelMode.Operations);

    private void OtherLettersButton_Click(object sender, RoutedEventArgs e)
        => ToggleToolPanel(ToolPanelMode.OtherLetters);

    private void CloseToolPanelButton_Click(object sender, RoutedEventArgs e)
        => CloseToolPanel();

    private void ToggleToolPanel(ToolPanelMode mode)
    {
        if (_busy || _catalog is null)
        {
            return;
        }

        if (_toolPanelMode == mode)
        {
            CloseToolPanel();
            return;
        }

        _toolPanelMode = mode;
        OperationsToolPanel.Visibility = mode == ToolPanelMode.Operations
            ? Visibility.Visible
            : Visibility.Collapsed;
        OtherLettersToolPanel.Visibility = mode == ToolPanelMode.OtherLetters
            ? Visibility.Visible
            : Visibility.Collapsed;
        ToolPanel.Visibility = Visibility.Visible;
        // 面板覆盖预览和状态行；状态行隐藏后，路径/错误只在面板内部显示一次。
        StatusTextBlock.Visibility = Visibility.Collapsed;

        UpdateSelectionUi();
        UpdatePreview();

        if (mode == ToolPanelMode.OtherLetters)
        {
            OtherLetterSearchBox.Focus();
            OtherLetterSearchBox.SelectAll();
        }
        else if (_operationButtons.Values.FirstOrDefault() is { } firstOperation)
        {
            firstOperation.Focus();
        }
        else
        {
            OperationsCloseButton.Focus();
        }
    }

    private void CloseToolPanel(bool focusBase = true)
    {
        if (_toolPanelMode == ToolPanelMode.None)
        {
            return;
        }

        _toolPanelMode = ToolPanelMode.None;
        ToolPanel.Visibility = Visibility.Collapsed;
        OperationsToolPanel.Visibility = Visibility.Collapsed;
        OtherLettersToolPanel.Visibility = Visibility.Collapsed;
        StatusTextBlock.Visibility = Visibility.Visible;

        if (focusBase
            && !_busy
            && IsActive
            && BaseLetterBox.IsEnabled)
        {
            BaseLetterBox.Focus();
            BaseLetterBox.SelectAll();
        }
    }

    private void ClearOperationsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }

        _selectedOperationIds.Clear();
        SetSecondLetterTextQuietly(string.Empty);
        UpdateSelectionUi();
        UpdatePreview();
        SetStatus("已清空变形");
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

    private bool RequiresSecondLetter()
        => _selectedOperationIds.Any(operationId =>
            _catalog?.LetterComposition?.FindOperation(operationId)?.RequiresSecondLetter == true);

    private void UpdatePreview()
    {
        _lastResult = null;
        UpdateToolPreviews(null);

        if (_catalog is null)
        {
            PreviewTextBlock.Text = "—";
            SetStatus(string.Empty);
            HideToolPath();
            UpdateActionState();
            return;
        }

        if (!UnicodeComposer.TryParseBaseLetter(BaseLetterBox.Text, out var baseLetter, out var inputError))
        {
            PreviewTextBlock.Text = "—";
            SetStatus(inputError, isError: true);
            UpdateToolPathError(DescribeFailure(inputError));
            UpdateActionState();
            return;
        }

        var result = UnicodeComposer.Compose(
            baseLetter,
            _selectedMarkIds,
            _catalog,
            _outputMode,
            _selectedOperationIds,
            RequiresSecondLetter() ? SecondLetterBox.Text : null);

        if (!result.IsValid)
        {
            PreviewTextBlock.Text = "—";
            var message = DescribeFailure(result.Error);
            SetStatus(message, isError: true);
            UpdateToolPathError(message);
            UpdateActionState();
            return;
        }

        _lastResult = result;
        PreviewTextBlock.Text = result.Output;
        UpdateToolPreviews(result.Output);

        // 只有命中路径时才显示路径提示；普通“字母 + 符号”仍然保持干净的状态行。
        var pathText = BuildPathFeedback(result);

        if (pathText is null)
        {
            SetStatus(string.Empty);
            HideToolPath();
        }
        else
        {
            SetStatus(pathText);
            UpdateToolPathSuccess(pathText);
        }

        UpdateActionState();
    }

    private string? BuildPathFeedback(CompositionResult result)
    {
        if (result.AppliedPathRule is { } pathRule)
        {
            return "变形路径：" + DescribePathRule(pathRule);
        }

        if (result.AppliedMarkPathRule is { } markPathRule)
        {
            return "符号例外：" + DescribeMarkPathRule(markPathRule);
        }

        return null;
    }

    private static string DescribeFailure(string? error)
    {
        if (string.IsNullOrWhiteSpace(error))
        {
            return "组合失败";
        }

        if (error.Contains("没有这条组合路径", StringComparison.Ordinal))
        {
            return "没有这条组合路径";
        }

        if (error.Contains("第二个字母", StringComparison.Ordinal))
        {
            return error.Contains("无效", StringComparison.Ordinal)
                ? "第二个字母无效"
                : "需要第二个字母";
        }

        return error;
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

    private void UpdateToolPreviews(string? output)
    {
        var text = string.IsNullOrEmpty(output) ? "—" : output;
        OperationsPreviewTextBlock.Text = text;
        OtherLettersPreviewTextBlock.Text = text;
    }

    private void UpdateToolPathSuccess(string message)
    {
        if (_selectedOperationIds.Count == 0)
        {
            HideToolPath();
            return;
        }

        OperationsPathTextBlock.Text = message;
        SetForeground(OperationsPathTextBlock, "AccentBrush");
        OperationsPathTextBlock.Visibility = Visibility.Visible;
    }

    private void UpdateToolPathError(string message)
    {
        if (_selectedOperationIds.Count == 0)
        {
            HideToolPath();
            return;
        }

        OperationsPathTextBlock.Text = message;
        SetForeground(OperationsPathTextBlock, "ErrorBrush");
        OperationsPathTextBlock.Visibility = Visibility.Visible;
    }

    private void HideToolPath()
    {
        OperationsPathTextBlock.Text = string.Empty;
        OperationsPathTextBlock.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// 根据当前状态集中更新按钮和编辑区域，避免多个代码路径各自设置 IsEnabled
    /// 造成“忙碌时还能编辑”或“忙碌时可以关闭窗口”的不一致。
    /// </summary>
    private void UpdateActionState()
    {
        var hasResult = _lastResult is { IsValid: true };
        var editingEnabled = _catalog is not null && !_busy;

        BaseLetterBox.IsEnabled = editingEnabled;
        NfcOutputRadio.IsEnabled = editingEnabled;
        NfdOutputRadio.IsEnabled = editingEnabled;
        AboveMarksPanel.IsEnabled = editingEnabled;
        BelowMarksPanel.IsEnabled = editingEnabled;
        LeftMarksPanel.IsEnabled = editingEnabled;
        RightMarksPanel.IsEnabled = editingEnabled;
        OperationsButton.IsEnabled = editingEnabled;
        OtherLettersButton.IsEnabled = editingEnabled;
        ToolPanel.IsEnabled = editingEnabled;
        SwitchToEditorButton.IsEnabled = !_busy;

        CopyResultButton.IsEnabled = hasResult && !_busy;
        InsertButton.IsEnabled = hasResult && CanInsert && !_busy;

        var showKeyHints = !_compactKeysUnavailable;
        InsertButton.Content = showKeyHints ? "插入 (Enter)" : "插入";
        CancelButton.Content = _hasInserted
            ? (showKeyHints ? "关闭 (Esc)" : "关闭")
            : (showKeyHints ? "取消 (Esc)" : "取消");

        if (_busy)
        {
            ActionHintTextBlock.Text = "正在插入…";
        }
        else if (_compactKeysUnavailable)
        {
            ActionHintTextBlock.Text = "按全局快捷键切回";
        }
        else
        {
            ActionHintTextBlock.Text = string.Empty;
        }

        CancelButton.IsEnabled = !_busy;
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.F2:
                e.Handled = true;

                if (!_busy)
                {
                    RequestSwitchToEditor();
                }

                return;

            case Key.Escape:
                if (_busy)
                {
                    e.Handled = true;
                    return;
                }

                e.Handled = true;

                if (_toolPanelMode != ToolPanelMode.None)
                {
                    CloseToolPanel();
                    return;
                }

                Close();
                return;

            case Key.Enter:
                if (_busy)
                {
                    e.Handled = true;
                    return;
                }

                e.Handled = true;

                if (CanInsert)
                {
                    _ = InsertResultAsync();
                }
                else
                {
                    CopyResult();
                }

                return;

            case Key.Back when Keyboard.FocusedElement is not TextBoxBase && !_busy:
                e.Handled = true;
                RemoveLastInput();
                return;
        }
    }

    /// <summary>
    /// Backspace 的删除顺序是：第二字母 → 最后一个变形 → 最后一个符号。
    /// 这与完整编辑器的撤销顺序保持一致。
    /// </summary>
    private void RemoveLastInput()
    {
        if (RequiresSecondLetter() && !string.IsNullOrEmpty(SecondLetterBox.Text))
        {
            SetSecondLetterTextQuietly(string.Empty);
            UpdateSelectionUi();
            UpdatePreview();
            SetStatus("已删除第二个字母");
            return;
        }

        if (_selectedOperationIds.Count > 0)
        {
            var operationId = _selectedOperationIds[^1];
            _selectedOperationIds.RemoveAt(_selectedOperationIds.Count - 1);
            var operation = _catalog?.LetterComposition?.FindOperation(operationId);

            UpdateSelectionUi();
            UpdatePreview();
            SetStatus(operation is null ? "已删除最后一个变形" : $"已删除：{operation.Name}");
            return;
        }

        if (_selectedMarkIds.Count == 0)
        {
            SetStatus("没有可删除的输入。");
            return;
        }

        var markId = _selectedMarkIds[^1];
        _selectedMarkIds.RemoveAt(_selectedMarkIds.Count - 1);
        var mark = _catalog?.FindMark(markId);

        UpdateSelectionUi();
        UpdatePreview();
        SetStatus(mark is null ? "已删除最后一个符号" : $"已删除：{mark.Name}");
    }

    private void InsertButton_Click(object sender, RoutedEventArgs e)
        => _ = InsertResultAsync();

    private void CopyResultButton_Click(object sender, RoutedEventArgs e)
        => CopyResult();

    private void CopyResult()
    {
        if (_lastResult is not { IsValid: true } result)
        {
            return;
        }

        // 复制反馈会写到状态行；先收起覆盖状态行的工具面板。
        CloseToolPanel(focusBase: false);

        if (!ClipboardPasteService.TrySetResultText(
                result.Output,
                TimeSpan.FromSeconds(1),
                out var resultData,
                out var error))
        {
            SetStatus($"复制失败：{error}", isError: true);
            return;
        }

        // 用户主动复制后，关闭窗口应保留结果，而不是恢复旧剪贴板。
        _keepClipboard = true;
        _ourClipboardData = resultData;
        _ourClipboardSequence = ClipboardPasteService.GetSequenceNumber();
        SetStatus(
            "已复制",
            details: $"已复制到剪贴板：{FormatCodePoints(result.CodePoints)}。关闭窗口不会恢复原剪贴板。");
        UpdateActionState();
    }

    /// <summary>
    /// 插入流程：验证目标 → 备份剪贴板 → 写入结果 → 恢复目标焦点 →
    /// 等待修饰键松开 → 确认剪贴板未变 → SendInput Ctrl+V。
    /// 任何一步失败都不重试、不自动关闭，保留结果供手动粘贴；
    /// 成功后清空输入，方便在同一个目标里连续插入。
    /// </summary>
    private async Task InsertResultAsync()
    {
        if (_busy)
        {
            return;
        }

        if (_lastResult is not { IsValid: true } result)
        {
            return;
        }

        // 插入过程的状态提示需要占用状态行；先收起覆盖状态行的工具面板。
        CloseToolPanel(focusBase: false);

        var inserted = false;

        if (!CanInsert)
        {
            CopyResult();
            return;
        }

        if (!ForegroundWindowService.TryValidate(_target, out var validationError))
        {
            SetStatus(
                $"无法插入：{validationError} 可“复制结果”后手动粘贴",
                isError: true);
            return;
        }

        _busy = true;
        UpdateActionState();

        try
        {
            if (!_clipboardBackupAttempted && !_keepClipboard)
            {
                _clipboardBackup = ClipboardPasteService.Capture();
                _clipboardBackupAttempted = true;
                _clipboardWarning = _clipboardBackup.Warning;
            }

            if (!ClipboardPasteService.TrySetResultText(
                    result.Output,
                    TimeSpan.FromSeconds(1),
                    out var resultData,
                    out var clipboardError))
            {
                SetStatus(
                    $"无法写入剪贴板：{clipboardError} 目标没有被修改",
                    isError: true);
                await TryReturnToCompactAsync();
                return;
            }

            _ourClipboardData = resultData;
            _ourClipboardSequence = ClipboardPasteService.GetSequenceNumber();

            var activation = await ForegroundWindowService.ActivateAsync(
                _target!,
                TimeSpan.FromMilliseconds(350));

            if (!activation.Success)
            {
                var restoreNote = RestoreClipboardAfterFailedActivation();
                await TryReturnToCompactAsync();
                SetStatus(
                    $"无法插入：{activation.Message} 未发送粘贴，{restoreNote}可直接复制结果",
                    isError: true);
                return;
            }

            if (!ClipboardPasteService.IsResultStillCurrent(
                    _ourClipboardSequence,
                    _ourClipboardData))
            {
                await TryReturnToCompactAsync();
                SetStatus(
                    "剪贴板在发送前被其他程序改写，已取消自动插入，请点“复制结果”后手动粘贴",
                    isError: true);
                return;
            }

            if (!await ForegroundWindowService.WaitForModifiersReleasedAsync(
                    TimeSpan.FromMilliseconds(900)))
            {
                await TryReturnToCompactAsync();
                SetStatus(
                    "修饰键仍未松开，已取消自动插入，请松开按键后重试",
                    isError: true);
                return;
            }

            if (!ForegroundWindowService.IsForeground(_target!.WindowHandle))
            {
                await TryReturnToCompactAsync();
                SetStatus(
                    "目标窗口在发送前失去前台，已取消自动插入，可手动粘贴",
                    isError: true);
                return;
            }

            if (!ClipboardPasteService.TrySendCtrlV(out var inputError))
            {
                await TryReturnToCompactAsync();
                SetStatus(
                    $"发送粘贴失败：{inputError} 可手动粘贴",
                    isError: true);
                return;
            }

            inserted = true;
            _hasInserted = true;

            // 只表示 Ctrl+V 已发送，不能证明目标应用真的接受了文本。
            await Task.Delay(400);
            var targetWasForeground = ForegroundWindowService.IsForeground(_target.WindowHandle);

            var reactivated = await TryReturnToCompactAsync();
            _compactKeysUnavailable = !reactivated;
            var clipboardNote = BuildClipboardNote();
            var visibilityNote = reactivated
                ? string.Empty
                : " 快速输入窗口未能自动回到前台，按快捷键切回后可继续输入";
            var recoveryNote = " 若目标中没有出现文本，可在目标中手动 Ctrl+V，剪贴板中仍保留本次结果";
            var details =
                (targetWasForeground
                    ? "已向目标发送 Ctrl+V，但无法确认是否显示"
                    : "已尝试发送 Ctrl+V，但目标窗口未在前台")
                + " 可继续输入"
                + recoveryNote
                + visibilityNote
                + clipboardNote;

            SetStatus(
                targetWasForeground
                    ? "已发送粘贴；若未出现，按 Ctrl+V"
                    : "已尝试粘贴；若未出现，按 Ctrl+V",
                isError: !targetWasForeground,
                details: details);
        }
        catch (Exception ex)
        {
            await TryReturnToCompactAsync();
            var recoveryNote = inserted
                ? " 剪贴板中仍保留本次结果，可在目标中手动 Ctrl+V"
                : " 可复制结果后手动粘贴";
            SetStatus(
                $"插入时发生异常：{ex.Message} 未确认目标是否收到粘贴。{recoveryNote}",
                isError: true);
        }
        finally
        {
            _busy = false;

            if (inserted)
            {
                ClearInputAfterInsert();
            }
            else
            {
                UpdateActionState();
            }

            // 成功后输入框已经清空；等 _busy 解除、控件重新启用后再把焦点放回去。
            if (inserted && !_compactKeysUnavailable && IsActive && BaseLetterBox.IsEnabled)
            {
                BaseLetterBox.Focus();
            }
        }
    }

    /// <summary>
    /// UIA/前台激活失败时还没有发送按键，可以立即恢复原剪贴板。
    /// 如果用户此前主动复制过，则不恢复，避免覆盖用户明确要求保留的结果。
    /// </summary>
    private string RestoreClipboardAfterFailedActivation()
    {
        if (_keepClipboard)
        {
            return "剪贴板保留你主动复制的结果。";
        }

        if (_clipboardBackup is not { } backup)
        {
            return string.Empty;
        }

        if (!ClipboardPasteService.TryRestore(
                backup,
                TimeSpan.FromMilliseconds(700),
                out var restoreError))
        {
            return $"原剪贴板恢复失败（{restoreError}），结果仍在剪贴板";
        }

        _clipboardBackup = null;
        _clipboardBackupAttempted = false;
        _ourClipboardData = null;
        _ourClipboardSequence = 0;
        return "原剪贴板已恢复";
    }

    private string BuildClipboardNote()
    {
        if (_keepClipboard)
        {
            return " 已主动复制结果，关闭时不会恢复原剪贴板";
        }

        if (!string.IsNullOrWhiteSpace(_clipboardWarning))
        {
            return " " + _clipboardWarning;
        }

        return " 关闭时会尝试恢复原剪贴板";
    }

    /// <summary>
    /// 插入后尝试把紧凑窗口带回前台，让 Enter/Esc 继续有效。
    /// 系统拒绝前台切换时返回 false；此时目标保持前台，不会自动关闭窗口。
    /// </summary>
    private async Task<bool> TryReturnToCompactAsync()
    {
        if (!IsVisible || !IsLoaded)
        {
            return false;
        }

        Activate();

        var handle = new WindowInteropHelper(this).Handle;

        if (handle == IntPtr.Zero)
        {
            return false;
        }

        var brought = ForegroundWindowService.TryBringToForeground(handle);

        if (!brought)
        {
            await Task.Delay(80);
            brought = ForegroundWindowService.TryBringToForeground(handle);
        }

        await Task.Delay(60);

        var isForeground = ForegroundWindowService.IsForeground(handle);
        _compactKeysUnavailable = !isForeground;

        if (isForeground)
        {
            if (BaseLetterBox.IsEnabled)
            {
                BaseLetterBox.Focus();
            }
            else
            {
                CancelButton.Focus();
            }
        }

        return isForeground;
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

        ThemeToggleButton.Content = dark ? "浅色" : "深色";
        ThemeToggleButton.ToolTip = dark
            ? "切换到日间模式"
            : "切换到深夜模式";
        AutomationProperties.SetName(
            ThemeToggleButton,
            dark ? "切换到日间模式" : "切换到深夜模式");
    }

    private void SwitchToEditorButton_Click(object sender, RoutedEventArgs e)
        => RequestSwitchToEditor();

    private void RequestSwitchToEditor()
    {
        if (_busy)
        {
            return;
        }

        // 由主窗口负责关闭紧凑窗口、显示完整编辑器并接收当前输入。
        SwitchToMainRequested?.Invoke(this, EventArgs.Empty);
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_busy)
        {
            Close();
        }
    }

    private void SetStatus(string message, bool isError = false, string? details = null)
    {
        StatusTextBlock.Text = message;
        SetForeground(StatusTextBlock, isError ? "ErrorBrush" : "MutedBrush");

        // 状态行只显示一句话；完整细节放到悬停提示里，需要时仍可查看。
        StatusTextBlock.ToolTip = string.IsNullOrWhiteSpace(details)
            ? (string.IsNullOrWhiteSpace(message) ? null : message)
            : details;
    }

    // 使用动态资源引用，切换主题时已经在显示的文字颜色也会自动更新。
    private static void SetForeground(TextBlock element, string resourceKey)
        => element.SetResourceReference(TextBlock.ForegroundProperty, resourceKey);

    private static string FormatCodePoint(int value) => $"U+{value:X4}";

    private static string FormatCodePoints(IEnumerable<int> values)
        => string.Join(" ", values.Select(FormatCodePoint));

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);

        // 用户点击窗口或按全局快捷键切回后，Esc/Enter 重新可用。
        if (_compactKeysUnavailable)
        {
            _compactKeysUnavailable = false;
            UpdateActionState();
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // 插入流程是异步的。忙碌时暂时拒绝关闭，避免在写剪贴板/发送按键的
        // 中间状态恢复剪贴板，或让异步续体访问已经关闭的窗口。
        if (_busy)
        {
            e.Cancel = true;
            return;
        }

        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        ThemeManager.ThemeChanged -= ThemeManager_ThemeChanged;

        // 关闭时只在剪贴板仍是我们写入的结果时恢复原内容。
        // 用户/其他程序期间复制了新内容时，绝不能覆盖它们。
        if (!_keepClipboard
            && _clipboardBackup is { } backup
            && ClipboardPasteService.IsResultStillCurrent(
                _ourClipboardSequence,
                _ourClipboardData))
        {
            ClipboardPasteService.TryRestore(
                backup,
                TimeSpan.FromMilliseconds(700),
                out _);
        }

        // 取消/关闭后尽力把输入焦点还给原应用；目标已关闭时安全放弃。
        // 切换到完整编辑器时由主窗口接管焦点，不能把前台还给目标应用。
        if (!_suppressTargetFocusRestore)
        {
            ForegroundWindowService.TryRestoreTargetFocus(_target);
        }

        base.OnClosed(e);
    }

    private enum ToolPanelMode
    {
        None,
        Operations,
        OtherLetters
    }
}
