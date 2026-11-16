using System;
using System.Windows;

namespace Predict.Keyboard;

/// <summary>Opens the embedded help from the UI (tray « Aide », Réglages « ? » / F1, the welcome windows) and says so
/// when nothing could be opened. UI thread only; never called from the hook.</summary>
internal static class HelpUi
{
    public static void Open(HelpTopic topic, Window? owner, Action<string>? log)
    {
        if (HelpLinks.Open(topic, log)) return;
        if (owner != null && owner.IsVisible)
            MessageBox.Show(owner, Strings.HelpMissing, Strings.HelpTitle, MessageBoxButton.OK, MessageBoxImage.Information);
        else
            MessageBox.Show(Strings.HelpMissing, Strings.HelpTitle, MessageBoxButton.OK, MessageBoxImage.Information);
    }
}
