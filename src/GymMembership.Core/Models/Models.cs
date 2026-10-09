using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;

namespace GymMembership.Core.Models;

public static class Status
{
    public const string Pending = "pending", Verified = "verified", Rejected = "rejected", Expired = "expired",
        Active = "active", Cancelled = "cancelled", Completed = "completed", Approved = "approved",
        Scheduled = "scheduled", NoShow = "no_show", Ended = "ended";
}

[Table("profiles")] public class Profile : BaseModel
{ [PrimaryKey("id", true)] public string Id { get; set; } = ""; [Column("username")] public string Username { get; set; } = ""; [Column("is_active")] public bool IsActive { get; set; } = true; }

[Table("roles")] public class Role : BaseModel
{ [PrimaryKey("role_id")] public int RoleId { get; set; } [Column("name")] public string Name { get; set; } = ""; }

[Table("user_roles")] public class UserRole : BaseModel
{ [PrimaryKey("user_role_id")] public int UserRoleId { get; set; } [Column("user_id")] public string UserId { get; set; } = ""; [Column("role_id")] public int RoleId { get; set; } }

[Table("user_settings")] public class UserSettings : BaseModel
{ [PrimaryKey("user_setting_id")] public int Id { get; set; } [Column("user_id")] public string UserId { get; set; } = "";
  [Column("theme")] public string Theme { get; set; } = "system"; [Column("language")] public string Language { get; set; } = "en"; }

[Table("membership_packages")] public class MembershipPackage : BaseModel
{ [PrimaryKey("membership_package_id")] public int Id { get; set; } [Column("name")] public string Name { get; set; } = ""; [Column("description")] public string? Description { get; set; }
  [Column("price")] public decimal Price { get; set; } [Column("duration_days")] public int DurationDays { get; set; } [Column("is_active")] public bool IsActive { get; set; } = true; [Column("currency")] public string Currency { get; set; } = "USD"; }

[Table("user_membership_packages")] public class UserMembershipPackage : BaseModel
{ [PrimaryKey("user_membership_package_id")] public int Id { get; set; } [Column("user_id")] public string UserId { get; set; } = ""; [Column("membership_package_id")] public int MembershipPackageId { get; set; }
  [Column("starts_at")] public DateTime? StartsAt { get; set; } [Column("ends_at")] public DateTime? EndsAt { get; set; } [Column("status")] public string Status { get; set; } = ""; }

[Table("payments")] public class Payment : BaseModel
{ [PrimaryKey("payment_id")] public int Id { get; set; } [Column("qr")] public string Qr { get; set; } = ""; [Column("status")] public string Status { get; set; } = "pending";
  [Column("amount")] public decimal Amount { get; set; } [Column("currency")] public string Currency { get; set; } = "USD"; [Column("user_id")] public string UserId { get; set; } = "";
  [Column("user_membership_package_id")] public int? UserMembershipPackageId { get; set; } [Column("proof_path")] public string? ProofPath { get; set; } [Column("discount_code")] public string? DiscountCode { get; set; }
  [Column("created_at")] public DateTime CreatedAt { get; set; } [Column("verified_at")] public DateTime? VerifiedAt { get; set; } }

[Table("amenities")] public class Amenity : BaseModel
{ [PrimaryKey("amenity_id")] public int Id { get; set; } [Column("name")] public string Name { get; set; } = ""; [Column("description")] public string? Description { get; set; } [Column("is_active")] public bool IsActive { get; set; } = true; }

[Table("user_amenities")] public class UserAmenity : BaseModel
{ [PrimaryKey("user_amenity_id")] public int Id { get; set; } [Column("user_id")] public string UserId { get; set; } = ""; [Column("amenity_id")] public int AmenityId { get; set; } }

[Table("amenity_usages")] public class AmenityUsage : BaseModel
{ [PrimaryKey("amenity_usage_id")] public int Id { get; set; } [Column("user_amenity_id")] public int UserAmenityId { get; set; } [Column("used_at")] public DateTime UsedAt { get; set; }
  [Column("proof")] public string? Proof { get; set; } [Column("status")] public string Status { get; set; } = "pending"; }

