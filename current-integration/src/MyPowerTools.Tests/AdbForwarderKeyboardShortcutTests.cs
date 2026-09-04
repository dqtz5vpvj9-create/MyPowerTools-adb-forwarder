using Avalonia.Input;
using AdbForwarder.Surface.Views;

namespace MyPowerTools.Tests;

public sealed class AdbForwarderKeyboardShortcutTests
{
    [Theory]
    [InlineData(Key.F5, KeyModifiers.None, AdbForwarderKeyboardAction.Refresh)]
    [InlineData(Key.Enter, KeyModifiers.Control, AdbForwarderKeyboardAction.RunPrimaryAction)]
    [InlineData(Key.Escape, KeyModifiers.None, AdbForwarderKeyboardAction.CancelRunningAction)]
    public void Resolves_supported_shortcuts(
        Key key,
        KeyModifiers modifiers,
        AdbForwarderKeyboardAction expected)
    {
        Assert.Equal(expected, AdbForwarderKeyboardShortcut.Resolve(key, modifiers));
    }

    [Fact]
    public void Leaves_plain_enter_to_the_focused_control()
    {
        Assert.Equal(
            AdbForwarderKeyboardAction.None,
            AdbForwarderKeyboardShortcut.Resolve(Key.Enter, KeyModifiers.None));
    }
}
