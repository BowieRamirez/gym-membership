using System.Globalization;
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
    static string MethodName(string m) => m switch { "cash" => "Cash", "gcash" => "GCash", "qr" => "QR", "bank_transfer" => "Bank transfer", _ => m };

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
        UseFont(font);
        var unicode = new XPdfFontOptions(PdfFontEncoding.Unicode);
        XFont F(double size, bool bold = false) => new("Report", size, bold ? XFontStyleEx.Bold : XFontStyleEx.Regular, unicode);
        XFont body = F(8.5), head = F(8.5, true), title = F(16, true), text = F(9.5), strong = F(9.5, true);

        var doc = new PdfDocument();
        doc.Info.Title = "Revenue report " + Range(from, to);
        XGraphics g = null!; double y = 0;

        void Cell(string s, XFont f, double x, double width, bool right)
        {
            while (s.Length > 1 && g.MeasureString(s, f).Width > width - 4) s = s[..^2] + "…";
            g.DrawString(s, f, XBrushes.Black, new XRect(x, y, width - 4, RowHeight), right ? XStringFormats.TopRight : XStringFormats.TopLeft);
        }
        void Row(string[] cells, XFont f)
        {
            var x = Left;
            for (var i = 0; i < Columns.Length; i++) { Cell(cells[i], f, x, Columns[i].Width, Columns[i].Right); x += Columns[i].Width; }
            y += RowHeight;
        }
        void Header()
        {
            Row(Columns.Select(c => c.Title).ToArray(), head);
            g.DrawLine(XPens.Gray, Left, y - 2, Left + Columns.Sum(c => c.Width), y - 2);
            y += 2;
        }
        void NewPage()
        {
            g?.Dispose();
            g = XGraphics.FromPdfPage(doc.AddPage());
            y = Top;
        }

        NewPage();
        g.DrawString("Revenue report", title, XBrushes.Black, Left, y + 16); y += 28;
        void Text(string s, XFont f) { g.DrawString(s, f, XBrushes.Black, Left, y + 10); y += 15; }
        Text(Range(from, to), text);
        Text($"Generated {Manila(generatedUtc)} (Asia/Manila)", text);
        Text("Verified payments on the day they were verified, minus approved refunds on the day they were approved.", body);
        Text("Voided payments are left out.", body);
        y += 6;
        var receipts = lines.Where(l => !l.Refund).ToList(); var refunds = lines.Where(l => l.Refund).ToList();
        Text($"Receipts: {receipts.Count}, {Money(receipts.Sum(l => l.Amount))}", text);
        Text($"Refunds: {refunds.Count}, {Money(refunds.Sum(l => l.Amount))}", text);
        Text($"Net: {Money(lines.Sum(l => l.Amount))}", strong);
        y += 10;

        if (lines.Count == 0) Text("No receipts or approved refunds in this period.", text);
        else
        {
            Header();
            foreach (var l in lines)
            {
                if (y + RowHeight > Bottom) { NewPage(); Header(); }
                Row([Manila(l.At), l.Receipt ?? "", l.Payer, l.Refund ? "Refund: " + l.Item : l.Item, MethodName(l.Method), Money(l.Amount)], body);
            }
            if (y + RowHeight > Bottom) { NewPage(); Header(); }
            Row(["", "", "", "", "Net", Money(lines.Sum(l => l.Amount))], head);
        }
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
