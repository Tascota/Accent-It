namespace AccentIt.Models;

/// <summary>
/// 在主窗口和紧凑窗口之间切换时携带的输入状态。
/// 只保存文本、符号 id 和变形 id，不引用任何窗口控件。
/// 后两个参数为可选，旧调用方仍可使用原来的三个参数。
/// </summary>
public sealed record CompositionSnapshot(
    string BaseLetterText,
    IReadOnlyList<string> MarkIds,
    CompositionMode OutputMode,
    IReadOnlyList<string>? OperationIds = null,
    string? SecondLetterText = null);
