#if WINDOWS
using System.Globalization;
using GymMembership.Core.Services;

namespace GymMembership.App.Controls;

/// <summary>
/// The Windows Revenue page's day-by-day columns (mockup docs/mockups/revenue.html): net revenue or check-ins per day, today in yellow,
/// a dashed average, and the hovered day's figures in a dark label. Drawn, so no chart package is needed.
/// </summary>
public sealed class RevenueChart : GraphicsView, IDrawable
{
    static readonly CultureInfo En = CultureInfo.GetCultureInfo("en-PH");
    static readonly Microsoft.Maui.Graphics.Font Regular = new("Barlow"), Bold = new("BarlowSemiBold");
    IReadOnlyList<RevenueDay> _days = [];
    bool _visits;
    int _hover = -1;
    float _padL, _step;

    public RevenueChart()
    {
        Drawable = this;
        HeightRequest = 250;
        StartHoverInteraction += (_, e) => Hover(e.Touches[0].X);
        MoveHoverInteraction += (_, e) => Hover(e.Touches[0].X);
        EndHoverInteraction += (_, _) => { _hover = -1; Invalidate(); };
        if (Application.Current is { } app) app.RequestedThemeChanged += (_, _) => Invalidate();
    }

    public void Show(IReadOnlyList<RevenueDay> days, bool visits)
    {
        _days = days; _visits = visits; _hover = -1;
        SemanticProperties.SetDescription(this, (visits ? "Check-ins" : "Net revenue") + $" per day, {days.Count} days");
        Invalidate();
    }

    void Hover(float x)
    {
        var i = _step <= 0 ? -1 : (int)((x - _padL) / _step);
        i = i >= 0 && i < _days.Count ? i : -1;
        if (i != _hover) { _hover = i; Invalidate(); }
    }

    decimal Value(RevenueDay d) => _visits ? d.Visits : d.Net;
    string Label(decimal v) => _visits ? v.ToString("N0", En) : v >= 1000 || v <= -1000 ? $"₱{v / 1000:0.#}k" : RevenueDashboard.Peso(v);

