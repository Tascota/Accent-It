using AccentIt.Models;
using AccentIt.Services;

namespace AccentIt.Tests;

public class PopupPlacementTests
{
    private static readonly PhysicalRect WorkArea = new(0, 0, 1000, 800);

    [Fact]
    public void ComputeForAnchor_PrefersBelowCaret()
    {
        var position = PopupPlacement.ComputeForAnchor(
            new PhysicalRect(100, 100, 120, 120),
            WorkArea,
            300,
            200);

        Assert.Equal(new PhysicalPoint(100, 132), position);
    }

    [Fact]
    public void ComputeForAnchor_FlipsAboveWhenBelowDoesNotFit()
    {
        var position = PopupPlacement.ComputeForAnchor(
            new PhysicalRect(100, 650, 120, 670),
            WorkArea,
            300,
            200);

        Assert.Equal(new PhysicalPoint(100, 438), position);
    }

    [Fact]
    public void ComputeForAnchor_ClampsIntoWorkArea()
    {
        var position = PopupPlacement.ComputeForAnchor(
            new PhysicalRect(950, 20, 990, 40),
            WorkArea,
            300,
            200);

        Assert.Equal(new PhysicalPoint(688, 52), position);
    }

    [Fact]
    public void ComputeCentered_CentersWindowInWorkArea()
    {
        var position = PopupPlacement.ComputeCentered(WorkArea, 300, 200);

        Assert.Equal(new PhysicalPoint(350, 300), position);
    }

    [Fact]
    public void ComputeNearPoint_FlipsAboveWhenBelowDoesNotFit()
    {
        var position = PopupPlacement.ComputeNearPoint(
            new PhysicalPoint(100, 700),
            WorkArea,
            300,
            200);

        Assert.Equal(new PhysicalPoint(112, 488), position);
    }

    [Fact]
    public void ComputeCorner_UsesBottomRightWithMargin()
    {
        var position = PopupPlacement.ComputeCorner(WorkArea, 300, 200);

        Assert.Equal(new PhysicalPoint(688, 588), position);
    }

    [Fact]
    public void PhysicalRect_RejectsInvalidUiaRectangles()
    {
        Assert.False(PhysicalRect.FromDoubles(double.NaN, 0, 10, 10).IsValid);
        Assert.False(PhysicalRect.FromDoubles(0, 0, 0, 10).IsValid);
        Assert.True(PhysicalRect.FromDoubles(10.2, 20.8, 30.4, 40.1).IsValid);
    }
}
