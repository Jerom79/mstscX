#if DEBUG
using System.Runtime.InteropServices;
using System.Text;

namespace mstscX.WinUI;

// Sample only summary statistics from the composited screen. No pixels are written to disk.
internal sealed class FullScreenVisualTrace
{
    private const int Columns = 12;
    private const int Rows = 8;
    private readonly nint host;
    private readonly nint fullscreen;
    private volatile bool windowed;
    private volatile bool stopped;

    internal FullScreenVisualTrace(nint host, nint fullscreen)
    {
        this.host = host;
        this.fullscreen = fullscreen;
        _ = Task.Factory.StartNew(Sample, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    internal void MarkWindowed() => windowed = true;
    internal void Stop() => stopped = true;

    private void Sample()
    {
        var output = new StringBuilder();
        nint screen = GetDC(0);
        if (screen == 0)
            return;

        nint memory = CreateCompatibleDC(screen);
        nint bitmap = CreateCompatibleBitmap(screen, Columns, Rows);
        nint previous = bitmap != 0 && memory != 0 ? SelectObject(memory, bitmap) : 0;
        try
        {
            if (previous is 0 or -1)
                return;

            SetStretchBltMode(memory, 3); // COLORONCOLOR
            long deadline = Environment.TickCount64 + 60_000;
            while (!stopped && Environment.TickCount64 < deadline)
            {
                bool useHost = windowed || fullscreen == 0;
                nint source = useHost ? host : fullscreen;
                long now = Environment.TickCount64;
                if (GetWindowRect(source, out var rect))
                {
                    int width = rect.Right - rect.Left;
                    int height = rect.Bottom - rect.Top;
                    int insetX = width / 10;
                    int insetY = height / 10;
                    if (width > 0 && height > 0 && StretchBlt(memory, 0, 0, Columns, Rows, screen,
                        rect.Left + insetX, rect.Top + insetY, width - 2 * insetX, height - 2 * insetY, 0x00CC0020))
                    {
                        int black = 0, total = 0;
                        for (int y = 0; y < Rows; y++)
                        for (int x = 0; x < Columns; x++)
                        {
                            uint pixel = GetPixel(memory, x, y);
                            int red = (int)(pixel & 255);
                            int green = (int)((pixel >> 8) & 255);
                            int blue = (int)((pixel >> 16) & 255);
                            if (Math.Max(red, Math.Max(green, blue)) < 16)
                                black++;
                            total += (54 * red + 183 * green + 19 * blue) >> 8;
                        }
                        output.AppendLine($"Frame @{now} {(useHost ? "windowed" : "fullscreen")} visible={(IsWindowVisible(source) ? 1 : 0)} " +
                            $"position={rect.Left},{rect.Top} black={black}/{Columns * Rows} brightness={total / (Columns * Rows)}");
                    }
                }
                Thread.Sleep(25);
            }
        }
        finally
        {
            if (previous is not (0 or -1))
                SelectObject(memory, previous);
            if (bitmap != 0)
                DeleteObject(bitmap);
            if (memory != 0)
                DeleteDC(memory);
            ReleaseDC(0, screen);
            if (output.Length > 0)
                FullScreenTransitionTrace.Append(output.ToString());
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] private static extern nint GetDC(nint window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint window, nint dc);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out Rect rect);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleBitmap(nint dc, int width, int height);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint value);
    [DllImport("gdi32.dll")] private static extern int SetStretchBltMode(nint dc, int mode);
    [DllImport("gdi32.dll")] private static extern uint GetPixel(nint dc, int x, int y);
    [DllImport("gdi32.dll")] private static extern bool StretchBlt(nint dc, int x, int y, int width, int height,
        nint source, int sourceX, int sourceY, int sourceWidth, int sourceHeight, uint operation);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint value);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint dc);
}
#endif
