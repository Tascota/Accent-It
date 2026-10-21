using AccentIt.Models;

namespace AccentIt.Services;

/// <summary>紧凑窗口四边的符号分组。</summary>
public sealed record MarkLayoutGroups(
    IReadOnlyList<AccentMark> Above,
    IReadOnlyList<AccentMark> Below,
    IReadOnlyList<AccentMark> Left,
    IReadOnlyList<AccentMark> Right);

/// <summary>
/// 把符号按位置 id 分到预览字母的四边。位置 id 由用户在 accents.jsonc
/// 中定义，因此这里是约定而不是硬编码符号：
/// above/top → 上方，below/bottom/under → 下方，left/right → 左右，
/// 其他位置（包括默认的 overlay）在左右之间交替分配。
/// </summary>
public static class MarkLayout
{
    public static MarkLayoutGroups Create(AccentCatalog catalog)
    {
        var above = new List<AccentMark>();
        var below = new List<AccentMark>();
        var left = new List<AccentMark>();
        var right = new List<AccentMark>();
        var sideMarks = new List<AccentMark>();

        foreach (var mark in catalog.Marks)
        {
            switch (mark.PositionId.ToLowerInvariant())
            {
                case "above":
                case "top":
                    above.Add(mark);
                    break;

                case "below":
                case "bottom":
                case "under":
                    below.Add(mark);
                    break;

                case "left":
                    left.Add(mark);
                    break;

                case "right":
                    right.Add(mark);
                    break;

                default:
                    // overlay 等未指定方向的位置放在左右两侧，保持默认配置
                    // 的叠加符号不会挤在字母上方或下方。
                    sideMarks.Add(mark);
                    break;
            }
        }

        // 按数量较少的一侧优先分配，避免左右明显不平衡。
        foreach (var mark in sideMarks)
        {
            if (left.Count <= right.Count)
            {
                left.Add(mark);
            }
            else
            {
                right.Add(mark);
            }
        }

        return new MarkLayoutGroups(above, below, left, right);
    }
}
