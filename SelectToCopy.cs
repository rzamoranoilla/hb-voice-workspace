using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;

namespace HB;

/// <summary>
/// Text selected with the mouse anywhere in the app is copied to the clipboard as soon as the
/// button is released, so it can be pasted into another program without a copy step. The
/// terminal and the preview pages do the same on their side and send the text here.
/// </summary>
public static class SelectToCopy
{
    /// <summary>Hooks every text box of the app; called once at start-up.</summary>
    public static void Install()
    {
        EventManager.RegisterClassHandler(typeof(TextBoxBase), UIElement.PreviewMouseLeftButtonUpEvent,
            new MouseButtonEventHandler(OnMouseUp), handledEventsToo: true);
    }

    private static void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not TextBoxBase box) return;
        // After the text box has finished its own handling of the release (the selection is final then).
        box.Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            var text = box switch
            {
                TextBox t => t.SelectedText,
                RichTextBox r => r.Selection.Text,
                _ => "",
            };
            if (text.Length > 0) TryCopy(text);
        });
    }

    /// <summary>Puts the text on the clipboard; false when another program is holding it.</summary>
    public static bool TryCopy(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        try
        {
            Clipboard.SetDataObject(text, copy: true);
            return true;
        }
        catch (COMException)
        {
            return false;
        }
    }
}
