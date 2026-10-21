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
    private readonly Dictionary<string, ToggleButton> _markButtons = new(StringComparer.Ordinal);
    private readonly AccentCatalog? _catalog;
    private readonly CatalogLoadResult? _loadResult;
    private readonly TargetContext? _target;
    private CompositionResult? _lastResult;
    private CompositionMode _outputMode = CompositionMode.Nfc;
    private bool _uiReady;
    private bool _updatingMarkButtons;
    private bool _updatingOutputMode;
    private bool _busy;
    private bool _hasInserted;

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
        CatalogLoadResult? loadResult,
        TargetContext? target,
        CompositionSnapshot? initial = null)
    {
        _catalog = catalog;
        _loadResult = loadResult;
        _target = target;

        InitializeComponent();

        _outputMode = _catalog?.CompositionMode ?? CompositionMode.Nfc;
        UpdateOutputModeUi();
        BuildMarkLayout();
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
            TargetHintTextBlock.Text = "符号目录加载失败，无法组合或插入。";
            return;
        }

        if (_target is null || !_target.HasUsableTarget)
        {
            TargetHintTextBlock.Text = _target is { IsExternal: false, WindowHandle: not 0 }
                ? "当前目标是 Accent It 自己：Enter 只复制结果，不会修改自身。"
                : "没有捕获到外部目标：Enter 只复制结果，不会修改其他应用。";
            return;
        }

        if (_target.IsPasswordField)
        {
            TargetHintTextBlock.Text = $"目标：{_target.DisplayName}（密码框，不会自动插入）";
            return;
        }

        var location = _target.CaretSource switch
        {
            CaretSource.Win32Caret => "已定位系统光标",
            CaretSource.UiaTextSelection => "已定位文本选区",
            CaretSource.UiaFocusBounds => "已定位输入框",
            _ => "未定位到光标，使用备用位置"
        };

        TargetHintTextBlock.Text = $"目标：{_target.DisplayName}（{location}）";
        TargetHintTextBlock.ToolTip = _target.IsPasswordField
            ? "检测到密码框，不会读取内容，也不会自动插入。"
            : "插入时会重新验证这个窗口和输入焦点是否仍然有效。";
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
        BaseLetterBox.Text = "a";
        BaseLetterBox.SelectAll();

        _outputMode = _catalog?.CompositionMode ?? CompositionMode.Nfc;
        UpdateOutputModeUi();
        UpdateSelectionUi();
        UpdatePreview();
    }

    /// <summary>读取当前输入，供“完整编辑器（F2）”切换时带过去。</summary>
    public CompositionSnapshot CreateSnapshot()
        => new(BaseLetterBox.Text, _selectedMarkIds.ToArray(), _outputMode);

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

            if (_catalog is not null)
            {
                foreach (var markId in snapshot.MarkIds.Distinct(StringComparer.Ordinal))
                {
                    if (_catalog.FindMark(markId) is not null)
                    {
                        _selectedMarkIds.Add(markId);
                    }
                }
            }

            BaseLetterBox.Text = snapshot.BaseLetterText ?? string.Empty;
            BaseLetterBox.SelectAll();

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
            BaseLetterBox.Text = string.Empty;
        }
        finally
        {
            _uiReady = true;
        }

        _lastResult = null;
        PreviewTextBlock.Text = "—";
        UpdateSelectionUi();
        UpdateActionState();
    }

    private void UpdateCatalogBanner()
    {
        var issues = _loadResult?.Issues ?? Array.Empty<CatalogIssue>();

        if (_catalog is null)
        {
            CatalogBanner.Visibility = Visibility.Visible;
            CatalogBanner.Background = GetBrush("ErrorSurfaceBrush");
            CatalogBanner.BorderBrush = GetBrush("ErrorBrush");
            CatalogBannerTextBlock.Foreground = GetBrush("ErrorBrush");
            CatalogBannerTextBlock.Text =
                "符号目录加载失败，无法组合。请回到完整编辑器查看“诊断详情”。";
            return;
        }

        if (_loadResult?.UsedFallback == true || issues.Count > 0)
        {
            CatalogBanner.Visibility = Visibility.Visible;
            CatalogBanner.Background = GetBrush("WarningSurfaceBrush");
            CatalogBanner.BorderBrush = GetBrush("WarningBorderBrush");
            CatalogBannerTextBlock.Foreground = GetBrush("InkBrush");
            CatalogBannerTextBlock.Text =
                "用户符号目录有问题，已临时使用默认配置。详情见完整编辑器的“诊断详情”。";
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
            AboveMarksPanel.Children.Add(new TextBlock
            {
                Text = "符号目录未加载。",
                FontSize = 10,
                FontStyle = FontStyles.Italic,
                Foreground = GetBrush("ErrorBrush"),
                Margin = new Thickness(2)
            });
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
            FontSize = 16,
            Foreground = GetBrush("InkBrush"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        var toolTip = $"{mark.Name}（{positionName}）\n{FormatCodePoint(mark.Symbol.Value)}";

        if (!string.IsNullOrWhiteSpace(mark.Description))
        {
            toolTip += $"\n{mark.Description}";
        }

        toolTip += "\n\n点击选择或取消；Backspace 删除最后一个符号。";

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
        // 紧凑窗口不再显示已选符号列表；按钮高亮和中央预览就是选中状态。
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
    }

    private void BaseLetterBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_uiReady)
        {
            return;
        }

        UpdatePreview();
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

    private void UpdatePreview()
    {
        _lastResult = null;

        if (_catalog is null)
        {
            PreviewTextBlock.Text = "—";
            SetStatus("符号目录加载失败，无法组合。", isError: true);
            UpdateActionState();
            return;
        }

        if (!UnicodeComposer.TryParseBaseLetter(BaseLetterBox.Text, out var baseLetter, out var inputError))
        {
            PreviewTextBlock.Text = "—";
            SetStatus(inputError, isError: true);
            UpdateActionState();
            return;
        }

        var result = UnicodeComposer.Compose(baseLetter, _selectedMarkIds, _catalog, _outputMode);

        if (!result.IsValid)
        {
            PreviewTextBlock.Text = "—";
            SetStatus(result.Error ?? "组合失败。", isError: true);
            UpdateActionState();
            return;
        }

        _lastResult = result;
        PreviewTextBlock.Text = result.Output;

        var enterHint = CanInsert ? "Enter 插入" : "Enter 复制";
        var selection = _selectedMarkIds.Count == 0
            ? "尚未选择符号"
            : $"已选 {_selectedMarkIds.Count} 个符号";

        SetStatus($"{DescribeKind(result.Kind)} · {selection} · {enterHint}。");

        UpdateActionState();
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
        SwitchToEditorButton.IsEnabled = !_busy;

        CopyResultButton.IsEnabled = hasResult && !_busy;
        InsertButton.IsEnabled = hasResult && CanInsert && !_busy;

        var showKeyHints = !_compactKeysUnavailable;
        InsertButton.Content = showKeyHints ? "插入（Enter）" : "插入";
        CancelButton.Content = _hasInserted
            ? (showKeyHints ? "关闭（Esc）" : "关闭")
            : (showKeyHints ? "取消（Esc）" : "取消");

        if (_busy)
        {
            ActionHintTextBlock.Text = "正在插入，请稍候…";
        }
        else if (_compactKeysUnavailable)
        {
            ActionHintTextBlock.Text = "窗口未在前台 · 按全局快捷键切回";
        }
        else if (!CanInsert)
        {
            ActionHintTextBlock.Text = "Enter 只复制";
        }
        else if (_hasInserted)
        {
            ActionHintTextBlock.Text = "可继续输入 · Enter 插入 · Esc 关闭";
        }
        else
        {
            ActionHintTextBlock.Text = "Enter 插入 · Esc 取消";
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
                RemoveLastMark();
                return;
        }
    }

    private void RemoveLastMark()
    {
        if (_selectedMarkIds.Count == 0)
        {
            SetStatus("没有可删除的符号。");
            return;
        }

        var markId = _selectedMarkIds[^1];
        _selectedMarkIds.RemoveAt(_selectedMarkIds.Count - 1);
        var mark = _catalog?.FindMark(markId);

        UpdateSelectionUi();
        UpdatePreview();
        SetStatus(mark is null ? "已删除最后一个符号。" : $"已删除：{mark.Name}。");
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
            $"已复制到剪贴板：{FormatCodePoints(result.CodePoints)}。关闭窗口不会恢复原剪贴板。");
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

        var inserted = false;

        if (!CanInsert)
        {
            SetStatus("没有可插入的外部目标，或目标是密码框；已改为复制结果。");
            CopyResult();
            return;
        }

        if (!ForegroundWindowService.TryValidate(_target, out var validationError))
        {
            SetStatus(
                $"无法插入：{validationError} 可直接“复制结果”后手动粘贴。",
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
                    $"无法写入剪贴板：{clipboardError} 目标没有被修改。",
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
                    $"无法插入：{activation.Message} 未发送粘贴。{restoreNote}可直接复制结果。",
                    isError: true);
                return;
            }

            if (!ClipboardPasteService.IsResultStillCurrent(
                    _ourClipboardSequence,
                    _ourClipboardData))
            {
                await TryReturnToCompactAsync();
                SetStatus(
                    "剪贴板在发送前被其他程序改写，已取消自动插入。请点“复制结果”后手动粘贴。",
                    isError: true);
                return;
            }

            if (!await ForegroundWindowService.WaitForModifiersReleasedAsync(
                    TimeSpan.FromMilliseconds(900)))
            {
                await TryReturnToCompactAsync();
                SetStatus(
                    "修饰键仍未松开，已取消自动插入。请松开按键后重试。",
                    isError: true);
                return;
            }

            if (!ForegroundWindowService.IsForeground(_target!.WindowHandle))
            {
                await TryReturnToCompactAsync();
                SetStatus(
                    "目标窗口在发送前失去前台，已取消自动插入。可复制结果后手动粘贴。",
                    isError: true);
                return;
            }

            if (!ClipboardPasteService.TrySendCtrlV(out var inputError))
            {
                await TryReturnToCompactAsync();
                SetStatus(
                    $"发送粘贴失败：{inputError} 可复制结果后手动粘贴。",
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
                : " 快速输入窗口未能自动回到前台；按全局快捷键切回后可继续输入。";
            var recoveryNote = " 若目标中没有出现文本，可在目标中手动按 Ctrl+V；剪贴板中仍保留本次结果。";

            SetStatus(
                (targetWasForeground
                    ? "已向目标发送 Ctrl+V，但无法确认目标是否真的显示文本。"
                    : "已尝试发送 Ctrl+V，但目标窗口未保持前台。")
                + " 可继续输入下一个字母；Esc 关闭。"
                + recoveryNote
                + visibilityNote
                + clipboardNote,
                isError: !targetWasForeground);
        }
        catch (Exception ex)
        {
            await TryReturnToCompactAsync();
            var recoveryNote = inserted
                ? " 剪贴板中仍保留本次结果，可在目标中手动按 Ctrl+V。"
                : " 可复制结果后手动粘贴。";
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
            return $"原剪贴板恢复失败（{restoreError}），结果仍在剪贴板。";
        }

        _clipboardBackup = null;
        _clipboardBackupAttempted = false;
        _ourClipboardData = null;
        _ourClipboardSequence = 0;
        return "原剪贴板已恢复。";
    }

    private string BuildClipboardNote()
    {
        if (_keepClipboard)
        {
            return " 你已主动复制结果，关闭时不会恢复原剪贴板。";
        }

        if (!string.IsNullOrWhiteSpace(_clipboardWarning))
        {
            return " " + _clipboardWarning;
        }

        return " 关闭时会尝试恢复原剪贴板。";
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

    private void SetStatus(string message, bool isError = false)
    {
        StatusTextBlock.Text = message;
        StatusTextBlock.Foreground = GetBrush(isError ? "ErrorBrush" : "MutedBrush");

        // 紧凑窗口能显示的状态行有限；把完整文字放到悬停提示里，避免长错误被截断后无法查看。
        StatusTextBlock.ToolTip = message;
    }

    private Brush GetBrush(string key) => (Brush)FindResource(key);

    private static string DescribeKind(CompositionKind kind) => kind switch
    {
        CompositionKind.BaseOnly => "仅基础字母（尚未添加符号）",
        CompositionKind.Precomposed => "完整预组合字符",
        CompositionKind.NfcSequence => "NFC 序列（包含部分预组合）",
        CompositionKind.NfdSequence => "NFD 序列（完全分解）",
        CompositionKind.SpecialRule => "显式例外规则",
        _ => "无效"
    };

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
}
