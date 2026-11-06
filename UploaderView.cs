using System.Drawing.Drawing2D;

/// <summary>
/// The whole window content, painted by hand: one progress card
/// (headline, speed, current item, progress bar, time/items remaining, speed graph,
/// pause and cancel buttons). <see cref="MainForm"/> sets the text and values.
/// </summary>
internal sealed class UploaderView : Control
{
    private static readonly Color Background = Color.FromArgb(0xF4, 0xF7, 0xFB);
    private static readonly Color Accent = Color.FromArgb(0x1F, 0x6F, 0xEB);
    private static readonly Color CardBorder = Color.FromArgb(0xE1, 0xE7, 0xF0);
    private static readonly Color TextStrong = Color.FromArgb(0x17, 0x1F, 0x2E);
    private static readonly Color TextNormal = Color.FromArgb(0x2D, 0x37, 0x48);
    private static readonly Color TextMuted = Color.FromArgb(0x55, 0x60, 0x70);
    private static readonly Color Track = Color.FromArgb(0xE4, 0xEA, 0xF2);
    private static readonly Color GraphFill = Color.FromArgb(0xFA, 0xFC, 0xFF);
    private static readonly Color GridLine = Color.FromArgb(0xEA, 0xEF, 0xF5);
    private static readonly Color SpeedGreen = Color.FromArgb(0x1F, 0x9D, 0x55);
    private static readonly Color DisabledFill = Color.FromArgb(0xEC, 0xEF, 0xF3);
    private static readonly Color DisabledIcon = Color.FromArgb(0xA6, 0xB0, 0xBE);

    public static readonly Color HeadlineNormal = TextStrong;
    public static readonly Color BarNormal = Accent;
    public static readonly Color Success = Color.FromArgb(0x1F, 0x9D, 0x55);
    public static readonly Color Warning = Color.FromArgb(0xC2, 0x5E, 0x10);
    public static readonly Color Error = Color.FromArgb(0xD6, 0x45, 0x45);

    private readonly Font headlineFont = new("Segoe UI Semibold", 17f);
    private readonly Font bodyFont = new("Segoe UI", 12f);
    private readonly Font smallFont = new("Segoe UI", 10f);
    private readonly Font axisFont = new("Segoe UI", 9f);
    private readonly ToolTip toolTip = new();

    private Rectangle settingsRect, pauseRect, cancelRect;
    private int hover; // 0 none, 1 pause, 2 cancel, 3 settings
    private string? toolTipShown;

