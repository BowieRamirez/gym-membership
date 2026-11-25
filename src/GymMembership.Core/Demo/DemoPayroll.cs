using GymMembership.Core.Models;
using GymMembership.Core.Services;

namespace GymMembership.Core.Demo;

// 0136/0137/0142 in memory: pay type per person or role (fixed / hourly / per session), 1st-15th / 16th-end periods,
// approved shifts / check-ins / sessions (overlaps counted once), hour corrections, categorised bonus/deduction lines, mark paid.
public sealed partial class DemoGateway
{
    sealed class DemoStaffSalary
    {
        public string UserId = ""; public decimal Amount; public string PayType = "fixed"; public DateTime UpdatedAt; public string UpdatedBy = "";
    }
    sealed class DemoRoleSalary
    {
        public string Role = ""; public decimal Amount; public string PayType = "fixed"; public DateTime UpdatedAt; public string UpdatedBy = "";
    }
    sealed class DemoPayrollPeriod
    {
        public int Id; public DateOnly StartsOn; public DateOnly EndsOn; public DateTime? PaidAt; public string? PaidBy; public DateTime? PaidOutAt; public string? PaidOutBy;
    }
    sealed class DemoPayrollLine
    {
        public int Id; public int PeriodId; public string UserId = ""; public string Kind = "";
        public decimal Amount; public string? Note; public string? Category; public string? PayType; public decimal? Quantity; public decimal? Rate;
        public string CreatedBy = ""; public DateTime CreatedAt;
    }
    sealed class DemoHourAdjustment
    {
        public int Id; public int PeriodId; public string UserId = ""; public decimal Quantity; public string Note = ""; public string CreatedBy = ""; public DateTime CreatedAt;
    }

    readonly List<DemoStaffSalary> _staffSalaries = new();
    readonly List<DemoRoleSalary> _roleSalaries = new();
    readonly List<DemoPayrollPeriod> _payrollPeriods = new();
    readonly List<DemoPayrollLine> _payrollLines = new();
    readonly List<DemoHourAdjustment> _hourAdjustments = new();
    readonly List<(Expense E, string By, string? VoidBy)> _expenses = new();   // 0159 (Fix 5)
    readonly Dictionary<(string Source, long Id), (string Status, string? Reason)> _timeReviews = new();

    static readonly string[] Categories = ["regular_bonus", "overtime", "holiday", "other", "thirteenth_month", "de_minimis", "retro"];

    /// <summary>Tests: a closed (or open) cash shift at given times; returns its id.</summary>
    public int OwnerAddShift(string cashierId, DateTime openedAt, DateTime? closedAt)
    {
        var s = new DemoShift { Id = _shifts.Select(x => x.Id).DefaultIfEmpty(0).Max() + 1, Cashier = cashierId, OpenedAt = openedAt, ClosedAt = closedAt, Counted = closedAt is null ? null : 0 };
        _shifts.Add(s);
        return s.Id;
    }

    Expense Listed((Expense E, string By, string? VoidBy) x) => new()
    {
        ExpenseId = x.E.ExpenseId, SpentOn = x.E.SpentOn, Category = x.E.Category, Amount = x.E.Amount, Note = x.E.Note, CreatedAt = x.E.CreatedAt,
        CreatedByName = NameOf(x.By), VoidedAt = x.E.VoidedAt, VoidReason = x.E.VoidReason, VoidedByName = x.VoidBy is { } v ? NameOf(v) : null
    };

    static DateOnly PayrollPeriodEnd(DateOnly startsOn) => startsOn.Day switch
    {
        1 => startsOn.AddDays(14),
        16 => new DateOnly(startsOn.Year, startsOn.Month, DateTime.DaysInMonth(startsOn.Year, startsOn.Month)),
        _ => throw Err("bad_input")
    };

    static int RoleOrder(string role) => role switch { "employee" => 1, "coach" => 2, "subadmin" => 3, "admin" => 4, _ => 5 };

    string? PrimaryPayrollRole(string userId)
    {
        var roleIds = All<UserRole>().Where(ur => ur.UserId == userId).Select(ur => ur.RoleId).ToHashSet();
        return All<Role>()
            .Where(r => roleIds.Contains(r.RoleId) && r.Name is "employee" or "coach" or "subadmin" or "admin")
            .OrderBy(r => RoleOrder(r.Name))
            .Select(r => r.Name)
            .FirstOrDefault();
    }

    string StaffDisplayName(Profile p) => string.IsNullOrWhiteSpace(p.FullName) ? p.Username : p.FullName.Trim();
    string NameOf(string userId) => StaffDisplayName(All<Profile>().First(u => u.Id == userId));

    sealed record PayInfo(Profile Person, string Role, string PayType, decimal Rate, bool NoSalary, bool Personal);

    // payroll_pay: who is on the period and how they are paid (current pay while a draft, the frozen line once paid)
    List<PayInfo> PayrollPay(DemoPayrollPeriod per) => All<Profile>().Where(p =>
        {
            if (per.PaidAt is not null) return _payrollLines.Any(l => l.PeriodId == per.Id && l.UserId == p.Id);
            if (!p.IsActive || p.DeletedAt is not null) return false;
            return PrimaryPayrollRole(p.Id) is "employee" or "coach"
                || _staffSalaries.Any(s => s.UserId == p.Id)
                || _payrollLines.Any(l => l.PeriodId == per.Id && l.UserId == p.Id);
        })
        .Select(p =>
        {
            var role = PrimaryPayrollRole(p.Id) ?? "employee";
            var ss = _staffSalaries.FirstOrDefault(s => s.UserId == p.Id);
            if (per.PaidAt is not null)
            {
                var sl = _payrollLines.Where(l => l.PeriodId == per.Id && l.UserId == p.Id && l.Kind == "salary").OrderBy(l => l.Id).FirstOrDefault();
                return new PayInfo(p, role, sl?.PayType ?? "fixed", sl?.Rate ?? sl?.Amount ?? 0m, sl is null, ss is not null);
            }
            var rs = _roleSalaries.FirstOrDefault(r => r.Role == role);
            return new PayInfo(p, role, ss?.PayType ?? rs?.PayType ?? "fixed", ss?.Amount ?? rs?.Amount ?? 0m, ss is null && rs is null, ss is not null);
        }).ToList();

