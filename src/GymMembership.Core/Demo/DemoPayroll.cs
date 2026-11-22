using GymMembership.Core.Models;

namespace GymMembership.Core.Demo;

// 0136_payroll.sql in memory (P1-P10): fixed salaries per period, 1st-15th / 16th-end periods, manual bonus/deduction lines, mark paid.
public sealed partial class DemoGateway
{
    sealed class DemoStaffSalary
    {
        public string UserId = ""; public decimal Amount; public DateTime UpdatedAt; public string UpdatedBy = "";
    }
    sealed class DemoPayrollPeriod
    {
        public int Id; public DateOnly StartsOn; public DateOnly EndsOn; public DateTime? PaidAt; public string? PaidBy;
    }
    sealed class DemoPayrollLine
    {
        public int Id; public int PeriodId; public string UserId = ""; public string Kind = "";
        public decimal Amount; public string? Note; public string CreatedBy = ""; public DateTime CreatedAt;
    }

    readonly List<DemoStaffSalary> _staffSalaries = new();
    readonly List<DemoPayrollPeriod> _payrollPeriods = new();
    readonly List<DemoPayrollLine> _payrollLines = new();

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

    PayrollPeriod BuildPayrollPeriod(DemoPayrollPeriod per)
    {
        var paidByName = per.PaidBy is null ? null : StaffDisplayName(All<Profile>().First(p => p.Id == per.PaidBy));
        var roster = All<Profile>().Where(p =>
        {
            if (per.PaidAt is not null)
                return _payrollLines.Any(l => l.PeriodId == per.Id && l.UserId == p.Id);
            if (!p.IsActive || p.DeletedAt is not null) return false;
            var role = PrimaryPayrollRole(p.Id);
            return role is "employee" or "coach"
                || _staffSalaries.Any(s => s.UserId == p.Id)
                || _payrollLines.Any(l => l.PeriodId == per.Id && l.UserId == p.Id);
        });

        var rows = roster.Select(p =>
        {
            var role = PrimaryPayrollRole(p.Id) ?? "employee";
            var sessionsCount = 0;
            decimal hours = 0m;
            if (role == "coach")
            {
                var sessions = All<TrainingSession>().Where(ts =>
                    ts.CoachId == p.Id && ts.Status == "completed"
                    && (ts.HeldUntil is null || ts.HeldUntil > SubscriptionNow)
                    && DateOnly.FromDateTime(ts.ScheduledStart.AddHours(8)) >= per.StartsOn
                    && DateOnly.FromDateTime(ts.ScheduledStart.AddHours(8)) <= per.EndsOn).ToList();
                sessionsCount = sessions.Count;
                hours = Math.Round((decimal)sessions.Sum(ts => (ts.ScheduledEnd - ts.ScheduledStart).TotalHours), 2);
            }
            else if (role == "employee")
            {
                hours = Math.Round((decimal)_shifts.Where(s =>
                    s.Cashier == p.Id && s.ClosedAt is not null
                    && DateOnly.FromDateTime(s.OpenedAt.AddHours(8)) >= per.StartsOn
                    && DateOnly.FromDateTime(s.OpenedAt.AddHours(8)) <= per.EndsOn)
                    .Sum(s => (s.ClosedAt!.Value - s.OpenedAt).TotalHours), 2);
            }

            decimal salary;
            bool noSalary;
            if (per.PaidAt is null)
            {
                var ss = _staffSalaries.FirstOrDefault(s => s.UserId == p.Id);
                salary = ss?.Amount ?? 0m;
                noSalary = ss is null;
            }
            else
            {
                var salLines = _payrollLines.Where(l => l.PeriodId == per.Id && l.UserId == p.Id && l.Kind == "salary").ToList();
                salary = salLines.Sum(l => l.Amount);
                noSalary = salLines.Count == 0;
            }

            var bonuses = _payrollLines.Where(l => l.PeriodId == per.Id && l.UserId == p.Id && l.Kind == "bonus").Sum(l => l.Amount);
            var deductions = _payrollLines.Where(l => l.PeriodId == per.Id && l.UserId == p.Id && l.Kind == "deduction").Sum(l => l.Amount);
            return new PayrollRow
            {
                UserId = p.Id,
                FullName = StaffDisplayName(p),
                Role = role,
                SessionsCount = sessionsCount,
                Hours = hours,
                Salary = salary,
                Bonuses = bonuses,
                Deductions = deductions,
                Net = salary + bonuses - deductions,
                NoSalary = noSalary
            };
        }).OrderBy(r => RoleOrder(r.Role)).ThenBy(r => r.FullName).ToList();

        return new PayrollPeriod
        {
            PeriodId = per.Id,
            StartsOn = per.StartsOn,
            EndsOn = per.EndsOn,
            PaidAt = per.PaidAt,
            PaidByName = paidByName,
            TotalNet = rows.Sum(r => r.Net),
            Rows = rows
        };
    }