    public UploaderView()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Background;
    }

    public event EventHandler? PauseClicked;
    public event EventHandler? CancelClicked;
    public event EventHandler? SettingsClicked;

    public bool DragActive { get; set; }
    public string Headline { get; set; } = "Ready";
    public Color HeadlineColor { get; set; } = HeadlineNormal;
    public string SpeedText { get; set; } = string.Empty;
    public string Line1 { get; set; } = string.Empty;
    public string Line2 { get; set; } = string.Empty;
    public double Progress { get; set; }
    public Color BarColor { get; set; } = BarNormal;
    public string FooterLeft { get; set; } = string.Empty;
    public string FooterRight { get; set; } = string.Empty;
    public bool PauseEnabled { get; set; }
    public bool Paused { get; set; }
    public bool CancelEnabled { get; set; }

    /// <summary>Speed history in bytes per second, oldest first.</summary>
    public IReadOnlyList<double> SpeedSamples { get; set; } = Array.Empty<double>();

    public int SampleCapacity { get; set; } = 120;

    private float DpiScale => DeviceDpi / 96f;
    private int S(float value) => (int)Math.Round(value * DpiScale);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            headlineFont.Dispose();
            bodyFont.Dispose();
            smallFont.Dispose();
            axisFont.Dispose();
            toolTip.Dispose();
        }
        base.Dispose(disposing);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;

        var pad = S(16);
        PaintCard(g, new Rectangle(pad, pad, Width - 2 * pad, Height - 2 * pad));
    }

    private void PaintCard(Graphics g, Rectangle card)
    {
        using (var path = RoundedRect(card, S(12)))
        {
            using var fill = new SolidBrush(Color.White);
            g.FillPath(fill, path);
            using var pen = DragActive ? new Pen(Accent, S(2)) : new Pen(CardBorder, Math.Max(1f, DpiScale));
            g.DrawPath(pen, path);
        }

        var x = card.X + S(22);
        var right = card.Right - S(22);
        var width = right - x;

        // Headline + speed.
        var speedWidth = 0;
        if (SpeedText.Length > 0)
        {
            speedWidth = TextRenderer.MeasureText(g, SpeedText, bodyFont, Size.Empty, TextFormatFlags.NoPadding).Width;
            var speedRect = new Rectangle(right - speedWidth, card.Y + S(18), speedWidth, S(32));
            TextRenderer.DrawText(g, SpeedText, bodyFont, speedRect, TextNormal, TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            PaintUpArrow(g, speedRect.X - S(18), speedRect.Y + speedRect.Height / 2);
            speedWidth += S(28);
        }
        DrawLine(g, Headline, headlineFont, HeadlineColor, new Rectangle(x, card.Y + S(14), width - speedWidth, S(40)));

        DrawLine(g, Line1, bodyFont, TextNormal, new Rectangle(x, card.Y + S(58), width, S(28)));
        DrawLine(g, Line2, smallFont, TextMuted, new Rectangle(x, card.Y + S(88), width, S(22)));

        // Progress bar.
        var bar = new Rectangle(x, card.Y + S(118), width, S(14));
        using (var track = RoundedRect(bar, bar.Height / 2))
        using (var brush = new SolidBrush(Track))
            g.FillPath(brush, track);
        if (Progress > 0)
        {
            var filled = new Rectangle(bar.X, bar.Y, Math.Max(bar.Height, (int)(bar.Width * Math.Clamp(Progress, 0, 1))), bar.Height);
            using var path = RoundedRect(filled, bar.Height / 2);
            using var brush = new SolidBrush(BarColor);
            g.FillPath(brush, path);
        }

        // Footer texts.
        var footer = new Rectangle(x, card.Y + S(142), width, S(26));
        var rightWidth = TextRenderer.MeasureText(g, FooterRight, smallFont, Size.Empty, TextFormatFlags.NoPadding).Width;
        DrawLine(g, FooterLeft, smallFont, TextNormal, new Rectangle(footer.X, footer.Y, footer.Width - rightWidth - S(12), footer.Height));
        TextRenderer.DrawText(g, FooterRight, smallFont, footer, TextNormal,
            TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);

        // Buttons (bottom right), graph above them.
        var d = S(46);
        cancelRect = new Rectangle(right - d, card.Bottom - S(14) - d, d, d);
        pauseRect = new Rectangle(cancelRect.X - S(14) - d, cancelRect.Y, d, d);
        settingsRect = new Rectangle(x, cancelRect.Y, d, d);
        var graphTop = card.Y + S(176);
        var graph = new Rectangle(x, graphTop, width - S(80), Math.Max(S(40), cancelRect.Y - S(10) - graphTop));
        PaintGraph(g, graph);
        PaintPauseButton(g, pauseRect);
        PaintCancelButton(g, cancelRect);
        PaintSettingsButton(g, settingsRect);
    }

    private void PaintSettingsButton(Graphics g, Rectangle r)
    {
        var fill = hover == 3 ? Color.FromArgb(0xE0, 0xE6, 0xEE) : Color.FromArgb(0xEE, 0xF2, 0xF7);
        var icon = Color.FromArgb(0x4A, 0x55, 0x68);
        using (var brush = new SolidBrush(fill)) g.FillEllipse(brush, r);

        // Gear: 8 teeth around a ring.
        var cx = r.X + r.Width / 2f;
        var cy = r.Y + r.Height / 2f;
        var ring = r.Width * 0.17f;
        var tooth = r.Width * 0.26f;
        using var teeth = new Pen(icon, r.Width * 0.11f) { StartCap = LineCap.Flat, EndCap = LineCap.Flat };
        for (var i = 0; i < 8; i++)
        {
            var a = i * Math.PI / 4;
            g.DrawLine(teeth, cx, cy, cx + (float)Math.Cos(a) * tooth, cy + (float)Math.Sin(a) * tooth);
        }
        using (var body = new SolidBrush(icon)) g.FillEllipse(body, cx - ring - r.Width * 0.03f, cy - ring - r.Width * 0.03f, 2 * (ring + r.Width * 0.03f), 2 * (ring + r.Width * 0.03f));
        using (var hole = new SolidBrush(fill)) g.FillEllipse(hole, cx - r.Width * 0.08f, cy - r.Width * 0.08f, r.Width * 0.16f, r.Width * 0.16f);
    }

    private void PaintUpArrow(Graphics g, int x, int centerY)
    {
        using var pen = new Pen(SpeedGreen, Math.Max(2f, 2.4f * DpiScale)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        var h = S(14);
        var cx = x + S(6);
        g.DrawLine(pen, cx, centerY + h / 2, cx, centerY - h / 2);
        g.DrawLines(pen, new[] { new Point(cx - S(6), centerY - h / 2 + S(6)), new Point(cx, centerY - h / 2), new Point(cx + S(6), centerY - h / 2 + S(6)) });
    }

    private void PaintGraph(Graphics g, Rectangle r)
    {
        using (var path = RoundedRect(r, S(6)))
        {
            using var fill = new SolidBrush(GraphFill);
            g.FillPath(fill, path);
            using var pen = new Pen(CardBorder, Math.Max(1f, DpiScale));
            g.DrawPath(pen, path);
        }

        var plot = Rectangle.Inflate(r, -S(6), -S(8));
        var max = NiceMax(SpeedSamples.Count == 0 ? 0 : SpeedSamples.Max());
        var unit = max >= 1024 * 1024 ? (1024d * 1024, "MB/s") : (1024d, "KB/s");

        using (var grid = new Pen(GridLine, Math.Max(1f, DpiScale)))
        {
            for (var i = 0; i <= 2; i++)
            {
                var y = plot.Bottom - plot.Height * i / 2;
                if (i > 0) g.DrawLine(grid, plot.X, y, plot.Right, y);
                var value = max * i / 2 / unit.Item1;
                var label = $"{value:0.#} {unit.Item2}";
                var labelRect = new Rectangle(r.Right + S(10), y - S(10), S(80), S(20));
                TextRenderer.DrawText(g, label, axisFont, labelRect, TextMuted, TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }
        }

        var samples = SpeedSamples;
        if (samples.Count < 2)
            return;

        var capacity = Math.Max(SampleCapacity, samples.Count);
        var step = plot.Width / (float)(capacity - 1);
        var points = new PointF[samples.Count];
        for (var i = 0; i < samples.Count; i++)
        {
            var y = plot.Bottom - (float)(Math.Clamp(samples[i] / max, 0, 1) * plot.Height);
            points[i] = new PointF(plot.X + i * step, y);
        }

        using (var area = new GraphicsPath())
        {
            area.AddLines(points);
            area.AddLine(points[^1], new PointF(points[^1].X, plot.Bottom));
            area.AddLine(new PointF(points[^1].X, plot.Bottom), new PointF(points[0].X, plot.Bottom));
            area.CloseFigure();
            using var brush = new LinearGradientBrush(new Rectangle(plot.X, plot.Y - 1, plot.Width, plot.Height + 2),
                Color.FromArgb(80, Accent), Color.FromArgb(8, Accent), LinearGradientMode.Vertical);
            g.FillPath(brush, area);
        }

        using var line = new Pen(Accent, Math.Max(1.5f, 2f * DpiScale)) { LineJoin = LineJoin.Round };
        g.DrawLines(line, points);
    }

    private void PaintPauseButton(Graphics g, Rectangle r)
    {
        var fill = !PauseEnabled ? DisabledFill : hover == 1 ? Color.FromArgb(0xD2, 0xE0, 0xF7) : Color.FromArgb(0xE2, 0xEB, 0xFA);
        var icon = PauseEnabled ? Color.FromArgb(0x1E, 0x35, 0x5E) : DisabledIcon;
        using (var brush = new SolidBrush(fill)) g.FillEllipse(brush, r);

        using var iconBrush = new SolidBrush(icon);
        var cx = r.X + r.Width / 2f;
        var cy = r.Y + r.Height / 2f;
        if (Paused)
        {
            var size = r.Width * 0.34f;
            g.FillPolygon(iconBrush, new[]
            {
                new PointF(cx - size * 0.38f, cy - size / 2), new PointF(cx + size * 0.62f, cy), new PointF(cx - size * 0.38f, cy + size / 2),
            });
        }
        else
        {
            var barW = r.Width * 0.11f;
            var barH = r.Height * 0.34f;
            var gap = r.Width * 0.09f;
            g.FillRectangle(iconBrush, cx - gap / 2 - barW, cy - barH / 2, barW, barH);
            g.FillRectangle(iconBrush, cx + gap / 2, cy - barH / 2, barW, barH);
        }
    }

    private void PaintCancelButton(Graphics g, Rectangle r)
    {
        var fill = !CancelEnabled ? DisabledFill : hover == 2 ? Color.FromArgb(0xFA, 0xCF, 0xCF) : Color.FromArgb(0xFC, 0xE1, 0xE1);
        var icon = CancelEnabled ? Color.FromArgb(0xE0, 0x3E, 0x3E) : DisabledIcon;
        using (var brush = new SolidBrush(fill)) g.FillEllipse(brush, r);

        using var pen = new Pen(icon, Math.Max(2f, 2.6f * DpiScale)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        var half = r.Width * 0.15f;
        var cx = r.X + r.Width / 2f;
        var cy = r.Y + r.Height / 2f;
        g.DrawLine(pen, cx - half, cy - half, cx + half, cy + half);
        g.DrawLine(pen, cx + half, cy - half, cx - half, cy + half);
    }

    private static void DrawLine(Graphics g, string text, Font font, Color color, Rectangle bounds) =>
        TextRenderer.DrawText(g, text, font, bounds, color,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis |
            TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);

    /// <summary>Rounds the top of the graph up to 1, 2 or 5 × a power of ten (minimum 100 KB/s).</summary>
    private static double NiceMax(double value)
    {
        value = Math.Max(value * 1.15, 100 * 1024);
        var mb = 1024d * 1024;
        var unit = value >= mb ? mb : 1024d;
        var scaled = value / unit;
        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(scaled)));
        var normalized = scaled / magnitude;
        var nice = normalized <= 1 ? 1 : normalized <= 2 ? 2 : normalized <= 5 ? 5 : 10;
        return nice * magnitude * unit;
    }

    private static GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        var path = new GraphicsPath();
        var d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        if (d <= 0)
        {
            path.AddRectangle(r);
            return path;
        }
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static bool InCircle(Rectangle r, Point p)
    {
        var dx = p.X - (r.X + r.Width / 2.0);
        var dy = p.Y - (r.Y + r.Height / 2.0);
        return dx * dx + dy * dy <= r.Width * r.Width / 4.0;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var now = InCircle(pauseRect, e.Location) && PauseEnabled ? 1
            : InCircle(cancelRect, e.Location) && CancelEnabled ? 2
            : InCircle(settingsRect, e.Location) ? 3 : 0;
        if (now != hover)
        {
            hover = now;
            Cursor = hover == 0 ? Cursors.Default : Cursors.Hand;
            Invalidate();
        }

        var tip = hover == 1 ? (Paused ? "Resume" : "Pause") : hover == 2 ? "Cancel all" : hover == 3 ? "Settings (CNC IP and folder)" : null;
        if (tip != toolTipShown)
        {
            toolTipShown = tip;
            toolTip.SetToolTip(this, tip ?? string.Empty);
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (hover != 0)
        {
            hover = 0;
            Cursor = Cursors.Default;
            Invalidate();
        }
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Button != MouseButtons.Left)
            return;
        if (PauseEnabled && InCircle(pauseRect, e.Location))
            PauseClicked?.Invoke(this, EventArgs.Empty);
        else if (CancelEnabled && InCircle(cancelRect, e.Location))
            CancelClicked?.Invoke(this, EventArgs.Empty);
        else if (InCircle(settingsRect, e.Location))
            SettingsClicked?.Invoke(this, EventArgs.Empty);
    }
}
