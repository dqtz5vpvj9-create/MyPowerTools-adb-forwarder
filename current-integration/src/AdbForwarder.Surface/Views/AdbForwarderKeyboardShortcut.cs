using Avalonia.Input;

namespace AdbForwarder.Surface.Views;

public enum AdbForwarderKeyboardAction
{
    None,
    Refresh,
    RunPrimaryAction,
    CancelRunningAction
}

public static class AdbForwarderKeyboardShortcut
{
    public static AdbForwarderKeyboardAction Resolve(Key key, KeyModifiers modifiers)
    {
        if (key == Key.F5 && modifiers == KeyModifiers.None)
        {
            return AdbForwarderKeyboardAction.Refresh;
        }

        if (key == Key.Enter && modifiers == KeyModifiers.Control)
        {
            return AdbForwarderKeyboardAction.RunPrimaryAction;
        }

        if (key == Key.Escape && modifiers == KeyModifiers.None)
        {
            return AdbForwarderKeyboardAction.CancelRunningAction;
        }

        return AdbForwarderKeyboardAction.None;
    }
}