    // payroll_records: every shift / check-in / session that can pay someone in the period, with review status and flags
    List<(string UserId, PayrollTimeRecord Rec)> PayrollRecords(DemoPayrollPeriod per)
    {
        bool InPeriod(DateTime t) => ManilaDay(t) >= per.StartsOn && ManilaDay(t) <= per.EndsOn;
        var raw = new List<(string UserId, string Source, long Id, DateTime Start, DateTime? End, string Label, bool Auto)>();
        foreach (var pay in PayrollPay(per))
        {
            if (pay.PayType == "hourly" && pay.Role != "coach")
                raw.AddRange(_shifts.Where(s => s.Cashier == pay.Person.Id && InPeriod(s.OpenedAt))
                    .Select(s => (pay.Person.Id, "cash_shift", (long)s.Id, s.OpenedAt, s.ClosedAt, $"Cash shift #{s.Id}", false)));
            else if (pay.PayType == "hourly")
                raw.AddRange(All<CoachVisit>().Where(v => v.CoachId == pay.Person.Id && InPeriod(v.CheckedInAt))
                    .Select(v => (pay.Person.Id, "coach_visit", (long)v.Id, v.CheckedInAt, v.CheckedOutAt, "Check-in", v.AutoClosed)));
            else if (pay.PayType == "per_session")
                raw.AddRange(All<TrainingSession>().Where(ts => ts.CoachId == pay.Person.Id && ts.Status == "completed"
                        && (ts.HeldUntil is null || ts.HeldUntil > SubscriptionNow) && InPeriod(ts.ScheduledStart))
                    .Select(ts => (pay.Person.Id, "training_session", (long)ts.Id, ts.ScheduledStart, (DateTime?)ts.ScheduledEnd,
                        All<Profile>().FirstOrDefault(m => m.Id == ts.MemberId) is { } m ? StaffDisplayName(m) : ts.WalkInName ?? "Walk-in", false)));
        }
        return raw.Select(r =>
        {
            var review = _timeReviews.TryGetValue((r.Source, r.Id), out var rv) ? rv : ("pending", (string?)null);
            var flags = new List<string>();
            if (r.End is null) flags.Add("open");
            if (r.Auto) flags.Add("auto_closed");
            if (r.End - r.Start > TimeSpan.FromHours(12)) flags.Add("long");
            if (r.Source != "training_session" && r.End is not null && raw.Any(o => o.UserId == r.UserId && o.Source == r.Source && o.Id != r.Id
                    && o.End is not null && o.Start < r.End && r.Start < o.End))
                flags.Add("overlap");
            return (r.UserId, new PayrollTimeRecord
            {
                Source = r.Source, SourceId = r.Id, StartsAt = r.Start, EndsAt = r.End, Label = r.Label,
                Hours = r.End is null ? 0 : Math.Round((decimal)(r.End.Value - r.Start).TotalHours, 2),
                Status = review.Item1, Reason = review.Item2, Flags = flags
            });
        }).OrderBy(x => x.Item2.StartsAt).ThenBy(x => x.Item2.SourceId).ToList();
    }

    static decimal MergedHours(IEnumerable<PayrollTimeRecord> recs)
    {
        double total = 0; DateTime? curStart = null, curEnd = null;
        foreach (var r in recs.OrderBy(r => r.StartsAt))
        {
            if (curEnd is null || r.StartsAt > curEnd) { if (curEnd is not null) total += (curEnd.Value - curStart!.Value).TotalHours; curStart = r.StartsAt; curEnd = r.EndsAt; }
            else if (r.EndsAt > curEnd) curEnd = r.EndsAt;
        }
        if (curEnd is not null) total += (curEnd.Value - curStart!.Value).TotalHours;
        return Math.Round((decimal)total, 2);
    }

