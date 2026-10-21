using System.Text;
using AccentIt.Models;
using AccentIt.Services;

namespace AccentIt.Tests;

public class MarkLayoutTests
{
    [Fact]
    public void Create_GroupsDefaultCatalogAroundPreview()
    {
        var layout = MarkLayout.Create(TestCatalog.LoadDefault());

        Assert.Equal(14, layout.Above.Count);
        Assert.Equal(9, layout.Below.Count);
        Assert.Equal(3, layout.Left.Count);
        Assert.Equal(2, layout.Right.Count);

        Assert.All(layout.Above, mark => Assert.Equal("above", mark.PositionId));
        Assert.All(layout.Below, mark => Assert.Equal("below", mark.PositionId));

        Assert.Equal(
            new[] { "overlay-tilde", "overlay-long-stroke", "overlay-long-slash" },
            layout.Left.Select(mark => mark.Id));
        Assert.Equal(
            new[] { "overlay-short-stroke", "overlay-short-slash" },
            layout.Right.Select(mark => mark.Id));
    }

    [Fact]
    public void Create_MapsExplicitSidesAndBalancesUnknownPositions()
    {
        var positions = new[]
        {
            new AccentPosition("top", "上方"),
            new AccentPosition("below", "下方"),
            new AccentPosition("left", "左侧"),
            new AccentPosition("right", "右侧"),
            new AccentPosition("misc", "其他")
        };

        var marks = new[]
        {
            new AccentMark("top-mark", new Rune(0x0301), "上方符", "top"),
            new AccentMark("below-mark", new Rune(0x0302), "下方符", "below"),
            new AccentMark("left-mark", new Rune(0x0303), "左侧符", "left"),
            new AccentMark("right-mark", new Rune(0x0304), "右侧符", "right"),
            new AccentMark("misc-one", new Rune(0x0305), "其他符一", "misc"),
            new AccentMark("misc-two", new Rune(0x0306), "其他符二", "misc")
        };

        var catalog = new AccentCatalog(1, positions, marks, CompositionMode.Nfc);
        var layout = MarkLayout.Create(catalog);

        Assert.Equal(new[] { "top-mark" }, layout.Above.Select(mark => mark.Id));
        Assert.Equal(new[] { "below-mark" }, layout.Below.Select(mark => mark.Id));
        Assert.Equal(new[] { "left-mark", "misc-one" }, layout.Left.Select(mark => mark.Id));
        Assert.Equal(new[] { "right-mark", "misc-two" }, layout.Right.Select(mark => mark.Id));
    }

    [Fact]
    public void Create_WithNoMarks_ReturnsEmptyGroups()
    {
        var catalog = new AccentCatalog(
            1,
            new[] { new AccentPosition("above", "上方") },
            Array.Empty<AccentMark>(),
            CompositionMode.Nfc);

        var layout = MarkLayout.Create(catalog);

        Assert.Empty(layout.Above);
        Assert.Empty(layout.Below);
        Assert.Empty(layout.Left);
        Assert.Empty(layout.Right);
    }
}
