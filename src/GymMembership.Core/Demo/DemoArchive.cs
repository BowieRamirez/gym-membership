using System.Globalization;
using GymMembership.Core.Models;

namespace GymMembership.Core.Demo;

public sealed partial class DemoGateway
{
    readonly Dictionary<(string Type, string Id), (DateTime At, string Actor, string Reason)> _archiveMetadata = new();

    /// <summary>The table archive_catalog_record audits under (0024).</summary>
    static string CatalogTable(string type) => type switch { "plan" => "membership_packages", "amenity" => "amenities", _ => "discounts" };

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

    // G6/G9 (0061): what can be sold, and the newest published version in a package's line
    MembershipPackage PackageForSale(int id)
    {
        var p = All<MembershipPackage>().FirstOrDefault(p => p.Id == id && p.IsActive) ?? throw Err("not_found");
        return p.PublishedAt is null ? throw Err("item_not_for_sale") : p;
    }

    Amenity AmenityForSale(int id)
    {
        var a = All<Amenity>().FirstOrDefault(a => a.Id == id && a.IsActive) ?? throw Err("not_found");
        return a.PublishedAt is null ? throw Err("item_not_for_sale") : a;
    }

    int NewestVersion(int id)
    {
        while (All<MembershipPackage>().FirstOrDefault(p => p.ReplacesId == id && p.PublishedAt is not null) is { } next) id = next.Id;
        return id;
    }

    void PublishCatalog(string kind, int id)
    {
        RequireActive(); if (!Has("catalog:manage")) throw Err("forbidden");
        (string Name, string? Description, decimal Price, DateTime? Published, bool Active) row = kind switch
        {
            "plan" => All<MembershipPackage>().FirstOrDefault(p => p.Id == id) is { } p ? (p.Name, p.Description, p.Price, p.PublishedAt, p.IsActive) : throw Err("not_found"),
            "amenity" => All<Amenity>().FirstOrDefault(a => a.Id == id) is { } a ? (a.Name, a.Description, a.Price, a.PublishedAt, a.IsActive) : throw Err("not_found"),
            _ => throw Err("bad_input")
        };
        if (row.Published is not null) throw Err("already_published");
        if (!row.Active) throw Err("record_archived");
        if (string.IsNullOrWhiteSpace(row.Name) || string.IsNullOrWhiteSpace(row.Description) || row.Price <= 0) throw Err("missing_fields");
        if (kind == "plan")
        {
            var draft = All<MembershipPackage>().First(p => p.Id == id);
            if (All<MembershipPackage>().FirstOrDefault(p => p.Id == draft.ReplacesId && p.IsActive) is { } old) { old.IsActive = false; StampArchive("plan", old.Id.ToString(), "Replaced by a new version"); }
            if (All<MembershipPackage>().Any(p => p.Id != id && p.Name == draft.Name && p.State == CatalogState.Published)) throw Err("name_taken");
            draft.PublishedAt = SubscriptionNow;
        }
        else All<Amenity>().First(a => a.Id == id).PublishedAt = SubscriptionNow;
        Audit("catalog.publish", CatalogTable(kind), id.ToString());
    }

    int NewPackageVersion(int id)
    {
        RequireActive(); if (!Has("catalog:manage")) throw Err("forbidden");
        var p = All<MembershipPackage>().FirstOrDefault(p => p.Id == id) ?? throw Err("not_found");
        if (p.State != CatalogState.Published) throw Err("not_published");
        if (All<MembershipPackage>().Any(x => x.ReplacesId == id && x.PublishedAt is null)) throw Err("draft_exists");
        var v = Add(new MembershipPackage { Name = p.Name, Description = p.Description, Price = p.Price, DurationDays = p.DurationDays, Benefits = [.. p.Benefits], ReplacesId = id });
        Audit("catalog.new_version", "membership_packages", v.Id.ToString());
        return v.Id;
    }

