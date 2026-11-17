using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using FanCtl.Shared;

namespace FanCtl.UI;

/// <summary>Immediate-mode curve editor: one OnRender, no child visuals, redraws only on change.</summary>
public sealed class CurveEditor : FrameworkElement
{
    const float TMin = 20, TMax = 100;
    const double PadL = 40, PadR = 14, PadT = 12, PadB = 28, Hit = 11;

    // Palette rebuilt from the Windows theme whenever it changes.
    static Brush Accent = null!, Label = null!, Ink = null!, KnobFill = null!, TipBg = null!, Area = null!, Floor = null!;
    static Pen TipPen = null!, Grid = null!, Line = null!, KnobPen = null!, LivePen = null!;
    static readonly Typeface Face = new("Segoe UI");

    public static void RefreshPalette()
    {
        Color ac = Theme.Accent, fg = Theme.Fore;
        Accent = Frozen(new SolidColorBrush(ac));
        Label = Frozen(new SolidColorBrush(Alpha(fg, 0xA0)));
        Ink = Frozen(new SolidColorBrush(fg));
        KnobFill = Frozen(new SolidColorBrush(Theme.Light ? Colors.White : Color.FromRgb(0x24, 0x24, 0x24)));
        TipBg = Frozen(new SolidColorBrush(Theme.Light ? Color.FromArgb(0xF5, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0xF0, 0x2B, 0x2B, 0x2B)));
        TipPen = Frozen(new Pen(new SolidColorBrush(Alpha(fg, 0x18)), 1));
        Grid = Frozen(new Pen(new SolidColorBrush(Alpha(fg, Theme.Light ? (byte)0x1C : (byte)0x14)), 1));
        Line = Frozen(new Pen(Accent, 2) { LineJoin = PenLineJoin.Round });
        KnobPen = Frozen(new Pen(Accent, 2));
        LivePen = Frozen(new Pen(new SolidColorBrush(Alpha(fg, 0x80)), 1) { DashStyle = new DashStyle([3, 3], 0) });
        Floor = Frozen(new SolidColorBrush(Alpha(fg, 0x0C)));
        Area = Frozen(new LinearGradientBrush(Alpha(ac, Theme.Light ? (byte)0x38 : (byte)0x50), Alpha(ac, 0x00), 90));
    }

    static Color Alpha(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);
    static T Frozen<T>(T f) where T : Freezable { f.Freeze(); return f; }

    List<CurvePoint>? _pts;
    float _liveT = float.NaN, _liveD = float.NaN;
    int _hover = -1, _drag = -1;

    public event Action? Changed;

    public CurveEditor() { Focusable = false; }

    float _minDuty;
    /// <summary>Lowest duty the device accepts; the band below it is drawn as unreachable.</summary>
    public float MinDuty
    {
        get => _minDuty;
        set { _minDuty = Math.Clamp(value, 0, 100); InvalidateVisual(); }
    }

    public List<CurvePoint>? Points
    {
        get => _pts;
        set { _pts = value; _hover = _drag = -1; InvalidateVisual(); }
    }

    public void SetLive(float t, float d)
    {
        if (Same(t, _liveT) && Same(d, _liveD)) return;
        _liveT = t; _liveD = d;
        InvalidateVisual();
    }

    static bool Same(float a, float b) => (float.IsNaN(a) && float.IsNaN(b)) || MathF.Abs(a - b) < 0.05f;

    Rect Plot => new(PadL, PadT, Math.Max(1, ActualWidth - PadL - PadR), Math.Max(1, ActualHeight - PadT - PadB));

    Point ToScreen(float t, float d)
    {
        var r = Plot;
        return new Point(r.X + (t - TMin) / (TMax - TMin) * r.Width, r.Bottom - d / 100 * r.Height);
    }

    (float t, float d) FromScreen(Point p)
    {
        var r = Plot;
        float t = TMin + (float)((p.X - r.X) / r.Width) * (TMax - TMin);
        float d = (float)((r.Bottom - p.Y) / r.Height) * 100;
        return (t, d);
    }