    // payroll_basis: the pay maths in one place
    List<PayrollRow> PayrollBasis(DemoPayrollPeriod per)
    {
        var recs = PayrollRecords(per);
        return PayrollPay(per).Select(pay =>
        {
            var id = pay.Person.Id;
            var mine = recs.Where(r => r.UserId == id).Select(r => r.Rec).ToList();
            var approved = mine.Where(r => r.Status == "approved" && r.EndsAt is not null).ToList();
            var logged = pay.PayType switch { "hourly" => MergedHours(approved), "per_session" => approved.Count, _ => 0m };
            var adjustment = _hourAdjustments.Where(a => a.PeriodId == per.Id && a.UserId == id).Sum(a => a.Quantity);
            decimal payable, basePay;
            if (per.PaidAt is not null)
            {
                var sl = _payrollLines.Where(l => l.PeriodId == per.Id && l.UserId == id && l.Kind == "salary").OrderBy(l => l.Id).FirstOrDefault();
                payable = sl?.Quantity ?? (pay.PayType == "fixed" ? 1 : 0);
                basePay = sl?.Amount ?? 0;
                logged = pay.PayType == "fixed" ? 0 : Math.Max(0, payable - adjustment);   // what it was paid for (0143)
            }
            else if (pay.PayType == "fixed") { payable = 1; basePay = pay.Rate; }
            else { payable = Math.Max(0, logged + adjustment); basePay = Math.Round(payable * pay.Rate, 2, MidpointRounding.AwayFromZero); }
            var bonuses = _payrollLines.Where(l => l.PeriodId == per.Id && l.UserId == id && l.Kind == "bonus").Sum(l => l.Amount);
            var deductions = _payrollLines.Where(l => l.PeriodId == per.Id && l.UserId == id && l.Kind == "deduction" && !PayTypes.IsGovernment(l.Category)).Sum(l => l.Amount);

            // reference numbers from Part 3 (closed shifts, completed sessions)
            var sessions = pay.Role == "coach" ? All<TrainingSession>().Where(ts => ts.CoachId == id && ts.Status == "completed"
                && (ts.HeldUntil is null || ts.HeldUntil > SubscriptionNow)
                && ManilaDay(ts.ScheduledStart) >= per.StartsOn && ManilaDay(ts.ScheduledStart) <= per.EndsOn).ToList() : [];
            var hours = pay.Role switch
            {
                "coach" => Math.Round((decimal)sessions.Sum(ts => (ts.ScheduledEnd - ts.ScheduledStart).TotalHours), 2),
                "employee" => Math.Round((decimal)_shifts.Where(s => s.Cashier == id && s.ClosedAt is not null
                    && ManilaDay(s.OpenedAt) >= per.StartsOn && ManilaDay(s.OpenedAt) <= per.EndsOn).Sum(s => (s.ClosedAt!.Value - s.OpenedAt).TotalHours), 2),
                _ => 0m
            };
            return new PayrollRow
            {
                UserId = id, FullName = StaffDisplayName(pay.Person), Role = pay.Role, SessionsCount = sessions.Count, Hours = hours,
                PayType = pay.PayType, Rate = pay.Rate, Personal = pay.Personal, NoSalary = pay.NoSalary,
                Logged = logged, Adjustment = adjustment, Payable = payable, BasePay = basePay, Salary = basePay,
                Bonuses = bonuses, Deductions = deductions, Gross = basePay + bonuses, Net = basePay + bonuses - deductions,
                Pending = per.PaidAt is not null ? 0 : mine.Count(r => r.Status == "pending" && r.EndsAt is not null), Flagged = mine.Count(r => r.Flags.Count > 0)
            };
        }).OrderBy(r => RoleOrder(r.Role)).ThenBy(r => r.FullName).ToList();
    }

    // 0144/0145: people with contributions and/or tax switched off (no entry = both on)
    readonly Dictionary<string, (bool ContributionsOff, bool TaxOff, string? Reason)> _noDeductions = new();

    decimal Bonus(int periodId, string userId, string category) =>
        _payrollLines.Where(l => l.PeriodId == periodId && l.UserId == userId && l.Kind == "bonus" && l.Category == category).Sum(l => l.Amount);

    // 0144 (T1-T9) + 0145 (U1, U4-U6): government deductions on top of the basis; same order and rules as payroll_basis
    List<PayrollRow> PayrollWithDeductions(DemoPayrollPeriod per)
    {
        var rows = PayrollBasis(per);
        var secondHalf = per.StartsOn.Day == 16;
        var half1 = secondHalf ? _payrollPeriods.FirstOrDefault(p => p.StartsOn == new DateOnly(per.StartsOn.Year, per.StartsOn.Month, 1)) : null;
        var first = half1 is null ? [] : PayrollBasis(half1);
        var firstTaxed = half1 is null ? [] : PayrollWithDeductions(half1);   // 0160: the 1st-15th's taxable pay and any tax it took
        foreach (var r in rows)
        {
            var off = _noDeductions.TryGetValue(r.UserId, out var o) ? o : (false, false, null);
            bool cOn = !r.NoSalary && !off.ContributionsOff, tOn = !r.NoSalary && !off.TaxOff;
            var f = first.FirstOrDefault(x => x.UserId == r.UserId);
            decimal m13 = Bonus(per.Id, r.UserId, "thirteenth_month"), dm = Bonus(per.Id, r.UserId, "de_minimis");
            decimal Retro(int periodId) => _payrollLines.Where(l => l.PeriodId == periodId && l.UserId == r.UserId && l.Kind == "deduction" && l.Category == "retro").Sum(l => l.Amount);
            var rd = Retro(per.Id);
            decimal half1Out = half1 is null ? 0 : Bonus(half1.Id, r.UserId, "thirteenth_month") + Bonus(half1.Id, r.UserId, "de_minimis") + Retro(half1.Id);
            var m13Before = _payrollPeriods.Where(pp => pp.PaidAt is not null && pp.Id != per.Id && pp.StartsOn.Year == per.StartsOn.Year)
                .Sum(pp => Bonus(pp.Id, r.UserId, "thirteenth_month"));
            var nontax = Math.Min(m13, Math.Max(0, 90000 - m13Before)) + dm;
            var mGross = Math.Max(0, r.Gross + (f?.Gross ?? 0) - m13 - dm - rd - half1Out); var mBase = r.BasePay + (f?.BasePay ?? 0);
            var due = cOn && secondHalf && mGross > 0;   // no pay all month: nothing is due (0144 review fix)
            decimal ws = due ? PayrollDeductions.Sss(mGross) : 0, wp = due ? PayrollDeductions.PhilHealth(mBase) : 0,
                    wi = due ? PayrollDeductions.PagIbig(mGross) : 0;
            // 0160 (owner): tax once a month, in the 16th-end pay, on the month's taxable pay, less what the 1st-15th took
            var taxable = Math.Max(0, r.Gross - nontax - rd - ws - wp - wi);
            var fd = firstTaxed.FirstOrDefault(x => x.UserId == r.UserId);
            if (secondHalf) taxable += fd?.Taxable ?? 0;
            var wt = tOn && secondHalf ? Math.Max(0, PayrollDeductions.Tax(taxable) - (fd?.Tax ?? 0)) : 0;
            var on = cOn || tOn;
            r.ContributionsOn = cOn; r.TaxOn = tOn; r.DeductionsReason = off.Reason; r.Nontaxable = nontax; r.ThirteenthUsed = m13Before + m13;
            decimal s, p, i, t;
            if (per.PaidAt is not null)
            {
                decimal Frozen(string c) => _payrollLines.Where(l => l.PeriodId == per.Id && l.UserId == r.UserId && l.Kind == "deduction" && l.Category == c).Sum(l => l.Amount);
                (s, p, i, t) = (Frozen("sss"), Frozen("philhealth"), Frozen("pagibig"), Frozen("tax"));
            }
            else
            {
                var room = Math.Max(0, r.Gross - r.Deductions);
                s = Math.Min(ws, room); p = Math.Min(wp, Math.Max(0, room - ws)); i = Math.Min(wi, Math.Max(0, room - ws - wp)); t = Math.Min(wt, Math.Max(0, room - ws - wp - wi));
            }
            var gov = s + p + i + t;
            r.DeductionsOn = on; r.ManualDeductions = r.Deductions; r.Sss = s; r.PhilHealth = p; r.PagIbig = i; r.Tax = t; r.GovTotal = gov;
            r.GovCut = per.PaidAt is null && gov < ws + wp + wi + wt;
            r.MonthGross = secondHalf ? mGross : Math.Max(0, r.Gross - m13 - dm - rd); r.MonthBase = secondHalf ? mBase : r.BasePay; r.Taxable = taxable;
            r.Msc = PayrollDeductions.Msc(mGross); r.PagIbigBase = Math.Min(Math.Max(mGross, 0), 10000);
            r.Deductions += gov; r.Net -= gov;
        }
        return rows;
    }