    void DeleteDraft(string kind, int id)
    {
        RequireActive(); if (!Has("catalog:manage")) throw Err("forbidden");
        object row = kind switch
        {
            "plan" => All<MembershipPackage>().FirstOrDefault(p => p.Id == id) ?? throw Err("not_found"),
            "amenity" => All<Amenity>().FirstOrDefault(a => a.Id == id) ?? throw Err("not_found"),
            _ => throw Err("bad_input")
        };
        if (row is MembershipPackage { PublishedAt: not null } or Amenity { PublishedAt: not null }) throw Err("published_archive_instead");
        Table(row.GetType()).Remove(row);
        Audit("catalog.delete", CatalogTable(kind), id.ToString());
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
        Add(new AuditEntry { ActorId = Uid, Action = "catalog.archive", Entity = CatalogTable(type), EntityId = id.ToString(), CreatedAt = SubscriptionNow, Details = new() { ["reason"] = reason } });
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
                    if (p.IsActive) throw Err("not_archived");
                    if (p.PublishedAt is not null) throw Err("package_archived");   // G6 (0061): make a new version instead
                    p.IsActive = true; break;
                case "amenity":
                    var a = All<Amenity>().FirstOrDefault(a => a.Id == number) ?? throw Err("not_found");
                    if (a.IsActive) throw Err("not_archived"); a.IsActive = true; break;
                case "code":
                    var d = All<Discount>().FirstOrDefault(d => d.Id == number) ?? throw Err("not_found");
                    if (d.ExpiresAt <= SubscriptionNow || UsedUp(d)) throw Err("record_not_restorable");
                    if (d.IsActive) throw Err("not_archived"); d.IsActive = true; break;
                default: throw Err("bad_input");
            }
            var reason = _archiveMetadata.GetValueOrDefault((type, id)).Reason ?? "Unknown (legacy record)";
            Add(new AuditEntry { ActorId = Uid, Action = "catalog.restore", Entity = CatalogTable(type), EntityId = id, CreatedAt = SubscriptionNow, Details = new() { ["reason"] = reason } });
        }
        _archiveMetadata.Remove((type, id));
    }

    // 0026: pending, verified and voided payments hold a place on a code; verified and voided ones use it up
    int Held(Discount d) => All<Payment>().Count(p => p.DiscountId == d.Id && p.Status is Status.Pending or Status.Verified or Status.Voided);
    int Consumed(Discount d) => All<Payment>().Count(p => p.DiscountId == d.Id && p.Status is Status.Verified or Status.Voided);
    bool UsedUp(Discount d) => d.MaxUses is { } max && Consumed(d) >= max;

    // the database archives a code the moment its last place is verified; the demo catches up whenever codes are read or used
    void ArchiveUsedUpCodes()
    {
        foreach (var d in All<Discount>().Where(d => d.IsActive && d.ExpiresAt > SubscriptionNow && UsedUp(d)))
        { d.IsActive = false; StampArchive("code", d.Id.ToString(), "Limit reached"); }
    }

    void GuardDiscountLimit(Discount? old, Discount next)
    {
        if (next.MaxUses == old?.MaxUses && old is not null) return;
        if (next.MaxUses is null) throw Err("limit_required");
        if (old is not null && next.MaxUses < Held(old)) throw Err("limit_below_used");
    }

    List<ArchiveRecord> ArchiveRecords(Dictionary<string, object> args)
    {
        RequireArchiveAdmin();
        ArchiveUsedUpCodes();
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
            var row = Row("code", d.Id.ToString(), d.Code, expired ? "expired" : "inactive", $"{d.Percent}% discount; valid until {d.ExpiresAt.AddHours(8):yyyy-MM-dd HH:mm} (Asia/Manila)", !expired && !UsedUp(d));
            if (expired && row.ArchivedAt is null) { row.ArchivedAt = d.ExpiresAt; row.Reason = "Expired code"; }
            rows.Add(row);
        }
        return rows.Where(r => (type == "" || r.Type == type) && (status == "" || r.Status == status)
                && (from is null || r.ArchivedAt is { } a && DateOnly.FromDateTime(a.ToUniversalTime().AddHours(8)) >= from)
                && (to is null || r.ArchivedAt is { } b && DateOnly.FromDateTime(b.ToUniversalTime().AddHours(8)) <= to)
                && r.Reason.Contains(S("p_reason").Trim(), StringComparison.OrdinalIgnoreCase))
            .OrderBy(r => r.ArchivedAt is null).ThenByDescending(r => r.ArchivedAt).ThenBy(r => r.Type, StringComparer.Ordinal).ThenBy(r => r.RecordId, StringComparer.Ordinal).Skip(offset).Take(limit).ToList();
    }

    /// <summary>audit_records (0045): one page of the log, newest first, for audit:read only.</summary>
    List<AuditRecord> AuditRecords(Dictionary<string, object> args)
    {
        Require("audit:read");
        string S(string key) => Convert.ToString(args.GetValueOrDefault(key), CultureInfo.InvariantCulture) ?? "";
        DateOnly? D(string key) => string.IsNullOrEmpty(S(key)) ? null : DateOnly.ParseExact(S(key), "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var (from, to, user, area, entity) = (D("p_from"), D("p_to"), S("p_user").Trim(), S("p_area"), S("p_entity"));
        var offset = args.ContainsKey("p_offset") ? Convert.ToInt32(args["p_offset"]) : 0;
        var limit = args.ContainsKey("p_limit") ? Convert.ToInt32(args["p_limit"]) : 11;
        if (offset < 0 || limit is < 1 or > 1001 || to < from || user.Length > 100 || area.Length > 40 || entity.Length > 40) throw Err("bad_audit_filter");
        string? Name(string? id) => All<Profile>().FirstOrDefault(p => p.Id == id)?.FullName;
        DateOnly Day(DateTime at) => DateOnly.FromDateTime(at.ToUniversalTime().AddHours(8));
        return All<AuditEntry>()
            .Select(e => new AuditRecord { Id = e.Id, CreatedAt = e.CreatedAt, ActorName = Name(e.ActorId) ?? "System", Role = e.Role, IpAddress = e.IpAddress,
                Action = e.Action, Entity = e.Entity, EntityId = e.EntityId, ObjectName = e.Entity is "profiles" or "user_roles" ? Name(e.EntityId) : null, Details = e.Details })
            .Where(r => (from is null || Day(r.CreatedAt) >= from) && (to is null || Day(r.CreatedAt) <= to)
                && r.ActorName.Contains(user, StringComparison.OrdinalIgnoreCase)
                && (area == "" || r.Action.Split('.')[0] == area) && (entity == "" || r.Entity == entity))
            .OrderByDescending(r => r.CreatedAt).ThenByDescending(r => r.Id).Skip(offset).Take(limit).ToList();
    }
}
