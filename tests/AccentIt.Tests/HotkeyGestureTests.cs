using AccentIt.Models;

namespace AccentIt.Tests;

public class HotkeyGestureTests
{
    [Theory]
    [InlineData("Ctrl+Alt+Space", 0x0003, 0x20u, "Ctrl+Alt+Space")]
    [InlineData("ctrl + shift + a", 0x0006, 0x41u, "Ctrl+Shift+A")]
    [InlineData("Alt+9", 0x0001, 0x39u, "Alt+9")]
    [InlineData("Win+F24", 0x0008, 0x87u, "Win+F24")]
    [InlineData("Control+Windows+F1", 0x000A, 0x70u, "Ctrl+Win+F1")]
    public void TryParseAcceptsSupportedGestures(
        string text,
        int modifierValue,
        uint virtualKey,
        string canonical)
    {
        Assert.True(HotkeyGesture.TryParse(text, out var gesture, out var error), error);
        Assert.NotNull(gesture);
        Assert.Equal((HotkeyModifiers)modifierValue, gesture!.Modifiers);
        Assert.Equal(virtualKey, gesture.VirtualKey);
        Assert.Equal(canonical, gesture.ToString());
    }

    [Fact]
    public void DefaultIsCtrlAltSpace()
    {
        Assert.Equal("Ctrl+Alt+Space", HotkeyGesture.Default.ToString());
        Assert.Equal(0x20u, HotkeyGesture.Default.VirtualKey);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("A")]
    [InlineData("Ctrl")]
    [InlineData("Ctrl+Shift")]
    [InlineData("Ctrl+Alt+A+B")]
    [InlineData("Ctrl+Ctrl+A")]
    [InlineData("Ctrl+Alt+F12")]
    [InlineData("Ctrl+Alt+Unknown")]
    [InlineData("Ctrl+Alt+")]
    [InlineData("Ctrl+Alt++A")]
    public void TryParseRejectsInvalidGestures(string text)
    {
        Assert.False(HotkeyGesture.TryParse(text, out var gesture, out var error));
        Assert.Null(gesture);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void TryParseRoundTripsCanonicalText()
    {
        Assert.True(HotkeyGesture.TryParse("shift+win+f8", out var gesture, out _));
        Assert.NotNull(gesture);

        Assert.True(HotkeyGesture.TryParse(gesture!.ToString(), out var roundTrip, out _));
        Assert.Equal(gesture, roundTrip);
    }

    [Fact]
    public void TryParseRejectsNull()
    {
        Assert.False(HotkeyGesture.TryParse(null, out _, out var error));
        Assert.Contains("不能为空", error);
    }
}
