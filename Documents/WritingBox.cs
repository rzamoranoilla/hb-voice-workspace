using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace HB.Documents;

/// <summary>
/// The text editor of a document. Writing assistants such as Grammarly find text fields through
/// UI Automation (the Text pattern of a control that is in the control view); while
/// <see cref="AssistantsEnabled"/> is off the editor does not present itself there, so they
/// leave the writing alone.
/// </summary>
public sealed class WritingBox : TextBox
{
    public static readonly DependencyProperty AssistantsEnabledProperty = DependencyProperty.Register(
        nameof(AssistantsEnabled), typeof(bool), typeof(WritingBox),
        new PropertyMetadata(false, OnAssistantsEnabledChanged));

    /// <summary>Whether writing assistants (Grammarly and the like) may see and check the text.</summary>
    public bool AssistantsEnabled
    {
        get => (bool)GetValue(AssistantsEnabledProperty);
        set => SetValue(AssistantsEnabledProperty, value);
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new WritingBoxAutomationPeer(this);

    private static void OnAssistantsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var box = (WritingBox)d;
        if (UIElementAutomationPeer.FromElement(box) is WritingBoxAutomationPeer peer)
            peer.AnnounceChange((bool)e.OldValue, (bool)e.NewValue);

        // Assistants look at a field when it receives the focus: let it receive the focus again.
        if (!box.IsKeyboardFocused) return;
        Keyboard.ClearFocus();
        box.Dispatcher.BeginInvoke(DispatcherPriority.Input, () => box.Focus());
    }

    private sealed class WritingBoxAutomationPeer(WritingBox owner) : TextBoxAutomationPeer(owner)
    {
        private bool Exposed => ((WritingBox)Owner).AssistantsEnabled;

        // Hidden from the control and content views, without the Text (or any) pattern, and
        // marked as a password field: the three signals assistants use to skip a field.
        protected override bool IsControlElementCore() => Exposed && base.IsControlElementCore();
        protected override bool IsContentElementCore() => Exposed && base.IsContentElementCore();
        protected override bool IsPasswordCore() => !Exposed || base.IsPasswordCore();
        public override object? GetPattern(PatternInterface patternInterface) =>
            Exposed ? base.GetPattern(patternInterface) : null;

        public void AnnounceChange(bool was, bool now)
        {
            RaisePropertyChangedEvent(AutomationElementIdentifiers.IsControlElementProperty, was, now);
            RaisePropertyChangedEvent(AutomationElementIdentifiers.IsContentElementProperty, was, now);
            RaisePropertyChangedEvent(AutomationElementIdentifiers.IsPasswordProperty, !was, !now);
        }
    }
}
