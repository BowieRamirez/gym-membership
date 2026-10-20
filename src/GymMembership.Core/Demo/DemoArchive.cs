using System.Globalization;
using GymMembership.Core.Models;

namespace GymMembership.Core.Demo;

public sealed partial class DemoGateway
{
    readonly Dictionary<(string Type, string Id), (DateTime At, string Actor, string Reason)> _archiveMetadata = new();

    void StampArchive(string type, string id, string reason) => _archiveMetadata[(type, id)] = (SubscriptionNow, Uid, reason);

    void GuardCatalogEdit(object old, object edited)
    {
        var (type, id, active, next) = (old, edited) switch
        {
            (MembershipPackage p, MembershipPackage n) => ("plan", p.Id, p.IsActive, n.IsActive),
            (Amenity a, Amenity n) => ("amenity", a.Id, a.IsActive, n.IsActive),
            (Discount d, Discount n) => ("code", d.Id, d.IsActive && d.ExpiresAt > SubscriptionNow, n.IsActive),
            _ => throw Err("bad_input")
        };
        if (!active) throw Err("record_archived");
        if (!next) StampArchive(type, id.ToString(), "Deactivated " + (type == "plan" ? "gym plan" : type));
    }

    void ArchiveCatalog(string type, int id, string reason)
    {
        RequireArchiveAdmin(); reason = reason.Trim();
        if (reason.Length is < 1 or > 500) throw Err("bad_archive_reason");
        switch (type)
        {
            case "plan":
                var plan = All<MembershipPackage>().FirstOrDefault(p => p.Id == id) ?? throw Err("not_found");
                if (!plan.IsActive) throw Err("record_archived"); plan.IsActive = false; break;
            case "amenity":
                var amenity = All<Amenity>().FirstOrDefault(p => p.Id == id) ?? throw Err("not_found");
                if (!amenity.IsActive) throw Err("record_archived"); amenity.IsActive = false; break;
            case "code":
                var code = All<Discount>().FirstOrDefault(p => p.Id == id) ?? throw Err("not_found");
                if (!code.IsActive || code.ExpiresAt <= SubscriptionNow) throw Err("record_archived"); code.IsActive = false; break;
            default: throw Err("bad_input");
        }
        StampArchive(type, id.ToString(), reason);
        Add(new AuditEntry { ActorId = Uid, Action = "catalog.archive", Entity = type, EntityId = id.ToString(), CreatedAt = SubscriptionNow, Details = new() { ["reason"] = reason } });
    }

    void RestoreArchived(string type, string id)
    {
        RequireArchiveAdmin();
        if (type == "account")
        {
            var profile = All<Profile>().FirstOrDefault(p => p.Id == id) ?? throw Err("not_found");
            if (profile.IsActive) throw Err("not_archived");
            if (profile.DeletedAt is not null) RestoreAccount(id);
            else { profile.IsActive = true; Audit("user.activate", "profiles", id); }
        }
        else
        {
            if (!int.TryParse(id, out var number)) throw Err("bad_input");
            switch (type)
            {
                case "plan":
                    var p = All<MembershipPackage>().FirstOrDefault(p => p.Id == number) ?? throw Err("not_found");
                    if (p.IsActive) throw Err("not_archived"); p.IsActive = true; break;
                case "amenity":
                    var a = All<Amenity>().FirstOrDefault(a => a.Id == number) ?? throw Err("not_found");
                    if (a.IsActive) throw Err("not_archived"); a.IsActive = true; break;
                case "code":
                    var d = All<Discount>().FirstOrDefault(d => d.Id == number) ?? throw Err("not_found");
                    if (d.ExpiresAt <= SubscriptionNow) throw Err("record_not_restorable");
                    if (d.IsActive) throw Err("not_archived"); d.IsActive = true; break;
                default: throw Err("bad_input");
            }
            var reason = _archiveMetadata.GetValueOrDefault((type, id)).Reason ?? "Unknown (legacy record)";
            Add(new AuditEntry { ActorId = Uid, Action = "catalog.restore", Entity = type, EntityId = id, CreatedAt = SubscriptionNow, Details = new() { ["reason"] = reason } });
        }
        _archiveMetadata.Remove((type, id));
    }

