using System.Globalization;
using System.Text;
using GymMembership.Core.Models;

namespace GymMembership.Core.Services;

/// <summary>
/// Item 14: plain words for the audit log's codes, the same in the table, filters and export. An unknown code shows as itself,
/// never blank. AuditLabelTests fails when a code written by a migration, an Edge Function or the demo has no label here.
/// </summary>
public static class AuditLabels
{
    public static readonly IReadOnlyDictionary<string, string> Actions = new Dictionary<string, string>
    {
        ["user.create"] = "Account created", ["user.archive"] = "Account deleted", ["user.restore"] = "Account restored",
        ["user.activate"] = "Account reactivated", ["user.deactivate"] = "Account deactivated",
        ["user.correct_name"] = "Name corrected", ["user.add_last_name"] = "Last name added", ["user.reset_password"] = "Password reset",
        ["role.assign"] = "Role given", ["role.revoke"] = "Role removed",
        ["payment.record"] = "Payment recorded", ["payment.verify"] = "Payment verified", ["payment.reject"] = "Payment rejected",
        ["payment.void"] = "Payment voided", ["payment.cancel"] = "Payment cancelled",
        ["refund.request"] = "Refund requested", ["refund.decide"] = "Refund decided", ["refund.paid"] = "Refund repaid",
        ["reservation.book"] = "Booking made", ["reservation.hold"] = "Booking held for payment", ["reservation.cancel"] = "Booking cancelled", ["reservation.end"] = "Booking ended",
        ["reservation.extend"] = "Booking extended",
        ["amenity.status"] = "Amenity status changed", ["amenity.runs"] = "Coach runs amenity changed",
        ["discount.create"] = "Code added", ["discount.turn_off"] = "Code turned off", ["discount.turn_on"] = "Code turned on", ["discount.delete"] = "Code deleted",
        ["catalog.archive"] = "Archived", ["catalog.restore"] = "Restored from archive",
        ["catalog.publish"] = "Published", ["catalog.new_version"] = "New version drafted", ["catalog.delete"] = "Deleted draft",
        ["cash_shift.open"] = "Cash shift started", ["cash_shift.close"] = "Cash shift ended",
        ["announcement.post"] = "Announcement posted",
    };

    /// <summary>Action type: the part of the code before the dot. The filter sends the key.</summary>
    public static readonly IReadOnlyDictionary<string, string> Areas = new Dictionary<string, string>
    {
        ["user"] = "Account", ["role"] = "Role", ["payment"] = "Payment", ["refund"] = "Refund",
        ["reservation"] = "Booking", ["amenity"] = "Amenity", ["catalog"] = "Catalog", ["cash_shift"] = "Cash shift", ["announcement"] = "Announcement", ["discount"] = "Discount code",
    };

    /// <summary>Object type: the table the entry is about. The filter sends the key.</summary>
    public static readonly IReadOnlyDictionary<string, string> Objects = new Dictionary<string, string>
    {
        ["profiles"] = "Account", ["user_roles"] = "Role", ["payments"] = "Payment", ["refund_requests"] = "Refund request",
        ["reservations"] = "Booking", ["amenities"] = "Amenity", ["membership_packages"] = "Package", ["discounts"] = "Discount code",
        ["cash_shifts"] = "Cash shift", ["announcements"] = "Announcement",
    };

    public static string Action(string code) => Actions.GetValueOrDefault(code, code);
    public static string Area(string code) => Areas.GetValueOrDefault(code.Split('.')[0], code.Split('.')[0]);
    public static string Object(string entity) => Objects.GetValueOrDefault(entity, entity);

    /// <summary>Change details as "key: value; key: value", keys made readable.</summary>
    public static string Details(IReadOnlyDictionary<string, object>? details) => details is null or { Count: 0 } ? "" :
        string.Join("; ", details.Select(d => $"{char.ToUpperInvariant(d.Key[0])}{d.Key[1..].Replace('_', ' ')}: {Convert.ToString(d.Value, CultureInfo.InvariantCulture)}"));
}

/// <summary>Builds the audit export. Every cell is quoted, and one starting with = + - @ or a tab or return gets a leading ' so a spreadsheet shows it instead of running it.</summary>
public static class AuditCsv
{
    public static readonly string[] Header = ["Date and time (Manila)", "User", "Role", "IP address", "Action type", "Action", "Object type", "Object", "Change details"];

    public static string Build(IEnumerable<AuditRecord> rows)
    {
        var csv = new StringBuilder();
        Line(csv, Header);
        foreach (var r in rows) Line(csv, [r.WhenText, r.ActorName, r.RoleText, r.IpAddress ?? "", r.AreaText, r.ActionText, r.ObjectTypeText, r.ObjectText, r.DetailsText]);
        return csv.ToString();
    }

    static void Line(StringBuilder csv, IEnumerable<string> cells) => csv.AppendJoin(',', cells.Select(Cell)).Append("\r\n");

    public static string Cell(string? value)
    {
        value ??= "";
        if (value.Length > 0 && "=+-@\t\r".Contains(value[0])) value = "'" + value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
}
