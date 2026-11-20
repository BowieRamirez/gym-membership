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
        public string? CloseReason; public DateTime? ResolvedAt; public List<CashShiftNote> Notes = new();
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
        var exp = s.ClosedAt is null ? s.Float + cashIn - cashOut : s.Expected;
        var diff = s.Counted - exp;
        return new CashShift
        {
            Id = s.Id, Cashier = All<Profile>().First(p => p.Id == s.Cashier).FullName, OpenedAt = s.OpenedAt, ClosedAt = s.ClosedAt,
            OpeningFloat = s.Float, CashIn = cashIn, CashOut = cashOut, Expected = exp,
            Counted = s.Counted, Difference = diff,
            Shortage = diff is < 0 ? -diff.Value : 0m, Overage = diff is > 0 ? diff.Value : 0m,
            OpenNote = s.OpenNote, CloseNote = s.CloseNote, CloseReason = s.CloseReason,
            ResolvedAt = s.ResolvedAt, Notes = s.Notes.ToList()
        };
    }

    bool CashShiftCall(string fn, Dictionary<string, object> a, out object? result)
    {
        result = null;
        decimal Money(string k) => a.TryGetValue(k, out var v) && v is not null ? Convert.ToDecimal(v) : throw Err("bad_input");
        string? Str(string k) => a.TryGetValue(k, out var v) && v is string s && !string.IsNullOrWhiteSpace(s) ? s.Trim() : null;
        string? Note() => Str("p_note");
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
                var counted = Math.Round(Money("p_counted"), 2); var note = Note(); var reason = Str("p_reason");
                if (counted is < 0 or > 10_000_000 || note?.Length > 500) throw Err("bad_input");
                var s = _shifts.FirstOrDefault(x => x.Cashier == Uid && x.ClosedAt is null) ?? throw Err("no_open_shift");
                var (cashIn, cashOut) = ShiftTotals(s.Id);
                var expected = s.Float + cashIn - cashOut;
                if (counted != expected)
                {
                    if (reason is null) throw Err("reason_required");
                    if (reason is not ("wrong_change" or "unrecorded_sale" or "counting_mistake" or "float_wrong" or "other")) throw Err("bad_input");
                    if (reason == "other" && note is null) throw Err("note_required");
                }
                else reason = null;
                s.ClosedAt = SubscriptionNow; s.CashIn = cashIn; s.CashOut = cashOut; s.Expected = expected;
                s.Counted = counted; s.CloseNote = note; s.CloseReason = reason;
                Audit("cash_shift.close", "cash_shifts", s.Id.ToString());
                result = Row(s); return true;
            }
            case "add_cash_shift_note":
            {
                Require("reports:revenue");
                var id = Convert.ToInt32(a["p_shift"]); var note = Note();
                var resolve = a.TryGetValue("p_resolve", out var rv) && rv is bool b && b;
                if (note is null || note.Length > 1000) throw Err("bad_input");
                var s = _shifts.FirstOrDefault(x => x.Id == id) ?? throw Err("bad_input");
                if (s.ClosedAt is null) throw Err("bad_input");
                var author = All<Profile>().First(p => p.Id == Uid).FullName;
                var n = new CashShiftNote { Id = s.Notes.Count + 1, Author = author, Note = note, Resolved = resolve, CreatedAt = SubscriptionNow };
                s.Notes.Add(n);
                if (resolve && s.ResolvedAt is null) s.ResolvedAt = SubscriptionNow;
                Audit("cash_shift.note", "cash_shifts", s.Id.ToString());
                result = n; return true;
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
