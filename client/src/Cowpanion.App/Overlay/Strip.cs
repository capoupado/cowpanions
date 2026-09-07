using Cowpanion.App.Interop;
using Cowpanion.Core.Simulation;

namespace Cowpanion.App.Overlay;

/// <summary>Everything that belongs to one monitor: window, simulator, renderers, chat host.</summary>
internal sealed class Strip
{
    public Strip(MonitorInfo monitor, OverlayWindow window, HerdSimulator simulator, HerdRenderer herd, BubbleRenderer bubbles, ChatInputHost chat)
    {
        Monitor = monitor;
        Window = window;
        Simulator = simulator;
        Herd = herd;
        Bubbles = bubbles;
        Chat = chat;
    }

    public MonitorInfo Monitor { get; }
    public OverlayWindow Window { get; }
    public HerdSimulator Simulator { get; }
    public HerdRenderer Herd { get; }
    public BubbleRenderer Bubbles { get; }
    public ChatInputHost Chat { get; }

    /// <summary>Converts a physical-pixel screen point to strip DIPs; false when the point is not over this strip's monitor.</summary>
    public bool TryToStripDips(int screenX, int screenY, out double xDips, out double yDips)
    {
        var bounds = Monitor.BoundsPx;
        xDips = 0;
        yDips = 0;
        if (!bounds.Contains(screenX, screenY))
        {
            return false;
        }
        var work = Monitor.WorkAreaPx;
        double scale = Monitor.Scale;
        xDips = (screenX - work.Left) / scale;
        double stripTopPx = work.Bottom - OverlayWindow.StripHeightDips * scale;
        yDips = (screenY - stripTopPx) / scale;
        return true;
    }
}
