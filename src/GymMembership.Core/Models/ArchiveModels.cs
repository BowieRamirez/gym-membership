using System.Text.Json.Serialization;
using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;

namespace GymMembership.Core.Models;

public sealed class ArchiveRecord : BaseModel
{
    [Column("record_type")] public string Type { get; set; } = "";
    [Column("record_id")] public string RecordId { get; set; } = "";
    [Column("name")] public string Name { get; set; } = "";
    [Column("status")] public string Status { get; set; } = "inactive";
    [Column("archived_at")] public DateTime? ArchivedAt { get; set; }
    [Column("archived_by")] public string? ArchivedBy { get; set; }
    [Column("reason")] public string Reason { get; set; } = "";
    [Column("details")] public string Details { get; set; } = "";
    [Column("can_restore")] public bool CanRestore { get; set; }
    [JsonIgnore] public string DateText => ArchivedAt is { } at
        ? $"{at.ToUniversalTime().AddHours(8):dd MMM yyyy, h:mm tt} (Asia/Manila)" : "Unknown (legacy record)";
    /// <summary>D13: Deleted for a deleted account, Inactive for everything else here (Status keeps "expired" for the filter).</summary>
    [JsonIgnore] public string StatusText => GymMembership.Core.Services.RecordStatus.Of(false, Status == "deleted");
    [JsonIgnore] public string ActorText => ArchivedBy ?? "Unknown / automatic";
    [JsonIgnore] public string TypeLabel => Type switch { "account" => "Account", "code" => "Code", "plan" => "Gym plan", "amenity" => "Amenity", _ => Type };
}

/// <summary>One row of audit_records (0045): an audit entry with its actor's and object's names. Read-only.</summary>
public sealed class AuditRecord : BaseModel
{
    [Column("audit_id")] public long Id { get; set; }
    [Column("created_at")] public DateTime CreatedAt { get; set; }
    [Column("actor_name")] public string ActorName { get; set; } = "System";
    [Column("role")] public string? Role { get; set; }
    [Column("ip_address")] public string? IpAddress { get; set; }
    [Column("action")] public string Action { get; set; } = "";
    [Column("entity")] public string Entity { get; set; } = "";
    [Column("entity_id")] public string? EntityId { get; set; }
    [Column("object_name")] public string? ObjectName { get; set; }
    [Column("details")] public Dictionary<string, object>? Details { get; set; }

    [JsonIgnore] public DateTime Manila => (CreatedAt.Kind == DateTimeKind.Local ? CreatedAt.ToUniversalTime() : CreatedAt).AddHours(8);   // no daylight saving
    [JsonIgnore] public string WhenText => Manila.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture);   // the CSV: sortable
    /// <summary>On screen, in the app's date words (9.8: the table showed the CSV's "2026-11-05 17:12").</summary>
    [JsonIgnore] public string ShownWhen => Manila.ToString("ddd d MMM yyyy, h:mm tt", System.Globalization.CultureInfo.InvariantCulture);
    /// <summary>Blank for entries written before 0045 (D7: not guessed).</summary>
    [JsonIgnore] public string RoleText => Role is null ? "" : GymMembership.Core.Services.UserRow.RoleTitle(Role);
    [JsonIgnore] public string AreaText => GymMembership.Core.Services.AuditLabels.Area(Action);
    [JsonIgnore] public string ActionText => GymMembership.Core.Services.AuditLabels.Action(Action);
    [JsonIgnore] public string ObjectTypeText => GymMembership.Core.Services.AuditLabels.Object(Entity);
    [JsonIgnore] public string ObjectText => ObjectName ?? EntityId ?? "";
    [JsonIgnore] public string DetailsText => GymMembership.Core.Services.AuditLabels.Details(Details);
}

public sealed record AuditFilter(DateOnly? From = null, DateOnly? To = null, string? User = null, string? Area = null, string? Entity = null);
public sealed record AuditPage(IReadOnlyList<AuditRecord> Rows, bool HasMore);