    FormattedText Text(string s, double size, Brush b) =>
        new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, size, b,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);

    protected override void OnRender(DrawingContext dc)
    {
        var r = Plot;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize)); // hit-testable surface

        for (int t = 20; t <= 100; t += 10)
        {
            double x = Math.Round(ToScreen(t, 0).X) + 0.5;
            dc.DrawLine(Grid, new Point(x, r.Top), new Point(x, r.Bottom));
            var ft = Text($"{t}°", 11, Label);
            dc.DrawText(ft, new Point(x - ft.Width / 2, r.Bottom + 8));
        }
        for (int d = 0; d <= 100; d += 20)
        {
            double y = Math.Round(ToScreen(0, d).Y) + 0.5;
            dc.DrawLine(Grid, new Point(r.Left, y), new Point(r.Right, y));
            var ft = Text($"{d}%", 11, Label);
            dc.DrawText(ft, new Point(r.Left - ft.Width - 8, y - ft.Height / 2));
        }

        if (_minDuty > 0)
        {
            double ym = ToScreen(0, _minDuty).Y;
            dc.DrawRectangle(Floor, null, new Rect(r.Left, ym, r.Width, r.Bottom - ym));
            var ft = Text($"minimo {_minDuty:0}%", 10.5, Label);
            dc.DrawText(ft, new Point(r.Right - ft.Width - 6, ym + 3));
        }

        if (_pts is not { Count: > 0 } p) return;

        var line = new StreamGeometry();
        var fill = new StreamGeometry();
        using (var g = line.Open())
        using (var f = fill.Open())
        {
            var start = ToScreen(TMin, p[0].D);
            g.BeginFigure(start, false, false);
            f.BeginFigure(new Point(r.Left, r.Bottom), true, true);
            f.LineTo(start, false, false);
            foreach (var pt in p)
            {
                var s = ToScreen(Math.Clamp(pt.T, TMin, TMax), pt.D);
                g.LineTo(s, true, true);
                f.LineTo(s, false, false);
            }
            var end = ToScreen(TMax, p[^1].D);
            g.LineTo(end, true, true);
            f.LineTo(end, false, false);
            f.LineTo(new Point(r.Right, r.Bottom), false, false);
        }
        line.Freeze(); fill.Freeze();
        dc.DrawGeometry(Area, null, fill);
        dc.DrawGeometry(null, Line, line);

        // Live operating point
        if (!float.IsNaN(_liveT))
        {
            float lt = Math.Clamp(_liveT, TMin, TMax);
            float ld = float.IsNaN(_liveD) ? Curves.Eval(p, _liveT) : _liveD;
            var lp = ToScreen(lt, Math.Clamp(ld, 0, 100));
            dc.DrawLine(LivePen, new Point(lp.X, r.Bottom), lp);
            dc.DrawEllipse(Ink, null, lp, 4.5, 4.5);
            var ft = Text($"{_liveT:0}° → {ld:0}%", 11.5, Ink);
            double tx = Math.Min(lp.X + 8, r.Right - ft.Width);
            dc.DrawText(ft, new Point(tx, Math.Max(r.Top, lp.Y - ft.Height - 6)));
        }

        for (int i = 0; i < p.Count; i++)
        {
            var s = ToScreen(Math.Clamp(p[i].T, TMin, TMax), p[i].D);
            double rad = i == _hover || i == _drag ? 7 : 5;
            dc.DrawEllipse(KnobFill, KnobPen, s, rad, rad);
        }

        int k = _drag >= 0 ? _drag : _hover;
        if (k >= 0 && k < p.Count)
        {
            var s = ToScreen(Math.Clamp(p[k].T, TMin, TMax), p[k].D);
            var ft = Text($"{p[k].T:0}°  ·  {p[k].D:0}%", 12, Ink);
            var box = new Rect(s.X - ft.Width / 2 - 8, s.Y - ft.Height - 22, ft.Width + 16, ft.Height + 8);
            if (box.Top < 0) box.Y = s.Y + 14;
            box.X = Math.Clamp(box.X, 0, Math.Max(0, ActualWidth - box.Width));
            dc.DrawRoundedRectangle(TipBg, TipPen, box, 5, 5);
            dc.DrawText(ft, new Point(box.X + 8, box.Y + 4));
        }
    }

    int HitTest(Point m)
    {
        if (_pts == null) return -1;
        int best = -1; double bd = Hit * Hit;
        for (int i = 0; i < _pts.Count; i++)
        {
            var s = ToScreen(Math.Clamp(_pts[i].T, TMin, TMax), _pts[i].D);
            double dx = s.X - m.X, dy = s.Y - m.Y, d2 = dx * dx + dy * dy;
            if (d2 <= bd) { bd = d2; best = i; }
        }
        return best;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (_pts == null) return;
        var m = e.GetPosition(this);
        int h = HitTest(m);
        if (h >= 0)
        {
            _drag = h;
            CaptureMouse();
            e.Handled = true;
        }
        else if (e.ClickCount == 2)
        {
            var (t, d) = FromScreen(m);
            t = MathF.Round(Math.Clamp(t, TMin, TMax));
            d = MathF.Round(Math.Clamp(d, _minDuty, 100));
            foreach (var pt in _pts) if (MathF.Abs(pt.T - t) < 1) return;
            int idx = _pts.FindIndex(x => x.T > t);
            if (idx < 0) idx = _pts.Count;
            _pts.Insert(idx, new CurvePoint(t, d));
            _hover = idx;
            InvalidateVisual();
            Changed?.Invoke();
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_pts == null) return;
        var m = e.GetPosition(this);
        if (_drag >= 0)
        {
            var (t, d) = FromScreen(m);
            float lo = _drag > 0 ? _pts[_drag - 1].T + 1 : TMin;
            float hi = _drag < _pts.Count - 1 ? _pts[_drag + 1].T - 1 : TMax;
            var pt = _pts[_drag];
            pt.T = MathF.Round(Math.Clamp(t, lo, Math.Max(lo, hi)));
            pt.D = MathF.Round(Math.Clamp(d, _minDuty, 100));
            InvalidateVisual();
            return;
        }
        int h = HitTest(m);
        if (h != _hover)
        {
            _hover = h;
            Cursor = h >= 0 ? Cursors.Hand : null;
            InvalidateVisual();
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (_drag < 0) return;
        _drag = -1;
        ReleaseMouseCapture();
        InvalidateVisual();
        Changed?.Invoke();
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        if (_pts == null || _pts.Count <= 2) return;
        int h = HitTest(e.GetPosition(this));
        if (h < 0) return;
        _pts.RemoveAt(h);
        _hover = -1;
        InvalidateVisual();
        Changed?.Invoke();
        e.Handled = true;
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        if (_hover >= 0 && _drag < 0) { _hover = -1; InvalidateVisual(); }
    }
}