    public void Draw(ICanvas canvas, RectF rect)
    {
        if (_days.Count == 0) return;
        var dark = Application.Current?.RequestedTheme == AppTheme.Dark;
        Color C(string light, string darkKey) => (Color)Application.Current!.Resources[dark ? darkKey : light];
        Color ink = C("InkLight", "InkDark"), muted = C("MutedLight", "MutedDark"), line = C("LineLight", "LineDark"), page = C("CardLight", "CardDark");
        Color bar = dark ? Color.FromArgb("#3A3A3A") : Color.FromArgb("#CFCFCF"), signal = (Color)Application.Current!.Resources["Signal"], bad = C("BadLight", "BadDark");

        const float padT = 22, padB = 26;
        var values = _days.Select(Value).ToList();
        // round guide lines: three steps of 1, 2, 2.5 or 5 times a power of ten reach the highest day (whole check-ins)
        var step = RevenueDashboard.NiceStep(Math.Max(values.Max(), 1));
        if (_visits) step = Math.Max(1, Math.Ceiling(step));
        decimal top = step * 3, low = values.Min() < 0 ? -step * Math.Ceiling(-values.Min() / step) : 0;
        canvas.Font = Regular;
        canvas.FontSize = 12;
        _padL = 8 + Math.Max(canvas.GetStringSize(Label(top), Regular, 12).Width, canvas.GetStringSize(Label(low), Regular, 12).Width) + 10;
        var plotH = rect.Height - padT - padB;
        float Y(decimal v) => padT + plotH * (float)((top - v) / (top - low));
        _step = (rect.Width - _padL) / _days.Count;
        var barW = Math.Clamp(_step * 0.62f, 3, 28);

        // four guide lines with their amounts
        canvas.StrokeSize = 1;
        for (var v = low; v <= top; v += step)
        {
            canvas.StrokeColor = line; canvas.DrawLine(_padL, Y(v), rect.Width, Y(v));
            canvas.FontColor = muted; canvas.DrawString(Label(v), 0, Y(v) - 8, _padL - 8, 16, HorizontalAlignment.Right, VerticalAlignment.Center);
        }

        // the columns; dates under every few
        var every = _days.Count <= 10 ? 1 : _days.Count <= 35 ? 5 : 14;
        for (var i = 0; i < _days.Count; i++)
        {
            var v = values[i]; var x = _padL + i * _step + (_step - barW) / 2;
            float y0 = Y(0), y1 = Y(v);
            canvas.FillColor = i == _hover ? ink : i == _days.Count - 1 && _days[i].Day.Date == DateTime.Today ? signal : v < 0 ? bad : bar;
            if (Math.Abs(y0 - y1) >= 1) canvas.FillRoundedRectangle(x, Math.Min(y0, y1), barW, Math.Abs(y0 - y1), Math.Min(4, barW / 3));
            if ((_days.Count - 1 - i) % every == 0)
            {
                canvas.FontColor = muted;
                var left = Math.Clamp(x + barW / 2 - 30, 0, rect.Width - 60);   // the last date stays inside the panel
                var align = left >= rect.Width - 60 ? HorizontalAlignment.Right : HorizontalAlignment.Center;
                canvas.DrawString(_days[i].Day.ToString("d MMM", En), left, rect.Height - padB + 6, 60, 16, align, VerticalAlignment.Top);
            }
        }

        // the average, dashed, labelled at the right end
        var avg = values.Average();
        canvas.StrokeColor = ink.WithAlpha(0.55f); canvas.StrokeDashPattern = [4, 4];
        canvas.DrawLine(_padL, Y(avg), rect.Width, Y(avg));
        canvas.StrokeDashPattern = null;
        canvas.FontColor = muted;
        canvas.DrawString("average " + (_visits ? avg.ToString("0.#", En) : RevenueDashboard.Peso(Math.Round(avg))), _padL, Y(avg) - 18, rect.Width - _padL, 16, HorizontalAlignment.Right, VerticalAlignment.Center);

        if (_hover >= 0) Tip(canvas, rect, _days[_hover], _padL + _hover * _step + _step / 2, Y(Math.Max(values[_hover], 0)), ink, page);
    }

    static void Tip(ICanvas canvas, RectF rect, RevenueDay d, float cx, float top, Color ink, Color page)
    {
        var lines = new List<string> { d.Day.ToString("ddd d MMM", En), $"Net {RevenueDashboard.Peso(d.Net)} · {d.Payments} {(d.Payments == 1 ? "payment" : "payments")}" };
        if (d.Refunds > 0) lines.Add($"Refunded {RevenueDashboard.Peso(d.Refunds)}");
        lines.Add($"{d.Visits} {(d.Visits == 1 ? "check-in" : "check-ins")}");
        canvas.FontSize = 13;
        var w = lines.Max(l => canvas.GetStringSize(l, Bold, 13).Width) + 22;
        var h = lines.Count * 18 + 14f;
        var x = Math.Clamp(cx - w / 2, 0, rect.Width - w);
        var y = Math.Max(0, top - h - 10);
        canvas.SetShadow(new SizeF(0, 4), 12, Colors.Black.WithAlpha(0.25f));
        canvas.FillColor = ink; canvas.FillRoundedRectangle(x, y, w, h, 10);
        canvas.SetShadow(SizeF.Zero, 0, Colors.Transparent);
        canvas.FontColor = page;
        for (var i = 0; i < lines.Count; i++)
        {
            canvas.Font = i == 0 ? Bold : Regular;
            canvas.DrawString(lines[i], x + 11, y + 7 + i * 18, w - 22, 18, HorizontalAlignment.Left, VerticalAlignment.Center);
        }
        canvas.Font = Regular;
    }
}
#endif