[Table("coaches")] public class Coach : BaseModel
{ [PrimaryKey("coach_id")] public int Id { get; set; } [Column("user_id")] public string UserId { get; set; } = ""; [Column("bio")] public string? Bio { get; set; }
  [Column("specialty")] public string? Specialty { get; set; } [Column("hourly_rate")] public decimal? HourlyRate { get; set; } [Column("is_available")] public bool IsAvailable { get; set; } = true; }

[Table("members")] public class Member : BaseModel
{ [PrimaryKey("member_id")] public int Id { get; set; } [Column("user_id")] public string UserId { get; set; } = ""; }

[Table("coach_hires")] public class CoachHire : BaseModel
{ [PrimaryKey("coach_hire_id")] public int Id { get; set; } [Column("member_id")] public int MemberId { get; set; } [Column("coach_id")] public int CoachId { get; set; }
  [Column("status")] public string Status { get; set; } = "active"; [Column("ended_at")] public DateTime? EndedAt { get; set; } }

[Table("time_requests")] public class TimeRequest : BaseModel
{ [PrimaryKey("time_request_id")] public int Id { get; set; } [Column("coach_id")] public int CoachId { get; set; } [Column("member_id")] public int? MemberId { get; set; }
  [Column("requested_by")] public string RequestedBy { get; set; } = ""; [Column("requested_start")] public DateTime RequestedStart { get; set; } [Column("requested_end")] public DateTime RequestedEnd { get; set; }
  [Column("status")] public string Status { get; set; } = "pending"; [Column("message")] public string? Message { get; set; }
  [Column("training_session_id")] public int? SessionId { get; set; } } // set when the request moves an existing session

[Table("training_sessions")] public class TrainingSession : BaseModel
{ [PrimaryKey("training_session_id")] public int Id { get; set; } [Column("coach_id")] public int CoachId { get; set; } [Column("member_id")] public int MemberId { get; set; }
  [Column("title")] public string? Title { get; set; } [Column("scheduled_start")] public DateTime ScheduledStart { get; set; } [Column("scheduled_end")] public DateTime ScheduledEnd { get; set; }
  [Column("status")] public string Status { get; set; } = "scheduled"; }

[Table("user_notifications")] public class UserNotification : BaseModel
{ [PrimaryKey("notification_id")] public int Id { get; set; } [Column("user_id")] public string UserId { get; set; } = ""; [Column("type")] public string Type { get; set; } = "";
  [Column("title")] public string Title { get; set; } = ""; [Column("body")] public string? Body { get; set; } [Column("is_read")] public bool IsRead { get; set; } [Column("created_at")] public DateTime CreatedAt { get; set; } }

[Table("audit_log")] public class AuditEntry : BaseModel
{ [PrimaryKey("audit_id")] public int Id { get; set; } [Column("actor_id")] public string? ActorId { get; set; } [Column("action")] public string Action { get; set; } = "";
  [Column("entity")] public string Entity { get; set; } = ""; [Column("entity_id")] public string? EntityId { get; set; } [Column("created_at")] public DateTime CreatedAt { get; set; } }

[Table("discounts")] public class Discount : BaseModel
{ [PrimaryKey("discount_id")] public int Id { get; set; } [Column("code")] public string Code { get; set; } = ""; [Column("percent")] public int Percent { get; set; }
  [Column("expires_at")] public DateTime ExpiresAt { get; set; } [Column("is_active")] public bool IsActive { get; set; } = true; }

// one conversation = one coach + one member
[Table("messages")] public class ChatMessage : BaseModel
{ [PrimaryKey("message_id")] public int Id { get; set; } [Column("coach_id")] public int CoachId { get; set; } [Column("member_id")] public int MemberId { get; set; }
  [Column("sender_user_id")] public string SenderUserId { get; set; } = ""; [Column("body")] public string Body { get; set; } = ""; [Column("sent_at")] public DateTime SentAt { get; set; } }

[Table("shift_posts")] public class ShiftPost : BaseModel
{ [PrimaryKey("shift_post_id")] public int Id { get; set; } [Column("coach_id")] public int CoachId { get; set; } [Column("starts_at")] public DateTime Start { get; set; }
  [Column("ends_at")] public DateTime End { get; set; } [Column("note")] public string? Note { get; set; } [Column("status")] public string Status { get; set; } = "scheduled"; }

public record RevenueRow(DateTime Day, string Cur, decimal Total);
