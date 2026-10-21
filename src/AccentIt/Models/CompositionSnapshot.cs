namespace AccentIt.Models;

/// <summary>
/// 在主窗口和紧凑窗口之间切换时携带的输入状态。
/// 只保存文本和符号 id，不引用任何窗口控件。
/// </summary>
public sealed record CompositionSnapshot(
    string BaseLetterText,
    IReadOnlyList<string> MarkIds,
    CompositionMode OutputMode);
