using GymMembership.Core.Models;

namespace GymMembership.Core.Demo;

// 0050_cash_shifts.sql in memory (F1-F3): one open drawer shift per cashier; cash taken or paid back is tied to it and refused
// without one; closing stores in, out, expected and the count, and a closed shift never changes. No table the app reads: plain fields.
public sealed partial class DemoGateway
{
    sealed class DemoShift
    {
        public int Id; public string Cashier = ""; public DateTime OpenedAt; public decimal Float; public string? OpenNote;
        public DateTime? ClosedAt; public decimal CashIn, CashOut, Expected; public decimal? Counted; public string? CloseNote;
    }
    readonly List<DemoShift> _shifts = new();

    /// <summary>The cashier's open shift, for tying cash to it; as the 0050 triggers, refuses when there is none.</summary>
    int OpenShiftOf(string cashier) => _shifts.FirstOrDefault(s => s.Cashier == cashier && s.ClosedAt is null)?.Id ?? throw Err("no_open_shift");

    (decimal In, decimal Out) ShiftTotals(int id) =>
        (All<Payment>().Where(p => p.CashShiftId == id && p.Status == Status.Verified).Sum(p => p.Amount),
         All<RefundRequest>().Where(r => r.CashShiftId == id && r.Status == "paid").Sum(r => r.Amount));

    CashShift Row(DemoShift s)
    {
        var (cashIn, cashOut) = s.ClosedAt is null ? ShiftTotals(s.Id) : (s.CashIn, s.CashOut);
        return new CashShift
        {
            Id = s.Id, Cashier = All<Profile>().First(p => p.Id == s.Cashier).FullName, OpenedAt = s.OpenedAt, ClosedAt = s.ClosedAt,
            OpeningFloat = s.Float, CashIn = cashIn, CashOut = cashOut, Expected = s.ClosedAt is null ? s.Float + cashIn - cashOut : s.Expected,
            Counted = s.Counted, Difference = s.Counted - s.Expected, OpenNote = s.OpenNote, CloseNote = s.CloseNote
        };
    }

    bool CashShiftCall(string fn, Dictionary<string, object> a, out object? result)
    {
        result = null;
        decimal Money(string k) => a.TryGetValue(k, out var v) && v is not null ? Convert.ToDecimal(v) : throw Err("bad_input");
        string? Note() => a.TryGetValue("p_note", out var v) && v is string s && !string.IsNullOrWhiteSpace(s) ? s.Trim() : null;
        switch (fn)
        {
            case "open_cash_shift":
            {
                Require("payments:record");
                var amount = Money("p_float"); var note = Note();
                if (amount is < 0 or > 10_000_000 || note?.Length > 500) throw Err("bad_input");
                if (amount != All<GymSettings>().First().OpeningFloat && note is null) throw Err("note_required");
                if (_shifts.Any(s => s.Cashier == Uid && s.ClosedAt is null)) throw Err("shift_open");
                var s = new DemoShift { Id = _shifts.Count + 1, Cashier = Uid, OpenedAt = SubscriptionNow, Float = Math.Round(amount, 2), OpenNote = note };
                _shifts.Add(s);
                Audit("cash_shift.open", "cash_shifts", s.Id.ToString());
                result = Row(s); return true;
            }
            case "close_cash_shift":
            {
                Require("payments:record");
                var counted = Math.Round(Money("p_counted"), 2); var note = Note();
                if (counted is < 0 or > 10_000_000 || note?.Length > 500) throw Err("bad_input");
                var s = _shifts.FirstOrDefault(x => x.Cashier == Uid && x.ClosedAt is null) ?? throw Err("no_open_shift");
                var (cashIn, cashOut) = ShiftTotals(s.Id);
                if (counted != s.Float + cashIn - cashOut && note is null) throw Err("note_required");
                s.ClosedAt = SubscriptionNow; s.CashIn = cashIn; s.CashOut = cashOut; s.Expected = s.Float + cashIn - cashOut; s.Counted = counted; s.CloseNote = note;
                Audit("cash_shift.close", "cash_shifts", s.Id.ToString());
                result = Row(s); return true;
            }
            case "my_cash_shift":
                Require("payments:record");
                result = _shifts.Where(s => s.Cashier == Uid && s.ClosedAt is null).Select(Row).ToList();
                return true;
            case "cash_shifts_list":
            {
                Require("reports:revenue");
                var from = DateTime.Parse((string)a["p_from"]).Date; var to = DateTime.Parse((string)a["p_to"]).Date;
                if (from > to) throw Err("bad_input");
                result = _shifts.Where(s => s.OpenedAt.AddHours(8).Date >= from && s.OpenedAt.AddHours(8).Date <= to)
                    .OrderByDescending(s => s.OpenedAt).ThenByDescending(s => s.Id).Select(Row).ToList();
                return true;
            }
            default: return false;
        }
    }
}
