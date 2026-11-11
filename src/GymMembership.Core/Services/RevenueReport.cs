using System.Globalization;
using GymMembership.Core.Models;
using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;

namespace GymMembership.Core.Services;

/// <summary>One receipt, or one approved refund (negative), from revenue_lines (0027). They add up to revenue_summary.</summary>
public sealed record RevenueLine(DateTime At, string? Receipt, string Payer, string Item, string Kind, string Method, decimal Amount, bool Refund, long Payment);

/// <summary>The platform side of an export: the bundled font (it has the peso sign) and the system share sheet.</summary>
public interface IReportFiles
{
    Task<byte[]> FontAsync();
    Task ShareAsync(string fileName, byte[] pdf);
}

/// <summary>The revenue report as an A4 PDF: summary, then every line with the header repeated on each page.</summary>
public static class RevenuePdf
{
    static readonly CultureInfo En = CultureInfo.InvariantCulture;
    public static string Money(decimal v) => (v < 0 ? "-₱" : "₱") + Math.Abs(v).ToString("N2", En);
    public static string Manila(DateTime utc) => utc.AddHours(8).ToString("d MMM yyyy, HH:mm", En);   // Manila has no daylight saving
    public static string Range(DateOnly from, DateOnly to) => $"{from.ToString("d MMM yyyy", En)} to {to.ToString("d MMM yyyy", En)} (Asia/Manila)";

    // PDFsharp needs font bytes on Android (no system fonts), and the resolver is process-wide, so it is set once
    sealed class OneFont(byte[] data) : IFontResolver
    {
        public FontResolverInfo ResolveTypeface(string familyName, bool isBold, bool isItalic) => new("Report", isBold, isItalic);
        public byte[] GetFont(string faceName) => data;
    }
    static readonly object Gate = new();
    static void UseFont(byte[] font) { lock (Gate) GlobalFontSettings.FontResolver ??= new OneFont(font); }

    const double Left = 40, Top = 40, Bottom = 802, RowHeight = 14;
    static readonly (string Title, double Width, bool Right)[] Columns =
        [("Date and time", 82, false), ("Receipt", 72, false), ("Paid by", 118, false), ("Item", 113, false), ("Method", 55, false), ("Amount", 75, true)];

    public static byte[] Build(DateOnly from, DateOnly to, IReadOnlyList<RevenueLine> lines, DateTime generatedUtc, byte[] font)
    {
        var receipts = lines.Where(l => !l.Refund).ToList(); var refunds = lines.Where(l => l.Refund).ToList();
        return Document("Revenue report", "Revenue report " + Range(from, to), font,
            [(Range(from, to), Kind.Text), ($"Generated {Manila(generatedUtc)} (Asia/Manila)", Kind.Text),
             ("Verified payments on the day they were verified, minus approved refunds on the day they were approved.", Kind.Body),
             ("Voided payments are left out.", Kind.Body), ("", Kind.Gap),
             ($"Receipts: {receipts.Count}, {Money(receipts.Sum(l => l.Amount))}", Kind.Text),
             ($"Refunds: {refunds.Count}, {Money(refunds.Sum(l => l.Amount))}", Kind.Text),
             ($"Net: {Money(lines.Sum(l => l.Amount))}", Kind.Strong)],
            Columns, lines.Select(l => new[] { Manila(l.At), l.Receipt ?? "", l.Payer, l.Refund ? "Refund: " + l.Item : l.Item, PayMethod.Name(l.Method), Money(l.Amount) }).ToList(),
            ["", "", "", "", "Net", Money(lines.Sum(l => l.Amount))], "No receipts or approved refunds in this period.", []);
    }

    internal enum Kind { Text, Body, Strong, Gap }

