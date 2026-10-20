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
    [JsonIgnore] public string ActorText => ArchivedBy ?? "Unknown / automatic";
    [JsonIgnore] public string TypeLabel => Type switch { "account" => "Account", "code" => "Code", "plan" => "Gym plan", "amenity" => "Amenity", _ => Type };
}
