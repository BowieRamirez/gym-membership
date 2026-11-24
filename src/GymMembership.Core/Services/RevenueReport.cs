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
    /// <summary>Opens the PDF in the system's PDF viewer, to print (a booking receipt at the desk). MAUI has no print dialog of its own.</summary>
    Task OpenAsync(string fileName, byte[] pdf);
}

/// <summary>The revenue report as an A4 PDF: summary, then every line with the header repeated on each page.</summary>
public static class RevenuePdf
{
    static readonly CultureInfo En = CultureInfo.InvariantCulture;
    public static string Money(decimal v) => (v < 0 ? "-₱" : "₱") + Math.Abs(v).ToString("N2", En);
    public static string Manila(DateTime utc) => ManilaTime.Of(utc).ToString("d MMM yyyy, HH:mm", En);
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
        [("Cashier", 72, false), ("Shift", 95, false), ("Float", 48, true), ("Cash in", 48, true), ("Cash out", 48, true),
         ("Expected", 52, true), ("Counted", 52, true), ("Shortage", 50, true), ("Overage", 50, true)];

    static string Span(CashShift s) => RevenuePdf.Manila(s.OpenedAt) + (s.ClosedAt is { } c ? " to " + ManilaTime.Of(c).ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture) : ", open");

    public static byte[] Build(DateOnly from, DateOnly to, IReadOnlyList<CashShift> shifts, DateTime generatedUtc, byte[] font)
    {
        var closed = shifts.Where(s => s.ClosedAt is not null).ToList();
        var off = closed.Where(s => s.Difference is not 0m).ToList();
        var notes = shifts.SelectMany(s => new[]
        {
            s.OpenNote is null ? null : $"{s.Cashier}, {RevenuePdf.Manila(s.OpenedAt)}. Opened: {s.OpenNote}",
            s.CloseReasonText is { Length: > 0 } || s.CloseNote is not null
                ? $"{s.Cashier}, {RevenuePdf.Manila(s.OpenedAt)}. Ended{(s.CloseReasonText is { Length: > 0 } r ? $" ({r})" : "")}: {s.CloseNote ?? "–"}"
                : null
        }.Concat(s.Notes.Select(n => $"{s.Cashier}, {RevenuePdf.Manila(s.OpenedAt)}. Note: {n.Summary}"))).Where(x => x is not null).Cast<string>().ToList();

        return RevenuePdf.Document("Cash shifts", "Cash shifts " + RevenuePdf.Range(from, to), font,
            [(RevenuePdf.Range(from, to), RevenuePdf.Kind.Text), ($"Generated {RevenuePdf.Manila(generatedUtc)} (Asia/Manila)", RevenuePdf.Kind.Text),
             ("Shifts opened in this period. Expected cash is the float plus cash taken minus cash paid back; shortages and overages are kept apart from sales.", RevenuePdf.Kind.Body),
             ("", RevenuePdf.Kind.Gap),
             ($"Shifts: {shifts.Count}, {closed.Count} ended", RevenuePdf.Kind.Text),
             ($"Over or short: {off.Count} · Shortages: {RevenuePdf.Money(off.Sum(s => s.Shortage))} · Overages: {RevenuePdf.Money(off.Sum(s => s.Overage))}", RevenuePdf.Kind.Strong)],
            Columns, shifts.Select(s => new[] { s.Cashier ?? "", Span(s), RevenuePdf.Money(s.OpeningFloat), RevenuePdf.Money(s.CashIn), RevenuePdf.Money(s.CashOut),
                RevenuePdf.Money(s.Expected), s.Counted is { } c ? RevenuePdf.Money(c) : "", s.IsOpen ? "" : RevenuePdf.Money(s.Shortage), s.IsOpen ? "" : RevenuePdf.Money(s.Overage) }).ToList(),
            null, "No cash shifts in this period.", notes);
    }
}

/// <summary>Phase 18 (P8): Printable payroll summary and per-person payslip blocks.</summary>
public static class PayrollPdf
{
    static readonly (string Title, double Width, bool Right)[] Columns =
        [("Name", 105, false), ("Role", 55, false), ("Worked out from", 95, false), ("Base pay", 55, true), ("Bonuses", 50, true), ("Gross", 55, true), ("Deductions", 50, true), ("Net pay", 55, true)];