    /// <summary>A titled A4 report: intro lines, a table whose header repeats on each page, an optional total row, then notes.</summary>
    internal static byte[] Document(string heading, string docTitle, byte[] font, IReadOnlyList<(string Text, Kind Kind)> intro,
        (string Title, double Width, bool Right)[] columns, IReadOnlyList<string[]> rows, string[]? total, string empty, IReadOnlyList<string> notes)
    {
        UseFont(font);
        var unicode = new XPdfFontOptions(PdfFontEncoding.Unicode);
        XFont F(double size, bool bold = false) => new("Report", size, bold ? XFontStyleEx.Bold : XFontStyleEx.Regular, unicode);
        XFont body = F(8.5), head = F(8.5, true), title = F(16, true), text = F(9.5), strong = F(9.5, true);

        var doc = new PdfDocument();
        doc.Info.Title = docTitle;
        XGraphics g = null!; double y = 0;

        void Cell(string s, XFont f, double x, double width, bool right)
        {
            while (s.Length > 1 && g.MeasureString(s, f).Width > width - 4) s = s[..^2] + "…";
            g.DrawString(s, f, XBrushes.Black, new XRect(x, y, width - 4, RowHeight), right ? XStringFormats.TopRight : XStringFormats.TopLeft);
        }
        void Row(string[] cells, XFont f)
        {
            var x = Left;
            for (var i = 0; i < columns.Length; i++) { Cell(cells[i], f, x, columns[i].Width, columns[i].Right); x += columns[i].Width; }
            y += RowHeight;
        }
        void Header()
        {
            Row(columns.Select(c => c.Title).ToArray(), head);
            g.DrawLine(XPens.Gray, Left, y - 2, Left + columns.Sum(c => c.Width), y - 2);
            y += 2;
        }
        void NewPage()
        {
            g?.Dispose();
            g = XGraphics.FromPdfPage(doc.AddPage());
            y = Top;
        }
        void Text(string s, XFont f) { if (y + 15 > Bottom) NewPage(); g.DrawString(s, f, XBrushes.Black, Left, y + 10); y += 15; }

        NewPage();
        g.DrawString(heading, title, XBrushes.Black, Left, y + 16); y += 28;
        foreach (var (line, kind) in intro)
            if (kind == Kind.Gap) y += 6; else Text(line, kind switch { Kind.Body => body, Kind.Strong => strong, _ => text });
        y += 10;

        if (rows.Count == 0) Text(empty, text);
        else
        {
            Header();
            foreach (var r in rows)
            {
                if (y + RowHeight > Bottom) { NewPage(); Header(); }
                Row(r, body);
            }
            if (total is not null)
            {
                if (y + RowHeight > Bottom) { NewPage(); Header(); }
                Row(total, head);
            }
        }
        if (notes.Count > 0) { y += 10; Text("Notes", strong); foreach (var n in notes) Text(n, body); }
        g.Dispose();

        for (var i = 0; i < doc.PageCount; i++)
        {
            using var footer = XGraphics.FromPdfPage(doc.Pages[i], XGraphicsPdfPageOptions.Append);
            footer.DrawString($"Page {i + 1} of {doc.PageCount}", body, XBrushes.Gray, new XRect(Left, Bottom + 8, 515, RowHeight), XStringFormats.TopRight);
        }
        using var ms = new MemoryStream();
        doc.Save(ms, false);
        return ms.ToArray();
    }
}

/// <summary>F2: the printable summary of drawer shifts for a range, for Admin and System admin (Revenue page).</summary>
public static class CashShiftPdf
{
    static readonly (string Title, double Width, bool Right)[] Columns =
        [("Cashier", 80, false), ("Shift", 105, false), ("Float", 52, true), ("Cash taken", 52, true), ("Paid back", 52, true),
         ("Expected", 58, true), ("Counted", 58, true), ("Over / short", 58, true)];

    static string Span(CashShift s) => RevenuePdf.Manila(s.OpenedAt) + (s.ClosedAt is { } c ? " to " + c.AddHours(8).ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture) : ", open");

    public static byte[] Build(DateOnly from, DateOnly to, IReadOnlyList<CashShift> shifts, DateTime generatedUtc, byte[] font)
    {
        var closed = shifts.Where(s => s.ClosedAt is not null).ToList();
        var off = closed.Where(s => s.Difference is not 0m).ToList();
        return RevenuePdf.Document("Cash shifts", "Cash shifts " + RevenuePdf.Range(from, to), font,
            [(RevenuePdf.Range(from, to), RevenuePdf.Kind.Text), ($"Generated {RevenuePdf.Manila(generatedUtc)} (Asia/Manila)", RevenuePdf.Kind.Text),
             ("Shifts opened in this period. Expected cash is the float plus cash taken minus cash paid back; open shifts show it so far.", RevenuePdf.Kind.Body),
             ("", RevenuePdf.Kind.Gap),
             ($"Shifts: {shifts.Count}, {closed.Count} ended", RevenuePdf.Kind.Text),
             ($"Over or short: {off.Count}, net {RevenuePdf.Money(off.Sum(s => s.Difference ?? 0))}", RevenuePdf.Kind.Strong)],
            Columns, shifts.Select(s => new[] { s.Cashier ?? "", Span(s), RevenuePdf.Money(s.OpeningFloat), RevenuePdf.Money(s.CashIn), RevenuePdf.Money(s.CashOut),
                RevenuePdf.Money(s.Expected), s.Counted is { } c ? RevenuePdf.Money(c) : "", s.IsOpen ? "" : s.DifferenceText }).ToList(),
            null, "No cash shifts in this period.",
            shifts.SelectMany(s => new[] { (s, "Opened", s.OpenNote), (s, "Ended", s.CloseNote) }).Where(n => n.Item3 is not null)
                .Select(n => $"{n.s.Cashier}, {RevenuePdf.Manila(n.s.OpenedAt)}. {n.Item2}: {n.Item3}").ToList());
    }
}
