using System;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace VoiceNoteTranscriber;

/// <summary>
/// Draws the tray icon: a speech bubble with a little waveform in it, colored by state.
/// Gray when idle, gold while transcribing, green when a transcript is waiting to be read.
/// Solid fill and a dark waveform, so it reads at 16px on any taskbar.
/// </summary>
public static class TrayIconRenderer
{
    public static Color StateColor(string state) => state switch
    {
        "working" => Color.FromArgb(255, 200, 40),  // gold
        "ready" => Color.FromArgb(0, 255, 127),     // spring green
        "error" => Color.FromArgb(220, 20, 60),     // crimson
        _ => Color.FromArgb(200, 200, 200),         // idle
    };

    /// <summary>Geometry is laid out on a 64px grid and scaled to size.</summary>
    public static Bitmap Draw(string state, int size = 64)
    {
        var bitmap = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);
        g.ScaleTransform(size / 64f, size / 64f);

        // Bubble: a rounded body with a tail at the bottom left.
        using var bubble = new GraphicsPath();
        const float left = 3, top = 6, right = 61, bottom = 46, r = 12;
        bubble.AddArc(left, top, 2 * r, 2 * r, 180, 90);
        bubble.AddArc(right - 2 * r, top, 2 * r, 2 * r, 270, 90);
        bubble.AddArc(right - 2 * r, bottom - 2 * r, 2 * r, 2 * r, 0, 90);
        bubble.AddLine(26, bottom, 12, 60);
        bubble.AddLine(14, bottom, 14, bottom);
        bubble.AddArc(left, bottom - 2 * r, 2 * r, 2 * r, 90, 90);
        bubble.CloseFigure();
        using (var fill = new SolidBrush(StateColor(state)))
            g.FillPath(fill, bubble);

        // Waveform: five bars, tallest in the middle.
        using var bar = new SolidBrush(Color.FromArgb(28, 28, 28));
        float[] heights = [10, 20, 28, 20, 10];
        const float barWidth = 6, gap = 4, mid = (top + bottom) / 2;
        float x = (left + right) / 2 - (heights.Length * barWidth + (heights.Length - 1) * gap) / 2;
        foreach (float h in heights)
        {
            g.FillRectangle(bar, x, mid - h / 2, barWidth, h);
            x += barWidth + gap;
        }
        return bitmap;
    }

    /// <summary>
    /// A tray-sized Icon. The caller owns it and must Dispose it; the
    /// underlying HICON is destroyed then too.
    /// </summary>
    public static Icon DrawIcon(string state, int size)
    {
        using var bitmap = Draw(state, size);
        IntPtr handle = bitmap.GetHicon();
        using var temp = Icon.FromHandle(handle);
        var icon = (Icon)temp.Clone(); // owns its own copy of the handle
        Native.DestroyIcon(handle);
        return icon;
    }
}