    PayrollPeriod BuildPayrollPeriod(DemoPayrollPeriod per)
    {
        var rows = PayrollWithDeductions(per);
        return new PayrollPeriod
        {
            PeriodId = per.Id, StartsOn = per.StartsOn, EndsOn = per.EndsOn, PaidAt = per.PaidAt,
            PaidByName = per.PaidBy is null ? null : NameOf(per.PaidBy),
            PaidOutAt = per.PaidOutAt, PaidOutByName = per.PaidOutBy is null ? null : NameOf(per.PaidOutBy),
            Status = per.PaidOutAt is not null ? "paid_out" : per.PaidAt is not null ? "approved" : "draft",
            TotalNet = rows.Sum(r => r.Net), TotalGross = rows.Sum(r => r.Gross), TotalGov = rows.Sum(r => r.GovTotal), Rows = rows
        };
    }

    DemoPayrollPeriod PayrollDraft(int periodId)
    {
        var per = _payrollPeriods.FirstOrDefault(x => x.Id == periodId) ?? throw Err("bad_input");
        if (per.PaidAt is not null) throw Err("payroll_paid");
        return per;
    }

    PayrollLine ToLine(DemoPayrollLine l) => new()
    {
        LineId = l.Id, Kind = l.Kind, Amount = l.Amount, Note = l.Note, Category = l.Category, PayType = l.PayType,
        Quantity = l.Quantity, Rate = l.Rate, CreatedByName = NameOf(l.CreatedBy), CreatedAt = l.CreatedAt
    };