    bool PayrollCall(string fn, Dictionary<string, object> a, out object? result)
    {
        result = null;
        decimal Money(string k) => a.TryGetValue(k, out var v) && v is not null ? Convert.ToDecimal(v) : throw Err("bad_input");
        string? Str(string k) => a.TryGetValue(k, out var v) && v is string s && !string.IsNullOrWhiteSpace(s) ? s.Trim() : null;
        switch (fn)
        {
            case "set_staff_salary":
            {
                Require("reports:revenue");
                var userId = Str("p_user") ?? throw Err("bad_input");
                var amount = Money("p_amount");
                if (amount is <= 0 or > 10_000_000) throw Err("bad_input");
                if (userId == Uid) throw Err("own_salary");
                var p = All<Profile>().FirstOrDefault(x => x.Id == userId && x.IsActive && x.DeletedAt is null) ?? throw Err("bad_input");
                if (PrimaryPayrollRole(p.Id) is null) throw Err("bad_input");
                var rounded = Math.Round(amount, 2);
                var existing = _staffSalaries.FirstOrDefault(s => s.UserId == userId);
                var old = existing?.Amount;
                if (existing is null)
                    _staffSalaries.Add(new DemoStaffSalary { UserId = userId, Amount = rounded, UpdatedAt = SubscriptionNow, UpdatedBy = Uid });
                else
                {
                    existing.Amount = rounded; existing.UpdatedAt = SubscriptionNow; existing.UpdatedBy = Uid;
                }
                Audit("payroll.salary_set", "staff_salaries", userId);
                result = null; return true;
            }
            case "payroll_staff":
            {
                Require("reports:revenue");
                var list = All<Profile>()
                    .Where(p => p.IsActive && p.DeletedAt is null)
                    .Select(p => (Profile: p, Role: PrimaryPayrollRole(p.Id), Salary: _staffSalaries.FirstOrDefault(s => s.UserId == p.Id)))
                    .Where(x => x.Role is "employee" or "coach" || (x.Role is "subadmin" or "admin" && x.Salary is not null))
                    .OrderBy(x => RoleOrder(x.Role!))
                    .ThenBy(x => StaffDisplayName(x.Profile))
                    .Select(x => new StaffSalary
                    {
                        UserId = x.Profile.Id,
                        FullName = StaffDisplayName(x.Profile),
                        Role = x.Role!,
                        Amount = x.Salary?.Amount,
                        UpdatedAt = x.Salary?.UpdatedAt,
                        UpdatedByName = x.Salary is null ? null : StaffDisplayName(All<Profile>().First(u => u.Id == x.Salary.UpdatedBy))
                    }).ToList();
                result = list; return true;
            }
            case "payroll_period":
            {
                Require("reports:revenue");
                var startsOn = DateOnly.Parse(Str("p_starts_on") ?? throw Err("bad_input"));
                var endsOn = PayrollPeriodEnd(startsOn);
                var per = _payrollPeriods.FirstOrDefault(x => x.StartsOn == startsOn);
                if (per is null)
                {
                    per = new DemoPayrollPeriod { Id = _payrollPeriods.Count + 1, StartsOn = startsOn, EndsOn = endsOn };
                    _payrollPeriods.Add(per);
                }
                result = BuildPayrollPeriod(per); return true;
            }
            case "payroll_lines_for":
            {
                Require("reports:revenue");
                var periodId = Convert.ToInt32(a["p_period"]);
                var userId = Str("p_user") ?? throw Err("bad_input");
                result = _payrollLines.Where(l => l.PeriodId == periodId && l.UserId == userId)
                    .OrderBy(l => l.CreatedAt).ThenBy(l => l.Id)
                    .Select(l => new PayrollLine
                    {
                        LineId = l.Id, Kind = l.Kind, Amount = l.Amount, Note = l.Note,
                        CreatedByName = StaffDisplayName(All<Profile>().First(u => u.Id == l.CreatedBy)),
                        CreatedAt = l.CreatedAt
                    }).ToList();
                return true;
            }
            case "payroll_add_line":
            {
                Require("reports:revenue");
                var periodId = Convert.ToInt32(a["p_period"]);
                var userId = Str("p_user") ?? throw Err("bad_input");
                var kind = Str("p_kind") ?? throw Err("bad_input");
                var amount = Money("p_amount");
                var note = Str("p_note");
                if (kind is not ("bonus" or "deduction") || amount is <= 0 or > 10_000_000 || note is null || note.Length > 200)
                    throw Err("bad_input");
                var per = _payrollPeriods.FirstOrDefault(x => x.Id == periodId) ?? throw Err("bad_input");
                if (per.PaidAt is not null) throw Err("payroll_paid");
                var p = All<Profile>().FirstOrDefault(x => x.Id == userId && x.IsActive && x.DeletedAt is null) ?? throw Err("bad_input");
                if (PrimaryPayrollRole(p.Id) is null) throw Err("bad_input");
                var line = new DemoPayrollLine
                {
                    Id = _payrollLines.Count + 1, PeriodId = periodId, UserId = userId, Kind = kind,
                    Amount = Math.Round(amount, 2), Note = note, CreatedBy = Uid, CreatedAt = SubscriptionNow
                };
                _payrollLines.Add(line);
                Audit("payroll.line_added", "payroll_lines", line.Id.ToString());
                result = new PayrollLine
                {
                    LineId = line.Id, Kind = line.Kind, Amount = line.Amount, Note = line.Note,
                    CreatedByName = StaffDisplayName(All<Profile>().First(u => u.Id == Uid)),
                    CreatedAt = line.CreatedAt
                };
                return true;
            }
            case "payroll_remove_line":
            {
                Require("reports:revenue");
                var lineId = Convert.ToInt32(a["p_line"]);
                var line = _payrollLines.FirstOrDefault(l => l.Id == lineId) ?? throw Err("not_found");
                if (line.Kind == "salary") throw Err("bad_input");
                var per = _payrollPeriods.First(x => x.Id == line.PeriodId);
                if (per.PaidAt is not null) throw Err("payroll_paid");
                _payrollLines.Remove(line);
                Audit("payroll.line_removed", "payroll_lines", lineId.ToString());
                result = null; return true;
            }
            case "payroll_mark_paid":
            {
                Require("reports:revenue");
                var periodId = Convert.ToInt32(a["p_period"]);
                var per = _payrollPeriods.FirstOrDefault(x => x.Id == periodId) ?? throw Err("bad_input");
                if (per.PaidAt is not null) throw Err("payroll_paid");
                var manilaToday = DateOnly.FromDateTime(SubscriptionNow.AddHours(8));
                if (manilaToday < per.EndsOn) throw Err("period_not_ended");

                var preview = BuildPayrollPeriod(per);
                var neg = preview.Rows.FirstOrDefault(r => r.Net < 0);
                if (neg is not null) throw Err($"net_below_zero:{neg.FullName}");

                foreach (var row in preview.Rows.Where(r => !r.NoSalary && r.Salary > 0))
                {
                    _payrollLines.Add(new DemoPayrollLine
                    {
                        Id = _payrollLines.Count + 1, PeriodId = periodId, UserId = row.UserId,
                        Kind = "salary", Amount = row.Salary, Note = null, CreatedBy = Uid, CreatedAt = SubscriptionNow
                    });
                }
                per.PaidAt = SubscriptionNow;
                per.PaidBy = Uid;
                Audit("payroll.paid", "payroll_periods", periodId.ToString());
                result = BuildPayrollPeriod(per); return true;
            }
            default: return false;
        }
    }
}