    /// <summary>One person's payslip block: pay type and rate, how the base pay was worked out, corrections, each line with its category, gross and net (H17).</summary>
    public static string Payslip(PayrollRow r, IReadOnlyList<PayrollLine> lines, IReadOnlyList<PayrollHourAdjustment> adjustments)
    {
        var unit = r.PayType == "per_session" ? "sessions" : "h";
        // government lines: their note already starts with the name ("SSS, Oct 2026, …"), so no "SSS: SSS, …"
        string Line(PayrollLine x) => $"{(x.Kind == "bonus" ? "+" : "-")}{PayTypes.Money(x.Amount)} "
            + (PayTypes.IsGovernment(x.Category) ? x.Note ?? x.CategoryLabel : $"{(string.IsNullOrEmpty(x.CategoryLabel) ? "" : x.CategoryLabel + ": ")}{x.Note}");
        // a draft has no frozen government lines yet: list the amounts the period shows (0144 review fix)
        if (r.GovTotal > 0 && !lines.Any(x => PayTypes.IsGovernment(x.Category)))
            lines = [.. lines, .. new[] { ("sss", r.Sss), ("philhealth", r.PhilHealth), ("pagibig", r.PagIbig), ("tax", r.Tax) }
                .Where(g => g.Item2 > 0).Select(g => new PayrollLine { Kind = "deduction", Category = g.Item1, Amount = g.Item2, Note = PayTypes.CategoryLabel(g.Item1) })];
        var parts = new List<string>
        {
            r.NoSalary ? $"{r.FullName} ({r.RoleLabel}) · No salary set" : $"{r.FullName} ({r.RoleLabel}) · {PayTypes.Label(r.PayType)} pay, {PayTypes.RateText(r.PayType, r.Rate)}",
            PayTypes.BasisSentence(r)
        };
        parts.AddRange(adjustments.Select(a => $"Correction {(a.Quantity > 0 ? "+" : "")}{a.Quantity.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)} {unit}: {a.Note}"));
        parts.Add($"Base pay {PayTypes.Money(r.BasePay)}");
        parts.AddRange(lines.Where(x => x.Kind == "bonus").Select(Line));
        parts.Add($"Gross pay {PayTypes.Money(r.Gross)}");
        parts.AddRange(lines.Where(x => x.Kind == "deduction").OrderBy(x => PayTypes.IsGovernment(x.Category)).Select(Line));
        parts.Add($"Net pay {PayTypes.Money(r.Net)}");
        return string.Join(" | ", parts);
    }

    public static string PeriodSpan(DateOnly startsOn, DateOnly endsOn) =>
        $"{startsOn.Day}–{endsOn.Day} {startsOn.ToString("MMM yyyy", System.Globalization.CultureInfo.InvariantCulture)}";

    public static byte[] Build(string gymName, PayrollPeriod period, IReadOnlyDictionary<string, IReadOnlyList<PayrollLine>> linesByUser, DateTime generatedUtc, byte[] font,
        IReadOnlyDictionary<string, IReadOnlyList<PayrollHourAdjustment>>? adjustmentsByUser = null)
    {
        var status = period.IsPaid
            ? $"Paid {ManilaTime.Of(period.PaidAt!.Value).ToString("d MMM yyyy", System.Globalization.CultureInfo.InvariantCulture)} by {period.PaidByName ?? "Admin"}"
            : "Draft (not yet paid)";
        var span = PeriodSpan(period.StartsOn, period.EndsOn);
        var rows = period.Rows.Select(r => new[]
        {
            r.FullName,
            r.RoleLabel,
            r.BasisText,
            r.NoSalary ? "No salary" : RevenuePdf.Money(r.BasePay),
            r.Bonuses == 0 ? "–" : RevenuePdf.Money(r.Bonuses),
            RevenuePdf.Money(r.Gross),
            r.Deductions == 0 ? "–" : RevenuePdf.Money(r.Deductions),
            RevenuePdf.Money(r.Net)
        }).ToList();

        var payslips = period.Rows.Select(r => Payslip(r,
            linesByUser.TryGetValue(r.UserId, out var l) ? l : [],
            adjustmentsByUser is not null && adjustmentsByUser.TryGetValue(r.UserId, out var a) ? a : [])).ToList();

        return RevenuePdf.Document(
            $"{gymName} — Payroll",
            $"Payroll {span}",
            font,
            [(span, RevenuePdf.Kind.Text), (status, RevenuePdf.Kind.Strong), ($"Generated {RevenuePdf.Manila(generatedUtc)} (Asia/Manila)", RevenuePdf.Kind.Body), ("", RevenuePdf.Kind.Gap)],
            Columns,
            rows,
            ["Total", "", $"{period.Rows.Count} people", "", "", RevenuePdf.Money(period.TotalGross), "", RevenuePdf.Money(period.TotalNet)],
            "No staff in this pay period.",
            payslips);
    }
}