    List<ArchiveRecord> ArchiveRecords(Dictionary<string, object> args)
    {
        RequireArchiveAdmin();
        string S(string key) => Convert.ToString(args.GetValueOrDefault(key), CultureInfo.InvariantCulture) ?? "";
        DateOnly? D(string key) => string.IsNullOrEmpty(S(key)) ? null : DateOnly.ParseExact(S(key), "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var type = S("p_type"); var status = S("p_status"); var from = D("p_from"); var to = D("p_to");
        var offset = args.ContainsKey("p_offset") ? Convert.ToInt32(args["p_offset"]) : 0;
        var limit = args.ContainsKey("p_limit") ? Convert.ToInt32(args["p_limit"]) : 31;
        if (offset < 0 || limit is < 1 or > 101 || to < from || type is not ("" or "account" or "plan" or "code" or "amenity") || status is not ("" or "inactive" or "deleted" or "expired")) throw Err("bad_archive_filter");
        var rows = new List<ArchiveRecord>();
        ArchiveRecord Row(string kind, string id, string name, string state, string details, bool restore = true)
        {
            var known = _archiveMetadata.TryGetValue((kind, id), out var meta);
            return new() { Type = kind, RecordId = id, Name = name, Status = state, Details = details, CanRestore = restore,
                ArchivedAt = known ? meta.At : null, ArchivedBy = known ? meta.Actor : null, Reason = known ? meta.Reason : "Unknown (legacy record)" };
        }
        foreach (var p in All<Profile>().Where(p => !p.IsActive))
        {
            var row = Row("account", p.Id, p.FullName, p.DeletedAt is null ? "inactive" : "deleted", p.Username);
            if (p.DeletedAt is not null) { row.ArchivedAt = p.DeletedAt; row.ArchivedBy = p.DeletedBy; row.Reason = p.DeletionReason!; }
            rows.Add(row);
        }
        foreach (var p in All<MembershipPackage>().Where(p => !p.IsActive)) rows.Add(Row("plan", p.Id.ToString(), p.Name, "inactive", $"PHP {p.Price:F2}; {p.DurationDays} days; {p.Description}"));
        foreach (var a in All<Amenity>().Where(a => !a.IsActive)) rows.Add(Row("amenity", a.Id.ToString(), a.Name, "inactive", $"PHP {a.Price:F2}; {a.Description}"));
        foreach (var d in All<Discount>().Where(d => !d.IsActive || d.ExpiresAt <= SubscriptionNow))
        {
            var expired = d.ExpiresAt <= SubscriptionNow;
            var row = Row("code", d.Id.ToString(), d.Code, expired ? "expired" : "inactive", $"{d.Percent}% discount; valid until {d.ExpiresAt.AddHours(8):yyyy-MM-dd HH:mm} (Asia/Manila)", !expired);
            if (expired && row.ArchivedAt is null) { row.ArchivedAt = d.ExpiresAt; row.Reason = "Expired code"; }
            rows.Add(row);
        }
        return rows.Where(r => (type == "" || r.Type == type) && (status == "" || r.Status == status)
                && (from is null || r.ArchivedAt is { } a && DateOnly.FromDateTime(a.ToUniversalTime().AddHours(8)) >= from)
                && (to is null || r.ArchivedAt is { } b && DateOnly.FromDateTime(b.ToUniversalTime().AddHours(8)) <= to)
                && r.Reason.Contains(S("p_reason").Trim(), StringComparison.OrdinalIgnoreCase))
            .OrderBy(r => r.ArchivedAt is null).ThenByDescending(r => r.ArchivedAt).ThenBy(r => r.Type, StringComparer.Ordinal).ThenBy(r => r.RecordId, StringComparer.Ordinal).Skip(offset).Take(limit).ToList();
    }
}
