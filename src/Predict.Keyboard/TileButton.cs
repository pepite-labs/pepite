using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Predict.Keyboard;

/// <summary>
/// The word button of a bar tile. Owner 2026-11-25 (« UIA Invoke »): a UI Automation <b>Invoke</b> (screen reader, voice
/// access, switch tools that go through UIA rather than a simulated mouse) is not a mouse click — it follows « Touches
/// F1–F8 », not « Clic sur un mot » (<see cref="ListenFirst.InvokeAction"/>). So the automation peer raises
/// <see cref="AutomationInvoked"/> instead of the button's <c>Click</c>: the mouse handler (and its double-click detection,
/// which reads the input message's own time and point) never sees an Invoke. With nobody listening (the settings preview,
/// the render sheets) an Invoke raises <c>Click</c> as a plain button does.
/// </summary>
internal sealed class TileButton : Button
{
    /// <summary>A UI Automation Invoke on this tile (UI thread, posted like WPF's own Invoke so the client never waits on
    /// the controller and nothing re-enters).</summary>
    public event EventHandler? AutomationInvoked;

    protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this);

    private void OnAutomationInvoke()
    {
        // nobody listening (settings preview, render sheets): a plain button's Invoke
        if (AutomationInvoked is not { } h) { OnClick(); return; }
        // the event a UIA client (Narrateur, Accès vocal) waits for — Button.OnClick raises it for a plain button, so here
        // only, where OnClick is not called
        if (AutomationPeer.ListenerExists(AutomationEvents.InvokePatternOnInvoked))
            UIElementAutomationPeer.FromElement(this)?.RaiseAutomationEvent(AutomationEvents.InvokePatternOnInvoked);
        h(this, EventArgs.Empty);
    }

    /// <summary>The button's peer with the Invoke pattern re-implemented (everything else — name, help text, item status —
    /// is <see cref="ButtonAutomationPeer"/>'s). Nothing here takes the focus. A client's Invoke or SetFocus may make the
    /// bar the foreground before this runs (BUG-1): the bar stays shown for the visibility check below because
    /// <see cref="KeyboardPolicy.ClassifyForeground"/> classes that change <c>Bar</c> (no hide); the give-back
    /// (<see cref="SuggestionWindow.GiveBackForeground"/>) is for the keys and the insertion target.</summary>
    private sealed class Peer : ButtonAutomationPeer, IInvokeProvider
    {
        public Peer(TileButton owner) : base(owner) { }

        void IInvokeProvider.Invoke()
        {
            if (!IsEnabled()) throw new ElementNotEnabledException();
            var b = (TileButton)Owner;
            if (!b.IsVisible) throw new ElementNotAvailableException();   // a hidden bar: its old words are not there
            b.Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(b.OnAutomationInvoke));
        }
    }
}
