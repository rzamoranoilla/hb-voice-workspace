using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace HB;

/// <summary>
/// Mouse-wheel scrolling for every ScrollViewer of the app (lists, editor, transcript…):
/// a notch of the wheel moves the content a few lines, and the movement is animated instead of
/// jumping there, which reads as slower and smoother. Install once at start-up.
/// </summary>
public static class SmoothScroll
{
    /// <summary>Height of a line of text for the purpose of the Windows "lines to scroll" setting.</summary>
    private const double LineHeight = 14;

    /// <summary>Time constant of the movement: most of the distance is covered in about this time.</summary>
    private static readonly TimeSpan Response = TimeSpan.FromMilliseconds(90);

    private static readonly ConditionalWeakTable<ScrollViewer, Motion> s_motions = new();

    // The bubbling event: the innermost ScrollViewer under the mouse gets it first, and the one
    // around it only when the inner one has nothing left to scroll. Registered on ScrollViewer,
    // this runs before its own wheel handling, which is skipped once the event is handled.
    public static void Install() =>
        EventManager.RegisterClassHandler(typeof(ScrollViewer), UIElement.MouseWheelEvent,
            new MouseWheelEventHandler(OnMouseWheel));

    private static void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || Keyboard.Modifiers != ModifierKeys.None) return; // Shift/Ctrl: horizontal scroll, zoom…
        var viewer = (ScrollViewer)sender;
        if (viewer.ScrollableHeight <= 0 || viewer.VerticalScrollBarVisibility == ScrollBarVisibility.Disabled)
            return; // nothing to scroll here: a parent may
        // Offsets are pixels when the viewer does not scroll a list item at a time (the lists of
        // the app set ScrollViewer.CanContentScroll="False" for that) and inside text boxes,
        // whose content scrolls by pixels anyway. Others (combo box drop-downs…) keep the
        // standard scrolling.
        if (viewer.CanContentScroll && viewer.TemplatedParent is not TextBoxBase) return;

        var motion = s_motions.GetValue(viewer, v => new Motion(v));
        double lines = SystemParameters.WheelScrollLines > 0 ? SystemParameters.WheelScrollLines : 3;
        double distance = -e.Delta / 120.0 * lines * LineHeight;
        if (!motion.Add(distance)) return; // already at the end in that direction: a parent may scroll
        e.Handled = true;
    }

    /// <summary>Moves one ScrollViewer towards a target offset, a little each frame.</summary>
    private sealed class Motion(ScrollViewer viewer)
    {
        private readonly Stopwatch _clock = new();
        private double _target;
        private bool _running;

        public bool Add(double distance)
        {
            double current = viewer.VerticalOffset;
            if (!_running) _target = current;
            double target = Math.Clamp(_target + distance, 0, viewer.ScrollableHeight);
            if (Math.Abs(target - current) < 0.5 && Math.Abs(target - _target) < 0.5) return false;
            _target = target;
            if (!_running)
            {
                _running = true;
                _clock.Restart();
                CompositionTarget.Rendering += OnFrame;
            }
            return true;
        }

        private void OnFrame(object? sender, EventArgs e)
        {
            double elapsed = _clock.Elapsed.TotalSeconds;
            _clock.Restart();
            double current = viewer.VerticalOffset;
            double remaining = _target - current;
            if (Math.Abs(remaining) < 0.5)
            {
                viewer.ScrollToVerticalOffset(_target);
                Stop();
                return;
            }
            // Exponential approach: the same fraction of the remaining distance each unit of time,
            // whatever the frame rate.
            double fraction = 1 - Math.Exp(-elapsed / Response.TotalSeconds);
            viewer.ScrollToVerticalOffset(current + remaining * fraction);
        }

        private void Stop()
        {
            _running = false;
            CompositionTarget.Rendering -= OnFrame;
        }
    }
}
