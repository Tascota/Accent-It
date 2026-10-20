using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using AccentIt.Models;
using AccentIt.Services;

namespace AccentIt;

public partial class MainWindow : Window
{
    private readonly List<string> _selectedMarkIds = new();
    private readonly Dictionary<string, ToggleButton> _markButtons = new(StringComparer.Ordinal);
    private AccentCatalog? _catalog;
    private CatalogLoadResult? _loadResult;
    private CompositionResult? _lastResult;
    private CompositionMode _outputMode = CompositionMode.Nfc;
    private bool _uiReady;
    private bool _updatingMarkButtons;
    private bool _updatingOutputMode;

    public MainWindow()
    {
        InitializeComponent();
        FitWindowToWorkArea();
        LoadCatalog();

        _uiReady = true;
        BaseLetterBox.Text = "a";
        BaseLetterBox.SelectAll();
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
        _markButtons.Clear();
        MarkGroupsPanel.Children.Clear();

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

        if (_catalog is not null)
        {
            BuildMarkGroups();
        }

        UpdateConfigUi();
        UpdateSelectionUi();
        UpdatePreview();
    }

    private void UpdateConfigUi()
    {
        var result = _loadResult;
        var issues = result?.Issues ?? Array.Empty<CatalogIssue>();
        var failed = result?.Catalog is null;
        var fallback = result?.UsedFallback == true;

        BaseLetterBox.IsEnabled = !failed;
        NfcOutputRadio.IsEnabled = !failed;
        NfdOutputRadio.IsEnabled = !failed;

        if (failed)
        {
            ConfigStatusTextBlock.Text = "配置加载失败。请滚动到“诊断详情”查看原因。";
            ConfigStatusTextBlock.Foreground = GetBrush("ErrorBrush");
        }
        else if (fallback)
        {
            ConfigStatusTextBlock.Text = "用户配置 accents.jsonc 有问题，已临时使用默认配置；详情见“诊断详情”。";
            ConfigStatusTextBlock.Foreground = GetBrush("ErrorBrush");
        }
        else
        {
            var sourcePath = result?.SourcePath;
            var fileName = string.IsNullOrWhiteSpace(sourcePath)
                ? "accents.jsonc"
                : Path.GetFileName(sourcePath);

            ConfigStatusTextBlock.Text = $"配置已加载：{fileName}";
            ConfigStatusTextBlock.Foreground = GetBrush("MutedBrush");
        }

        ConfigStatusTextBlock.ToolTip = result?.SourcePath;
        ConfigPathTextBlock.Text = string.IsNullOrWhiteSpace(result?.SourcePath)
            ? "未确定"
            : result.SourcePath;
        ConfigPathTextBlock.ToolTip = ConfigPathTextBlock.Text;

        if (issues.Count == 0)
        {
            IssuesTextBlock.Text = "没有配置问题。";
            IssuesTextBlock.Foreground = GetBrush("MutedBrush");
        }
        else
        {
            IssuesTextBlock.Text = string.Join(
                Environment.NewLine + Environment.NewLine,
                issues.Select(issue => "• " + issue));
            IssuesTextBlock.Foreground = GetBrush("ErrorBrush");
        }

        DiagnosticsExpander.Header = issues.Count == 0
            ? "诊断详情"
            : $"诊断详情（{issues.Count} 条配置提示）";
        DiagnosticsExpander.IsExpanded = failed || issues.Count > 0;
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
            MarkGroupsPanel.Children.Add(new TextBlock
            {
                Text = "符号目录未加载。",
                Foreground = GetBrush("ErrorBrush")
            });
            return;
        }

