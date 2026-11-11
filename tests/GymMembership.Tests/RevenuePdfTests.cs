using System.Text;
using System.Text.RegularExpressions;
using GymMembership.Core.Demo;
using GymMembership.Core.Models;
using GymMembership.Core.Services;
using GymMembership.Core.ViewModels;
using Xunit;

// Phase 4 (D11): receipt lines that add up to the on-screen revenue, exported as a PDF through the share sheet
public class RevenuePdfTests
{
    static readonly byte[] Font = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "NotoSans-Regular.ttf"));

    [Fact]
    public void Revenue_rows_and_methods_read_as_words_not_codes()
    {
        // 9.8: the screen showed "walk_in · cash · 1 payment(s)"
        Assert.Equal("Walk-in · Cash · 1 payment", new RevenueRow(DateTime.Today, PayKind.WalkIn, PayMethod.Cash, 1, 150).Summary);
        Assert.Equal("Extension · QR · 3 payments", new RevenueRow(DateTime.Today, PayKind.Renewal, PayMethod.Qr, 3, 900).Summary);
        Assert.Equal(("GCash", "voucher"), (PayMethod.Name(PayMethod.Gcash), PayMethod.Name("voucher")));
        Assert.Equal("Sat 7 Nov 2026, 8:30 AM", new AuditRecord { CreatedAt = new DateTime(2026, 11, 7, 0, 30, 0, DateTimeKind.Utc) }.ShownWhen);
    }

    sealed class Files : IReportFiles
    {
        public string? Name; public byte[]? Pdf; public Exception? Fail;
        public Task<byte[]> FontAsync() => Task.FromResult(Font);
        public Task ShareAsync(string fileName, byte[] pdf) { if (Fail is not null) throw Fail; Name = fileName; Pdf = pdf; return Task.CompletedTask; }
    }

    static async Task<DemoGateway> Admin()
    {
        var gw = new DemoGateway();
        await gw.SignInAsync("admin@demo.io", DemoGateway.DemoPassword);
        return gw;
    }

    static RevenueLine Line(int n, decimal amount = 1000m, bool refund = false) =>
        new(new DateTime(2026, 10, 1, 1, 0, 0, DateTimeKind.Utc).AddMinutes(n), $"R-{n:0000}", $"Member {n}", "Monthly", "membership", "cash", refund ? -amount : amount, refund, n);

    static int Pages(byte[] pdf) => Regex.Matches(Encoding.Latin1.GetString(pdf), @"/Type\s*/Page(?!s)").Count;

    [Fact]
    public async Task Lines_add_up_to_the_revenue_total_and_need_revenue_permission()
    {
        var gw = await Admin(); var admin = new AdminService(gw);
        var from = DateOnly.FromDateTime(DateTime.Today.AddDays(-60)); var to = DateOnly.FromDateTime(DateTime.Today.AddDays(1));
        var lines = (await admin.RevenueLinesAsync(from, to)).Value!;
        Assert.NotEmpty(lines);
        Assert.Equal((await admin.RevenueAsync(from, to)).Value!.Sum(r => r.Total), lines.Sum(l => l.Amount));
        Assert.Equal(AppErrorKind.Validation, (await admin.RevenueLinesAsync(to, from)).Error);

        await gw.SignInAsync("employee@demo.io", DemoGateway.DemoPassword);
        Assert.Equal(AppErrorKind.Unauthorized, (await new AdminService(gw).RevenueLinesAsync(from, to)).Error);
    }

    [Fact]
    public void A_long_report_spans_pages_and_an_empty_one_still_renders()
    {
        var many = Enumerable.Range(1, 150).Select(n => Line(n)).Append(Line(151, 500m, refund: true)).ToList();
        var pdf = RevenuePdf.Build(new(2026, 10, 1), new(2026, 10, 31), many, DateTime.UtcNow, Font);
        Assert.StartsWith("%PDF-", Encoding.ASCII.GetString(pdf, 0, 5));
        Assert.True(Pages(pdf) > 1);

        var empty = RevenuePdf.Build(new(2026, 10, 1), new(2026, 10, 1), [], DateTime.UtcNow, Font);
        Assert.Equal(1, Pages(empty));
    }

    [Fact]
    public void Money_is_shown_in_pesos_with_refunds_negative()
    {
        Assert.Equal("₱1,234.50", RevenuePdf.Money(1234.5m));
        Assert.Equal("-₱1,000.00", RevenuePdf.Money(-1000m));
        Assert.Equal("1 Oct 2026 to 31 Oct 2026 (Asia/Manila)", RevenuePdf.Range(new(2026, 10, 1), new(2026, 10, 31)));
        Assert.Equal("1 Oct 2026, 09:00", RevenuePdf.Manila(new DateTime(2026, 10, 1, 1, 0, 0, DateTimeKind.Utc)));
    }

    [Fact]
    public async Task Export_uses_the_range_that_was_loaded_not_the_one_being_edited()
    {
        var gw = await Admin(); var files = new Files();
        var vm = new RevenueViewModel(new AdminService(gw), files) { From = DateTime.Today.AddDays(-60), To = DateTime.Today.AddDays(1) };
        Assert.False(vm.ExportPdfCommand.CanExecute(null));    // nothing loaded yet
        await vm.LoadCommand.ExecuteAsync(null);
        var loadedFrom = vm.From;
        vm.From = DateTime.Today;                               // edited, not applied

        await vm.ExportPdfCommand.ExecuteAsync(null);
        Assert.Null(vm.ErrorMessage);
        Assert.Equal($"revenue-{loadedFrom:yyyy-MM-dd}-to-{DateTime.Today.AddDays(1):yyyy-MM-dd}.pdf", files.Name);
        Assert.StartsWith("%PDF-", Encoding.ASCII.GetString(files.Pdf!, 0, 5));
        Assert.NotNull(vm.Notice);
    }

    [Fact]
    public async Task Revenue_lists_the_drawer_shifts_of_the_range_and_exports_them()
    {
        var gw = new DemoGateway(); await gw.SignInAsync("employee@demo.io", DemoGateway.DemoPassword);
        var desk = new PaymentStaffService(gw);
        await desk.OpenShiftAsync(4000, "Short float"); await desk.RecordCounterAsync(PayKind.WalkIn, PayMethod.Cash, null, null, "Pedro Ramos", null, null);
        await desk.CloseShiftAsync(4100, "Gave change twice");
        await gw.SignInAsync("admin@demo.io", DemoGateway.DemoPassword);
        var files = new Files();
        var vm = new RevenueViewModel(new AdminService(gw), files) { From = DateTime.Today.AddDays(-1), To = DateTime.Today.AddDays(1) };
        Assert.False(vm.ExportShiftsPdfCommand.CanExecute(null));
        await vm.LoadCommand.ExecuteAsync(null);
        var shift = Assert.Single(vm.Shifts);
        Assert.Equal((4150m, -50m), (shift.Expected, shift.Difference!.Value));

        await vm.ExportShiftsPdfCommand.ExecuteAsync(null);
        Assert.Null(vm.ErrorMessage);
        Assert.StartsWith("cash-shifts-", files.Name);
        Assert.StartsWith("%PDF-", Encoding.ASCII.GetString(files.Pdf!, 0, 5));
    }

    [Fact]
    public void The_shifts_PDF_renders_open_closed_and_empty()
    {
        CashShift S(int n, bool open) => new() { Id = n, Cashier = $"Cashier {n}", OpenedAt = new DateTime(2026, 11, 9, 0, 0, 0, DateTimeKind.Utc).AddMinutes(n),
            ClosedAt = open ? null : new DateTime(2026, 11, 9, 8, 0, 0, DateTimeKind.Utc), OpeningFloat = 5000, CashIn = 150, Expected = 5150,
            Counted = open ? null : 5100, Difference = open ? null : -50, CloseNote = open ? null : "Gave change twice" };
        var pdf = CashShiftPdf.Build(new(2026, 11, 9), new(2026, 11, 9), Enumerable.Range(1, 80).Select(n => S(n, n == 1)).ToList(), DateTime.UtcNow, Font);
        Assert.True(Pages(pdf) > 1);
        Assert.Equal(1, Pages(CashShiftPdf.Build(new(2026, 11, 9), new(2026, 11, 9), [], DateTime.UtcNow, Font)));
    }

    [Fact]
    public async Task A_failed_share_shows_an_error_and_no_success_message()
    {
        var gw = await Admin(); var files = new Files { Fail = new InvalidOperationException("share failed") };
        var vm = new RevenueViewModel(new AdminService(gw), files) { From = DateTime.Today.AddDays(-60), To = DateTime.Today.AddDays(1) };
        await vm.LoadCommand.ExecuteAsync(null);
        await vm.ExportPdfCommand.ExecuteAsync(null);
        Assert.NotNull(vm.ErrorMessage);
        Assert.Null(vm.Notice);
    }

    [Fact]
    public async Task If_revenue_changed_since_loading_the_screen_refreshes_instead_of_exporting()
    {
        var gw = await Admin(); var files = new Files();
        var vm = new RevenueViewModel(new AdminService(gw), files) { From = DateTime.Today.AddDays(-60), To = DateTime.Today.AddDays(1) };
        await vm.LoadCommand.ExecuteAsync(null);
        var before = vm.TotalText;

        await gw.SignInAsync("employee@demo.io", DemoGateway.DemoPassword); await gw.OpenDrawerAsync();   // a walk-in is paid after the screen loaded
        Assert.True((await new PaymentStaffService(gw).RecordCounterAsync(PayKind.WalkIn, PayMethod.Cash, null, null, "Late Walk-in", null, null)).Ok);
        await gw.SignInAsync("admin@demo.io", DemoGateway.DemoPassword);

        await vm.ExportPdfCommand.ExecuteAsync(null);
        Assert.Null(files.Pdf);
        Assert.NotNull(vm.ErrorMessage);
        Assert.NotEqual(before, vm.TotalText);
    }
}
