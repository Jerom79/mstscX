namespace mstscX.Views;

internal sealed class ThemePreview(bool dark) : IDrawable
{
    public bool IsDark { get; set; } = dark;

    public void Draw(ICanvas canvas, RectF bounds)
    {
        canvas.SaveState();
        canvas.Translate(bounds.Center.X, bounds.Center.Y);
        float scale = Math.Min(bounds.Width, bounds.Height) / 88f;
        canvas.Scale(scale, scale);
        const float x = 0;
        const float y = 0;
        const float radius = 40;
        canvas.FillColor = Color.FromArgb(IsDark ? "#273C5B" : "#DEEAFE");
        canvas.FillCircle(x, y, radius);
        canvas.FillColor = Color.FromArgb(IsDark ? "#151F32" : "#FFFFFF");
        canvas.FillRoundedRectangle(x - 30, y - 22, 60, 44, 6);
        canvas.FillColor = Color.FromArgb(IsDark ? "#35496A" : "#E8F0FC");
        canvas.FillRoundedRectangle(x - 26, y - 17, 16, 34, 3);
        canvas.FillColor = Color.FromArgb(IsDark ? "#A5CAFF" : "#2459A0");
        canvas.FillRoundedRectangle(x - 5, y - 15, 24, 5, 2);
        canvas.FillColor = Color.FromArgb(IsDark ? "#35496A" : "#DFE9FA");
        canvas.FillRoundedRectangle(x - 5, y - 5, 30, 9, 2);
        canvas.FillRoundedRectangle(x - 5, y + 8, 30, 9, 2);
        canvas.RestoreState();
    }
}