        foreach (var position in _catalog.Positions)
        {
            var marks = _catalog.GetMarksForPosition(position.Id);
            var group = new StackPanel
            {
                Margin = new Thickness(0, 0, 0, 18)
            };

            group.Children.Add(new TextBlock
            {
                Text = position.DisplayName,
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Foreground = GetBrush("InkBrush"),
                Margin = new Thickness(0, 0, 0, 8)
            });

            if (marks.Count == 0)
            {
                group.Children.Add(new TextBlock
                {
                    Text = "这个位置还没有配置符号。",
                    FontSize = 11,
                    FontStyle = FontStyles.Italic,
                    Foreground = GetBrush("MutedBrush")
                });
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
            Foreground = GetBrush("InkBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 30
        };

        var name = new TextBlock
        {
            Text = mark.Name,
            FontSize = 12,
            Foreground = GetBrush("InkBrush"),
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 160
        };

        var codePoint = new TextBlock
        {
            Text = FormatCodePoint(mark.Symbol.Value),
            FontFamily = new FontFamily("Consolas"),
            FontSize = 10,
            Foreground = GetBrush("MutedBrush")
        };

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

        toolTip += "\n\n点击选择或取消；Tab 聚焦后按空格也可以切换。";

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
        if (_selectedMarkIds.Count == 0)
        {
            return;
        }

        _selectedMarkIds.RemoveAt(_selectedMarkIds.Count - 1);
        UpdateSelectionUi();
        UpdatePreview();
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedMarkIds.Count == 0)
        {
            return;
        }

        _selectedMarkIds.Clear();
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

        SelectedMarksPanel.Children.Clear();

        if (_catalog is null)
        {
            UndoButton.IsEnabled = false;
            ClearButton.IsEnabled = false;
            return;
        }

        if (_selectedMarkIds.Count == 0)
        {
            SelectedHeaderTextBlock.Text = "已选符号（按选择顺序）";
            SelectedMarksPanel.Children.Add(new TextBlock
            {
                Text = "尚未选择符号。",
                FontSize = 11,
                FontStyle = FontStyles.Italic,
                Foreground = GetBrush("MutedBrush"),
                Margin = new Thickness(0, 2, 0, 0)
            });
        }
        else
        {
            SelectedHeaderTextBlock.Text = $"已选符号（{_selectedMarkIds.Count} 个，按选择顺序）";

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

        var hasSelection = _selectedMarkIds.Count > 0;
        UndoButton.IsEnabled = hasSelection;
        ClearButton.IsEnabled = hasSelection;
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
            InputHintTextBlock.Foreground = GetBrush("ErrorBrush");
            ShowPlaceholder("符号目录未加载", "请查看诊断详情中的配置问题。", isError: true);
            return;
        }

        if (!UnicodeComposer.TryParseBaseLetter(BaseLetterBox.Text, out var baseLetter, out var inputError))
        {
            InputHintTextBlock.Text = inputError;
            InputHintTextBlock.Foreground = GetBrush("ErrorBrush");
            ShowPlaceholder("等待有效的基础字母", inputError, isError: true);
            return;
        }

        InputHintTextBlock.Text = "输入单个 Unicode 字母";
        InputHintTextBlock.Foreground = GetBrush("MutedBrush");

        var result = UnicodeComposer.Compose(baseLetter, _selectedMarkIds, _catalog, _outputMode);

        if (!result.IsValid)
        {
            ShowPlaceholder("无法组合", result.Error ?? "组合失败。", isError: true);
            return;
        }

        _lastResult = result;
        PreviewTextBlock.Text = result.Output;
        OutputKindTextBlock.Text = DescribeKind(result.Kind);
        OutputCodePointsTextBlock.Text = FormatCodePoints(result.CodePoints);
        RawTextBlock.Text = FormatCodePointsFromText(result.RawSequence);
        NfcTextBlock.Text = FormatCodePointsFromText(result.Nfc);
        NfdTextBlock.Text = FormatCodePointsFromText(result.Nfd);

        if (result.AppliedRule is { } rule)
        {
            AppliedRuleTextBlock.Text = "匹配规则：" + DescribeRule(rule);
            AppliedRuleTextBlock.Visibility = Visibility.Visible;
        }
        else
        {
            AppliedRuleTextBlock.Visibility = Visibility.Collapsed;
        }

        CopyButton.IsEnabled = true;
        SetStatus(_selectedMarkIds.Count == 0
            ? "已显示基础字母。选择符号后会立即更新。"
            : $"已选择 {_selectedMarkIds.Count} 个符号，预览已更新。");
    }

    private void ShowPlaceholder(string kind, string message, bool isError)
    {
        PreviewTextBlock.Text = "—";
        OutputKindTextBlock.Text = kind;
        OutputCodePointsTextBlock.Text = string.Empty;
        RawTextBlock.Text = "—";
        NfcTextBlock.Text = "—";
        NfdTextBlock.Text = "—";
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
            SetStatus($"已复制到剪贴板：{FormatCodePoints(result.CodePoints)}");
        }
        catch (Exception ex)
        {
            SetStatus($"复制失败：{ex.Message}", isError: true);
        }
    }

    private void SetStatus(string message, bool isError = false)
    {
        StatusTextBlock.Text = message;
        StatusTextBlock.Foreground = GetBrush(isError ? "ErrorBrush" : "MutedBrush");
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