    bool PayrollCall(string fn, Dictionary<string, object> a, out object? result)
    {
        result = null;
        decimal Money(string k) => a.TryGetValue(k, out var v) && v is not null ? Convert.ToDecimal(v) : throw Err("bad_input");
        string? Str(string k) => a.TryGetValue(k, out var v) && v is string s && !string.IsNullOrWhiteSpace(s) ? s.Trim() : null;
        int Int(string k) => a.TryGetValue(k, out var v) && v is not null ? Convert.ToInt32(v) : throw Err("bad_input");
        string? PayTypeArg()
        {
            var t = Str("p_pay_type");
            if (t is not null and not ("fixed" or "hourly" or "per_session")) throw Err("bad_input");
            return t;
        }
        switch (fn)
        {
            case "set_staff_salary":
            {
                Require("reports:revenue");
                var userId = Str("p_user") ?? throw Err("bad_input");
                var amount = Money("p_amount");
                var type = PayTypeArg();
                if (amount is <= 0 or > 10_000_000) throw Err("bad_input");
                if (userId == Uid) throw Err("own_salary");
                var p = All<Profile>().FirstOrDefault(x => x.Id == userId && x.IsActive && x.DeletedAt is null) ?? throw Err("bad_input");
                var role = PrimaryPayrollRole(p.Id) ?? throw Err("bad_input");
                var existing = _staffSalaries.FirstOrDefault(s => s.UserId == userId);
                type ??= existing?.PayType ?? _roleSalaries.FirstOrDefault(r => r.Role == role)?.PayType ?? "fixed";
                if (type == "per_session" && role != "coach") throw Err("per_session_coach_only");
                existing ??= AddReturn(_staffSalaries, new DemoStaffSalary { UserId = userId });
                existing.Amount = Math.Round(amount, 2); existing.PayType = type; existing.UpdatedAt = SubscriptionNow; existing.UpdatedBy = Uid;
                Audit("payroll.salary_set", "staff_salaries", userId);
                return true;
            }
            case "use_role_salary":
            {
                Require("reports:revenue");
                var userId = Str("p_user") ?? throw Err("bad_input");
                if (PrimaryPayrollRole(userId) is not ("employee" or "coach")) throw Err("bad_input");
                if (userId == Uid) throw Err("own_salary");
                if (_staffSalaries.RemoveAll(s => s.UserId == userId) == 0) return true;   // already following the role (0143)
                Audit("payroll.salary_set", "staff_salaries", userId);
                return true;
            }
            case "set_role_salary":
            {
                Require("reports:revenue");
                var role = Str("p_role") ?? throw Err("bad_input");
                var amount = Money("p_amount");
                var type = PayTypeArg();
                if (role is not ("employee" or "coach") || amount is <= 0 or > 10_000_000) throw Err("bad_input");
                var existing = _roleSalaries.FirstOrDefault(r => r.Role == role);
                type ??= existing?.PayType ?? "fixed";
                if (type == "per_session" && role != "coach") throw Err("per_session_coach_only");
                existing ??= AddReturn(_roleSalaries, new DemoRoleSalary { Role = role });
                existing.Amount = Math.Round(amount, 2); existing.PayType = type; existing.UpdatedAt = SubscriptionNow; existing.UpdatedBy = Uid;
                Audit("payroll.role_salary_set", "role_salaries", role);
                return true;
            }
            case "payroll_role_salaries":
            {
                Require("reports:revenue");
                result = new[] { "employee", "coach" }.Select(role =>
                {
                    var rs = _roleSalaries.FirstOrDefault(r => r.Role == role);
                    return new RoleSalary { Role = role, Amount = rs?.Amount, PayType = rs?.PayType ?? "fixed", UpdatedAt = rs?.UpdatedAt,
                                            UpdatedByName = rs is null ? null : NameOf(rs.UpdatedBy) };
                }).ToList();
                return true;
            }
            case "payroll_staff":
            {
                Require("reports:revenue");
                result = All<Profile>()
                    .Where(p => p.IsActive && p.DeletedAt is null)
                    .Select(p => (Profile: p, Role: PrimaryPayrollRole(p.Id), Salary: _staffSalaries.FirstOrDefault(s => s.UserId == p.Id)))
                    .Where(x => x.Role is "employee" or "coach" || (x.Role is "subadmin" or "admin" && x.Salary is not null))
                    .OrderBy(x => RoleOrder(x.Role!)).ThenBy(x => StaffDisplayName(x.Profile))
                    .Select(x =>
                    {
                        var rs = _roleSalaries.FirstOrDefault(r => r.Role == x.Role);
                        var by = x.Salary?.UpdatedBy ?? rs?.UpdatedBy;
                        return new StaffSalary
                        {
                            UserId = x.Profile.Id, FullName = StaffDisplayName(x.Profile), Role = x.Role!,
                            Amount = x.Salary?.Amount ?? rs?.Amount, PayType = x.Salary?.PayType ?? rs?.PayType ?? "fixed", Personal = x.Salary is not null,
                            DeductionsOn = !(_noDeductions.TryGetValue(x.Profile.Id, out var nd) && nd.ContributionsOff && nd.TaxOff),
                            ContributionsOn = !_noDeductions.TryGetValue(x.Profile.Id, out var nd2) || !nd2.ContributionsOff,
                            TaxOn = !_noDeductions.TryGetValue(x.Profile.Id, out var nd3) || !nd3.TaxOff,
                            DeductionsReason = _noDeductions.TryGetValue(x.Profile.Id, out var nd4) ? nd4.Reason : null,
                            UpdatedAt = x.Salary?.UpdatedAt ?? rs?.UpdatedAt, UpdatedByName = by is null ? null : NameOf(by)
                        };
                    }).ToList();
                return true;
            }
            case "payroll_period":
            {
                Require("reports:revenue");
                var startsOn = DateOnly.Parse(Str("p_starts_on") ?? throw Err("bad_input"));
                var endsOn = PayrollPeriodEnd(startsOn);
                DemoPayrollPeriod Open(DateOnly s) => _payrollPeriods.FirstOrDefault(x => x.StartsOn == s)
                          ?? AddReturn(_payrollPeriods, new DemoPayrollPeriod { Id = _payrollPeriods.Count + 1, StartsOn = s, EndsOn = PayrollPeriodEnd(s) });
                var per = Open(startsOn);
                if (startsOn.Day == 16) Open(new DateOnly(startsOn.Year, startsOn.Month, 1));   // the month's first half (0144)
                _ = endsOn;
                result = BuildPayrollPeriod(per); return true;
            }
            case "set_payroll_deductions":
            {
                Require("reports:revenue");
                var userId = Str("p_user") ?? throw Err("bad_input");
                bool Flag(string k) => a.TryGetValue(k, out var v) && v is bool b ? b : throw Err("bad_input");
                var (contributions, tax) = a.ContainsKey("p_on") ? (Flag("p_on"), Flag("p_on")) : (Flag("p_contributions"), Flag("p_tax"));
                var reason = Str("p_reason");
                if (PrimaryPayrollRole(userId) is null || reason?.Length > 60
                    || !All<Profile>().Any(p => p.Id == userId && p.IsActive && p.DeletedAt is null)) throw Err("bad_input");
                if (userId == Uid) throw Err("own_salary");
                var before = _noDeductions.TryGetValue(userId, out var was) ? was : (false, false, null);
                if (contributions && tax) _noDeductions.Remove(userId); else _noDeductions[userId] = (!contributions, !tax, reason);
                var after = _noDeductions.TryGetValue(userId, out var now) ? now : (false, false, null);
                if (before != after) Audit("payroll.deductions_set", "staff_salaries", userId);   // real changes only
                return true;
            }
            case "payroll_reopen":
            {
                Require("reports:revenue");
                var reason = Str("p_reason");
                if (reason is null || reason.Length > 200) throw Err("bad_input");
                var per = _payrollPeriods.FirstOrDefault(x => x.Id == Int("p_period")) ?? throw Err("bad_input");
                if (per.PaidOutAt is not null) throw Err("paid_out");
                if (per.PaidAt is null) throw Err("not_approved");
                if (per.StartsOn.Day == 1 && _payrollPeriods.Any(x => x.StartsOn == per.StartsOn.AddDays(15) && x.PaidAt is not null)) throw Err("second_half_approved");
                _payrollLines.RemoveAll(l => l.PeriodId == per.Id && (l.Kind == "salary" || PayTypes.IsGovernment(l.Category)));
                per.PaidAt = null; per.PaidBy = null;
                Audit("payroll.reopened", "payroll_periods", per.Id.ToString());
                result = BuildPayrollPeriod(per); return true;
            }
            case "payroll_mark_paid_out":
            {
                Require("reports:revenue");
                var per = _payrollPeriods.FirstOrDefault(x => x.Id == Int("p_period")) ?? throw Err("bad_input");
                if (per.PaidOutAt is not null) throw Err("paid_out");
                if (per.PaidAt is null) throw Err("not_approved");
                per.PaidOutAt = SubscriptionNow; per.PaidOutBy = Uid;
                Audit("payroll.paid_out", "payroll_periods", per.Id.ToString());
                result = BuildPayrollPeriod(per); return true;
            }
            case "payroll_paid_out_periods":
            {
                Require("reports:revenue");
                result = _payrollPeriods
                    .Where(p => p.PaidOutAt is not null)
                    .OrderByDescending(p => p.StartsOn)
                    .ThenByDescending(p => p.PaidOutAt)
                    .Select(p =>
                    {
                        var lines = _payrollLines.Where(l => l.PeriodId == p.Id).ToList();
                        var basePay = lines.Where(l => l.Kind == "salary").Sum(l => l.Amount);
                        var bonuses = lines.Where(l => l.Kind == "bonus").Sum(l => l.Amount);
                        var deductions = lines.Where(l => l.Kind == "deduction").Sum(l => l.Amount);
                        var gov = lines.Where(l => l.Kind == "deduction" && PayTypes.IsGovernment(l.Category)).Sum(l => l.Amount);
                        return new PaidOutPeriod
                        {
                            PeriodId = p.Id, StartsOn = p.StartsOn, EndsOn = p.EndsOn,
                            PaidAt = p.PaidAt, PaidByName = p.PaidBy is null ? null : NameOf(p.PaidBy),
                            PaidOutAt = p.PaidOutAt!.Value, PaidOutByName = p.PaidOutBy is null ? null : NameOf(p.PaidOutBy),
                            People = lines.Select(l => l.UserId).Distinct().Count(),
                            BasePay = basePay, Bonuses = bonuses, Gross = basePay + bonuses,
                            Deductions = deductions, GovTotal = gov, Net = basePay + bonuses - deductions
                        };
                    }).ToList();
                return true;
            }
            case "payroll_records_for":
            {
                Require("reports:revenue");
                var per = _payrollPeriods.FirstOrDefault(x => x.Id == Int("p_period")) ?? throw Err("bad_input");
                var userId = Str("p_user") ?? throw Err("bad_input");
                result = PayrollRecords(per).Where(r => r.UserId == userId).Select(r => r.Rec).ToList();
                return true;
            }
            // ---- 0159 (Fix 5): expenses and the payroll cost the financial report reads ----
            case "expense_add":
            {
                Require("reports:revenue");
                var day = DateOnly.TryParse(Str("p_spent_on"), out var d) ? d : throw Err("bad_input");
                var category = Str("p_category");
                var amount = Money("p_amount");
                var note = Str("p_note");
                if (day > ManilaDay(SubscriptionNow) || category is null || ExpenseCategories.All.All(c => c.Code != category)
                    || amount is <= 0 or > 10_000_000 || Math.Round(amount, 2) != amount || note is null || note.Length > 200) throw Err("bad_input");
                var e = new Expense { ExpenseId = _expenses.Count + 1, SpentOn = day, Category = category, Amount = amount, Note = note, CreatedAt = SubscriptionNow };
                _expenses.Add((e, Uid, null));
                Audit("expense.added", "expenses", e.ExpenseId.ToString());
                result = Listed(_expenses[^1]);
                return true;
            }
            case "expense_void":
            {
                Require("reports:revenue");
                var id = a.TryGetValue("p_expense", out var iv) && iv is not null ? Convert.ToInt64(iv) : throw Err("bad_input");
                var reason = Str("p_reason");
                if (reason is null || reason.Length > 200) throw Err("bad_input");
                var i = _expenses.FindIndex(x => x.E.ExpenseId == id);
                if (i < 0) throw Err("not_found");
                if (_expenses[i].E.VoidedAt is not null) throw Err("already_processed");
                _expenses[i].E.VoidedAt = SubscriptionNow; _expenses[i].E.VoidReason = reason;
                _expenses[i] = _expenses[i] with { VoidBy = Uid };
                Audit("expense.voided", "expenses", id.ToString());
                return true;
            }
            case "expenses_list":
            {
                Require("reports:revenue");
                var (from, to) = (DateOnly.TryParse(Str("p_from"), out var f) ? f : throw Err("bad_input"), DateOnly.TryParse(Str("p_to"), out var t) ? t : throw Err("bad_input"));
                if (to < from) throw Err("bad_input");
                result = _expenses.Where(x => x.E.SpentOn >= from && x.E.SpentOn <= to).OrderByDescending(x => x.E.SpentOn).ThenByDescending(x => x.E.ExpenseId).Select(Listed).ToList();
                return true;
            }
            case "payroll_cost":
            {
                Require("reports:revenue");
                var (from, to) = (DateOnly.TryParse(Str("p_from"), out var f) ? f : throw Err("bad_input"), DateOnly.TryParse(Str("p_to"), out var t) ? t : throw Err("bad_input"));
                if (to < from) throw Err("bad_input");
                result = _payrollPeriods.Where(p => p.PaidAt is not null && p.EndsOn >= from && p.EndsOn <= to).OrderBy(p => p.EndsOn)
                    .Select(p => new PayrollCostDay { Day = p.EndsOn, Gross = _payrollLines.Where(l => l.PeriodId == p.Id && l.Kind is "salary" or "bonus").Sum(l => l.Amount) }).ToList();
                return true;
            }
            case "payroll_records_all":   // 0158 (Fix 4): every person's records and corrections for Attendance Review
            {
                Require("reports:revenue");
                var per = _payrollPeriods.FirstOrDefault(x => x.Id == Int("p_period")) ?? throw Err("bad_input");
                var pays = PayrollPay(per).ToDictionary(p => p.Person.Id);
                result = new PayrollAttendance
                {
                    Records = PayrollRecords(per).Select(x => (Pay: pays[x.UserId], x.Rec)).OrderBy(x => StaffDisplayName(x.Pay.Person)).Select(x => new PayrollStaffRecord
                    {
                        UserId = x.Pay.Person.Id, FullName = StaffDisplayName(x.Pay.Person), RoleName = x.Pay.Role, PayType = x.Pay.PayType, Rate = x.Pay.Rate,
                        Source = x.Rec.Source, SourceId = x.Rec.SourceId, StartsAt = x.Rec.StartsAt, EndsAt = x.Rec.EndsAt, Hours = x.Rec.Hours, Label = x.Rec.Label,
                        Status = x.Rec.Status, Reason = x.Rec.Reason, Flags = x.Rec.Flags,
                        ActualStart = x.Rec.ActualStart, ExpectedStart = x.Rec.ExpectedStart, ExpectedEnd = x.Rec.ExpectedEnd
                    }).ToList(),
                    Adjustments = _hourAdjustments.Where(x => x.PeriodId == per.Id).OrderBy(x => x.CreatedAt).ThenBy(x => x.Id)
                        .Select(x => new PayrollHourAdjustment { AdjustmentId = x.Id, Quantity = x.Quantity, Note = x.Note, CreatedByName = NameOf(x.CreatedBy), CreatedAt = x.CreatedAt, UserId = x.UserId, FullName = NameOf(x.UserId) })
                        .ToList()
                };
                return true;
            }
            case "payroll_review_time":
            {
                Require("reports:revenue");
                var periodId = Int("p_period");
                var source = Str("p_source") ?? throw Err("bad_input");
                var sourceId = a.TryGetValue("p_source_id", out var sv) && sv is not null ? Convert.ToInt64(sv) : throw Err("bad_input");
                var status = Str("p_status");
                var reason = Str("p_reason");
                if (status is not ("approved" or "rejected" or "pending") || (status == "rejected" && (reason is null || reason.Length > 200)))
                    throw Err("bad_input");
                var per = PayrollDraft(periodId);
                var rec = PayrollRecords(per).FirstOrDefault(r => r.Rec.Source == source && r.Rec.SourceId == sourceId).Rec ?? throw Err("not_found");
                if (rec.EndsAt is null) throw Err("record_open");
                if (status == "pending") _timeReviews.Remove((source, sourceId));
                else _timeReviews[(source, sourceId)] = (status, status == "rejected" ? reason : null);
                Audit(status switch { "approved" => "payroll.time_approved", "rejected" => "payroll.time_rejected", _ => "payroll.time_reopened" },
                    "payroll_time_reviews", $"{source}:{sourceId}");
                return true;
            }
            case "payroll_approve_all":
            {
                Require("reports:revenue");
                var per = PayrollDraft(Int("p_period"));
                var userId = Str("p_user");
                var todo = PayrollRecords(per).Where(r => (userId is null || r.UserId == userId) && r.Rec.Status == "pending"
                    && r.Rec.EndsAt is not null && r.Rec.Flags.Count == 0).ToList();
                foreach (var r in todo) _timeReviews[(r.Rec.Source, r.Rec.SourceId)] = ("approved", null);
                if (todo.Count > 0) Audit("payroll.time_approved", "payroll_time_reviews", $"all:{per.Id}");
                result = todo.Count; return true;
            }
            case "payroll_adjustments_for":
            {
                Require("reports:revenue");
                var periodId = Int("p_period");
                var userId = Str("p_user") ?? throw Err("bad_input");
                result = _hourAdjustments.Where(x => x.PeriodId == periodId && x.UserId == userId).OrderBy(x => x.CreatedAt).ThenBy(x => x.Id)
                    .Select(x => new PayrollHourAdjustment { AdjustmentId = x.Id, Quantity = x.Quantity, Note = x.Note, CreatedByName = NameOf(x.CreatedBy), CreatedAt = x.CreatedAt })
                    .ToList();
                return true;
            }
            case "payroll_add_hours":
            {
                Require("reports:revenue");
                var periodId = Int("p_period");
                var userId = Str("p_user") ?? throw Err("bad_input");
                var quantity = Math.Round(Money("p_quantity"), 2);
                var note = Str("p_note");
                if (quantity == 0 || Math.Abs(quantity) > 1000 || note is null || note.Length > 200) throw Err("bad_input");
                var per = PayrollDraft(periodId);
                if (!PayrollPay(per).Any(p => p.Person.Id == userId && p.PayType is "hourly" or "per_session")) throw Err("bad_input");
                var adj = AddReturn(_hourAdjustments, new DemoHourAdjustment
                {
                    Id = _hourAdjustments.Select(x => x.Id).DefaultIfEmpty(0).Max() + 1, PeriodId = periodId, UserId = userId,
                    Quantity = quantity, Note = note, CreatedBy = Uid, CreatedAt = SubscriptionNow
                });
                Audit("payroll.hours_adjusted", "payroll_hour_adjustments", adj.Id.ToString());
                result = new PayrollHourAdjustment { AdjustmentId = adj.Id, Quantity = adj.Quantity, Note = adj.Note, CreatedByName = NameOf(Uid), CreatedAt = adj.CreatedAt };
                return true;
            }
            case "payroll_remove_hours":
            {
                Require("reports:revenue");
                var adj = _hourAdjustments.FirstOrDefault(x => x.Id == Int("p_adjustment")) ?? throw Err("not_found");
                PayrollDraft(adj.PeriodId);
                _hourAdjustments.Remove(adj);
                Audit("payroll.hours_adjustment_removed", "payroll_hour_adjustments", adj.Id.ToString());
                return true;
            }
            case "payroll_lines_for":
            {
                Require("reports:revenue");
                var periodId = Int("p_period");
                var userId = Str("p_user") ?? throw Err("bad_input");
                result = _payrollLines.Where(l => l.PeriodId == periodId && l.UserId == userId)
                    .OrderBy(l => l.CreatedAt).ThenBy(l => l.Id).Select(ToLine).ToList();
                return true;
            }
            case "payroll_add_line":
            {
                Require("reports:revenue");
                var periodId = Int("p_period");
                var userId = Str("p_user") ?? throw Err("bad_input");
                var kind = Str("p_kind") ?? throw Err("bad_input");
                var amount = Money("p_amount");
                var note = Str("p_note");
                var category = a.ContainsKey("p_category") ? Str("p_category") : "other";
                if (kind is not ("bonus" or "deduction") || amount is <= 0 or > 10_000_000 || note is null || note.Length > 200
                    || category is null || !Categories.Contains(category) || (kind == "deduction" && category is "thirteenth_month" or "de_minimis"))
                    throw Err("bad_input");
                PayrollDraft(periodId);
                var p = All<Profile>().FirstOrDefault(x => x.Id == userId && x.IsActive && x.DeletedAt is null) ?? throw Err("bad_input");
                if (PrimaryPayrollRole(p.Id) is null) throw Err("bad_input");
                var line = AddReturn(_payrollLines, new DemoPayrollLine
                {
                    Id = _payrollLines.Count + 1, PeriodId = periodId, UserId = userId, Kind = kind,
                    Amount = Math.Round(amount, 2), Note = note, Category = category, CreatedBy = Uid, CreatedAt = SubscriptionNow
                });
                Audit("payroll.line_added", "payroll_lines", line.Id.ToString());
                result = ToLine(line);
                return true;
            }
            case "payroll_remove_line":
            {
                Require("reports:revenue");
                var line = _payrollLines.FirstOrDefault(l => l.Id == Int("p_line")) ?? throw Err("not_found");
                if (line.Kind == "salary") throw Err("bad_input");
                PayrollDraft(line.PeriodId);
                _payrollLines.Remove(line);
                Audit("payroll.line_removed", "payroll_lines", line.Id.ToString());
                return true;
            }
            case "payroll_mark_paid":
            {
                Require("reports:revenue");
                var per = PayrollDraft(Int("p_period"));
                if (ManilaDay(SubscriptionNow) < per.EndsOn) throw Err("period_not_ended");
                var rows = PayrollWithDeductions(per);
                // hourly / per-session periods are paid from the day after (later shifts that day would be lost)
                if (ManilaDay(SubscriptionNow) == per.EndsOn && rows.Any(r => r.PayType != "fixed" && !r.NoSalary)) throw Err("period_not_ended");
                if (per.StartsOn.Day == 16 && rows.Any(r => r.DeductionsOn)
                    && _payrollPeriods.FirstOrDefault(p => p.StartsOn == new DateOnly(per.StartsOn.Year, per.StartsOn.Month, 1))?.PaidAt is null)
                    throw Err("first_half_unpaid");
                if (rows.Where(r => r.Pending > 0).OrderBy(r => r.FullName).FirstOrDefault() is { } waiting) throw Err($"time_pending:{waiting.FullName}");
                if (rows.Where(r => r.Net < 0).OrderBy(r => r.FullName).FirstOrDefault() is { } neg) throw Err($"net_below_zero:{neg.FullName}");
                foreach (var row in rows.Where(r => !r.NoSalary && (r.BasePay > 0 || r.PayType != "fixed")))
                    _payrollLines.Add(new DemoPayrollLine
                    {
                        Id = _payrollLines.Count + 1, PeriodId = per.Id, UserId = row.UserId, Kind = "salary", Amount = row.BasePay,
                        PayType = row.PayType, Quantity = row.Payable, Rate = row.Rate, CreatedBy = Uid, CreatedAt = SubscriptionNow
                    });
                var month = per.StartsOn.ToString("MMM yyyy", System.Globalization.CultureInfo.InvariantCulture);
                string M(decimal v) => v.ToString("#,##0.00", System.Globalization.CultureInfo.InvariantCulture);
                foreach (var row in rows)
                    foreach (var (cat, amount, note) in new[]
                    {
                        ("sss", row.Sss, $"SSS, {month}, on ₱{M(row.MonthGross)} month pay"),
                        ("philhealth", row.PhilHealth, $"PhilHealth, {month}, on ₱{M(row.MonthBase)} base pay"),
                        ("pagibig", row.PagIbig, $"Pag-IBIG, {month}, on ₱{M(row.PagIbigBase)}"),
                        ("tax", row.Tax, $"Withholding tax, {GymMembership.Core.Services.PayrollPdf.PeriodSpan(per.StartsOn, per.EndsOn)}, on ₱{M(row.Taxable)} taxable")
                    }.Where(x => x.Item2 > 0))
                        _payrollLines.Add(new DemoPayrollLine { Id = _payrollLines.Count + 1, PeriodId = per.Id, UserId = row.UserId, Kind = "deduction",
                                                                Amount = amount, Note = note, Category = cat, CreatedBy = Uid, CreatedAt = SubscriptionNow });
                per.PaidAt = SubscriptionNow;
                per.PaidBy = Uid;
                Audit("payroll.paid", "payroll_periods", per.Id.ToString());
                result = BuildPayrollPeriod(per); return true;
            }
            default: return false;
        }
    }

    static T AddReturn<T>(List<T> list, T item) { list.Add(item); return item; }
}
