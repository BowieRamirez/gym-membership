using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;

namespace GymMembership.Core.Models;

// Mirrors supabase/migrations/0002_tables.sql. People (profiles, members, coaches) are keyed by their Auth uuid, held as a string.
// Primary keys are never sent in a write body (the app filters on them instead), because the key column is not one a user may change.
// Columns the database fills in itself (ids, timestamps, status) are marked ignoreOnInsert, and columns a user may not change
// are marked ignoreOnUpdate, so the app only ever sends what the table privileges in 0006_rls.sql allow.

public static class Status
{
    public const string Pending = "pending", Verified = "verified", Rejected = "rejected", Expired = "expired", Voided = "voided",
        Active = "active", Cancelled = "cancelled", Completed = "completed", Approved = "approved",
        Scheduled = "scheduled", NoShow = "no_show", Ended = "ended";
}

public static class PayKind { public const string Membership = "membership", Renewal = "renewal", Amenity = "amenity", WalkIn = "walk_in"; }
public static class PayMethod { public const string Qr = "qr", Cash = "cash", Gcash = "gcash", BankTransfer = "bank_transfer"; }

[Table("profiles")] public class Profile : BaseModel
{ [PrimaryKey("id")] public string Id { get; set; } = ""; [Column("username")] public string Username { get; set; } = ""; [Column("full_name")] public string FullName { get; set; } = "";
  [Column("phone")] public string? Phone { get; set; } [Column("is_active")] public bool IsActive { get; set; } = true; [Column("must_change_password")] public bool MustChangePassword { get; set; }
  // written only by the set_my_avatar function
  [Column("avatar_path", ignoreOnInsert: true, ignoreOnUpdate: true)] public string? AvatarPath { get; set; } }

[Table("roles")] public class Role : BaseModel
{ [PrimaryKey("role_id")] public int RoleId { get; set; } [Column("name")] public string Name { get; set; } = ""; }

// composite key (user_id, role_id): the app only reads this table, roles change through assign_role / revoke_role
[Table("user_roles")] public class UserRole : BaseModel
{ [PrimaryKey("user_id")] public string UserId { get; set; } = ""; [Column("role_id")] public int RoleId { get; set; } }

[Table("user_settings")] public class UserSettings : BaseModel
{ [PrimaryKey("user_id")] public string UserId { get; set; } = ""; [Column("theme")] public string Theme { get; set; } = "system";
  [Column("language")] public string Language { get; set; } = "en"; [Column("timezone")] public string Timezone { get; set; } = "Asia/Manila"; }

[Table("gym_settings")] public class GymSettings : BaseModel
{ [PrimaryKey("id")] public int Id { get; set; } = 1; [Column("gym_name")] public string GymName { get; set; } = ""; [Column("qr_image_path")] public string? QrImagePath { get; set; }
  [Column("qr_account_name")] public string? QrAccountName { get; set; } [Column("walk_in_price")] public decimal WalkInPrice { get; set; }
  [Column("payment_expiry_hours")] public int PaymentExpiryHours { get; set; } = 48; [Column("receipt_prefix")] public string ReceiptPrefix { get; set; } = "OR"; }

[Table("membership_packages")] public class MembershipPackage : BaseModel
{ [PrimaryKey("membership_package_id")] public int Id { get; set; } [Column("name")] public string Name { get; set; } = ""; [Column("description")] public string? Description { get; set; }
  [Column("price")] public decimal Price { get; set; } [Column("duration_days")] public int DurationDays { get; set; } [Column("is_active")] public bool IsActive { get; set; } = true; }

[Table("user_membership_packages")] public class UserMembershipPackage : BaseModel
{ [PrimaryKey("user_membership_package_id")] public int Id { get; set; } [Column("member_id")] public string MemberId { get; set; } = ""; [Column("membership_package_id")] public int MembershipPackageId { get; set; }
  [Column("starts_at")] public DateTime? StartsAt { get; set; } [Column("ends_at")] public DateTime? EndsAt { get; set; } [Column("status")] public string Status { get; set; } = ""; }

[Table("membership_renewals")] public class MembershipRenewal : BaseModel
{ [PrimaryKey("renewal_id")] public int Id { get; set; } [Column("user_membership_package_id")] public int UserMembershipPackageId { get; set; }
  [Column("previous_ends_at")] public DateTime PreviousEndsAt { get; set; } [Column("new_starts_at")] public DateTime NewStartsAt { get; set; } [Column("new_ends_at")] public DateTime NewEndsAt { get; set; }
  [Column("status")] public string Status { get; set; } = "pending"; [Column("completed_at")] public DateTime? CompletedAt { get; set; } }

[Table("payments")] public class Payment : BaseModel
{ [PrimaryKey("payment_id")] public int Id { get; set; } [Column("payment_code")] public string PaymentCode { get; set; } = ""; [Column("kind")] public string Kind { get; set; } = PayKind.Membership;
  [Column("method")] public string Method { get; set; } = PayMethod.Qr; [Column("status")] public string Status { get; set; } = "pending"; [Column("amount")] public decimal Amount { get; set; }
  [Column("discount_id")] public int? DiscountId { get; set; } [Column("discount_amount")] public decimal DiscountAmount { get; set; }
  [Column("member_id")] public string? MemberId { get; set; } [Column("walk_in_name")] public string? WalkInName { get; set; }
  [Column("user_membership_package_id")] public int? UserMembershipPackageId { get; set; } [Column("renewal_id")] public int? RenewalId { get; set; } [Column("user_amenity_id")] public int? UserAmenityId { get; set; }
  [Column("reference_no")] public string? ReferenceNo { get; set; } [Column("proof_path")] public string? ProofPath { get; set; } [Column("expires_at")] public DateTime? ExpiresAt { get; set; }
  [Column("recorded_by")] public string? RecordedBy { get; set; } [Column("verified_by")] public string? VerifiedBy { get; set; } [Column("verified_at")] public DateTime? VerifiedAt { get; set; }
  [Column("receipt_no")] public string? ReceiptNo { get; set; } [Column("rejected_reason")] public string? RejectedReason { get; set; }
  [Column("voided_by")] public string? VoidedBy { get; set; } [Column("voided_at")] public DateTime? VoidedAt { get; set; } [Column("void_reason")] public string? VoidReason { get; set; }
  [Column("created_at")] public DateTime CreatedAt { get; set; } }

[Table("amenities")] public class Amenity : BaseModel
{ [PrimaryKey("amenity_id")] public int Id { get; set; } [Column("name")] public string Name { get; set; } = ""; [Column("description")] public string? Description { get; set; }
  [Column("price")] public decimal Price { get; set; } [Column("duration_days")] public int? DurationDays { get; set; } [Column("is_active")] public bool IsActive { get; set; } = true;
  [Column("image_path", ignoreOnInsert: true, ignoreOnUpdate: true)] public string? ImagePath { get; set; }
  // live status: written only by set_amenity_status, so a catalog edit never sends these
  [Column("status", ignoreOnInsert: true, ignoreOnUpdate: true)] public string Availability { get; set; } = "available";
  [Column("status_reason", ignoreOnInsert: true, ignoreOnUpdate: true)] public string? StatusReason { get; set; }
  [Column("back_at", ignoreOnInsert: true, ignoreOnUpdate: true)] public DateTime? BackAt { get; set; } }

// read only: a member's saved amenities change through set_amenity_followed
[Table("amenity_follows")] public class AmenityFollow : BaseModel
{ [PrimaryKey("amenity_id")] public int AmenityId { get; set; } [Column("member_id")] public string MemberId { get; set; } = ""; }

// what set_amenity_status returns for each member it alerted (a function result, not a table)
public class NotifiedMember : BaseModel
{
  [Column("member_id")] public string MemberId { get; set; } = "";
  [Column("full_name")] public string Name { get; set; } = "";
  public NotifiedMember() { }
  public NotifiedMember(string memberId, string name) { MemberId = memberId; Name = name; }
}

[Table("user_amenities")] public class UserAmenity : BaseModel
{ [PrimaryKey("user_amenity_id")] public int Id { get; set; } [Column("member_id")] public string MemberId { get; set; } = ""; [Column("amenity_id")] public int AmenityId { get; set; }
  [Column("status")] public string Status { get; set; } = "pending"; [Column("availed_at")] public DateTime AvailedAt { get; set; } [Column("expires_at")] public DateTime? ExpiresAt { get; set; } }

// members insert (user_amenity_id, proof_path, notes); status and times are set by the database and by verify_amenity_usage
[Table("amenity_usages")] public class AmenityUsage : BaseModel
{ [PrimaryKey("amenity_usage_id")] public int Id { get; set; } [Column("user_amenity_id")] public int UserAmenityId { get; set; } [Column("used_at", ignoreOnInsert: true, ignoreOnUpdate: true)] public DateTime UsedAt { get; set; }
  [Column("proof_path")] public string? ProofPath { get; set; } [Column("status", ignoreOnInsert: true, ignoreOnUpdate: true)] public string Status { get; set; } = "pending"; }

// keyed by the coach's Auth uuid. A coach edits bio, specialty, rate and availability only
[Table("coaches")] public class Coach : BaseModel
{ [PrimaryKey("user_id")] public string UserId { get; set; } = ""; [Column("bio")] public string? Bio { get; set; }
  [Column("specialty")] public string? Specialty { get; set; } [Column("hourly_rate")] public decimal? HourlyRate { get; set; } [Column("is_available")] public bool IsAvailable { get; set; } = true; }

[Table("members")] public class Member : BaseModel
{ [PrimaryKey("user_id")] public string UserId { get; set; } = ""; [Column("goals")] public string? Goals { get; set; } }

[Table("coach_hires")] public class CoachHire : BaseModel
{ [PrimaryKey("coach_hire_id")] public int Id { get; set; } [Column("member_id")] public string MemberId { get; set; } = ""; [Column("coach_id")] public string CoachId { get; set; } = "";
  [Column("status")] public string Status { get; set; } = "active"; [Column("hired_at")] public DateTime HiredAt { get; set; } [Column("ended_at")] public DateTime? EndedAt { get; set; } }

[Table("time_requests")] public class TimeRequest : BaseModel
{ [PrimaryKey("time_request_id")] public int Id { get; set; } [Column("coach_id")] public string CoachId { get; set; } = ""; [Column("member_id")] public string? MemberId { get; set; }
  [Column("requested_by")] public string RequestedBy { get; set; } = ""; [Column("requested_start")] public DateTime RequestedStart { get; set; } [Column("requested_end")] public DateTime RequestedEnd { get; set; }
  [Column("status")] public string Status { get; set; } = "pending"; [Column("message")] public string? Message { get; set; }
  [Column("training_session_id")] public int? SessionId { get; set; } } // set when the request moves an existing session

[Table("training_sessions")] public class TrainingSession : BaseModel
{ [PrimaryKey("training_session_id")] public int Id { get; set; } [Column("coach_id")] public string CoachId { get; set; } = ""; [Column("member_id")] public string MemberId { get; set; } = "";
  [Column("title")] public string? Title { get; set; } [Column("scheduled_start")] public DateTime ScheduledStart { get; set; } [Column("scheduled_end")] public DateTime ScheduledEnd { get; set; }
  [Column("status")] public string Status { get; set; } = "scheduled"; }

[Table("session_attendance")] public class SessionAttendance : BaseModel
{ [PrimaryKey("training_session_id")] public int SessionId { get; set; } [Column("status")] public string Status { get; set; } = "present";
  [Column("checked_in_at")] public DateTime? CheckedInAt { get; set; } [Column("notes")] public string? Notes { get; set; } }

[Table("gym_check_ins")] public class GymCheckIn : BaseModel
{ [PrimaryKey("check_in_id")] public int Id { get; set; } [Column("member_id")] public string? MemberId { get; set; } [Column("payment_id")] public int? PaymentId { get; set; }
  [Column("checked_in_at")] public DateTime CheckedInAt { get; set; } }

// unread while ReadAt is null; the app may only set read_at. (No computed IsRead property: every public property is sent in a write.)
[Table("user_notifications")] public class UserNotification : BaseModel
{ [PrimaryKey("notification_id")] public int Id { get; set; } [Column("user_id", ignoreOnUpdate: true)] public string UserId { get; set; } = ""; [Column("type", ignoreOnUpdate: true)] public string Type { get; set; } = "";
  [Column("title", ignoreOnUpdate: true)] public string Title { get; set; } = ""; [Column("body", ignoreOnUpdate: true)] public string? Body { get; set; } [Column("read_at")] public DateTime? ReadAt { get; set; }
  [Column("created_at", ignoreOnUpdate: true)] public DateTime CreatedAt { get; set; } }

[Table("audit_log")] public class AuditEntry : BaseModel
{ [PrimaryKey("audit_id")] public int Id { get; set; } [Column("actor_id")] public string? ActorId { get; set; } [Column("action")] public string Action { get; set; } = "";
  [Column("entity")] public string Entity { get; set; } = ""; [Column("entity_id")] public string? EntityId { get; set; } [Column("details")] public Dictionary<string, object>? Details { get; set; }
  [Column("created_at")] public DateTime CreatedAt { get; set; } }

[Table("discounts")] public class Discount : BaseModel
{ [PrimaryKey("discount_id")] public int Id { get; set; } [Column("code")] public string Code { get; set; } = ""; [Column("percent")] public int Percent { get; set; }
  [Column("starts_at")] public DateTime StartsAt { get; set; } = DateTime.UtcNow; [Column("expires_at")] public DateTime ExpiresAt { get; set; } [Column("is_active")] public bool IsActive { get; set; } = true; }

// one conversation = one coach + one member. A message is sent as one of the two (sender_id) and only read_at changes afterwards
[Table("messages")] public class ChatMessage : BaseModel
{ [PrimaryKey("message_id")] public int Id { get; set; } [Column("coach_id")] public string CoachId { get; set; } = ""; [Column("member_id")] public string MemberId { get; set; } = "";
  [Column("sender_id")] public string SenderId { get; set; } = ""; [Column("body")] public string Body { get; set; } = "";
  [Column("sent_at", ignoreOnInsert: true, ignoreOnUpdate: true)] public DateTime SentAt { get; set; } [Column("read_at", ignoreOnInsert: true)] public DateTime? ReadAt { get; set; }
  [Column("delivered_at", ignoreOnInsert: true, ignoreOnUpdate: true)] public DateTime? DeliveredAt { get; set; }
  [Column("attachment_path", ignoreOnUpdate: true)] public string? AttachmentPath { get; set; } }

// the front desk conversation: one thread per member. The app never reads these tables; it uses chat_threads, chat_messages and send_support_message.
// The demo keeps its data here.
[Table("support_threads")] public class SupportThread : BaseModel
{ [PrimaryKey("member_id")] public string MemberId { get; set; } = ""; [Column("claimed_by")] public string? ClaimedBy { get; set; } [Column("status")] public string Status { get; set; } = "open";
  [Column("last_message_at")] public DateTime LastMessageAt { get; set; } = DateTime.UtcNow; }

[Table("support_messages")] public class SupportMessage : BaseModel
{ [PrimaryKey("message_id")] public int Id { get; set; } [Column("member_id")] public string MemberId { get; set; } = ""; [Column("sender_id")] public string SenderId { get; set; } = "";
  [Column("body")] public string Body { get; set; } = ""; [Column("sent_at")] public DateTime SentAt { get; set; } [Column("delivered_at")] public DateTime? DeliveredAt { get; set; }
  [Column("read_at")] public DateTime? ReadAt { get; set; }
  [Column("attachment_path")] public string? AttachmentPath { get; set; } }

// one row of chat_threads(): a coach chat ("coach") or a front desk thread ("support"). A function result, never written.
public class ChatThread : BaseModel
{
  [Column("kind")] public string Kind { get; set; } = "coach";
  [Column("coach_id")] public string? CoachId { get; set; }
  [Column("member_id")] public string MemberId { get; set; } = "";
  [Column("with_id")] public string? WithId { get; set; }
  [Column("with_name")] public string WithName { get; set; } = "";
  [Column("with_avatar")] public string? WithAvatar { get; set; }
  [Column("last_body")] public string? LastBody { get; set; }
  [Column("last_at")] public DateTime? LastAt { get; set; }
  [Column("last_from_me")] public bool LastFromMe { get; set; }
  [Column("unread")] public int Unread { get; set; }
  [Column("can_send")] public bool CanSend { get; set; }
  [Column("status")] public string? Status { get; set; }
  [Column("claimed_by")] public string? ClaimedBy { get; set; }
  [Column("claimed_by_name")] public string? ClaimedByName { get; set; }
  [Column("with_last_seen")] public DateTime? WithLastSeen { get; set; }

  public string Key => $"{Kind}|{CoachId}|{MemberId}";
  public bool IsSupport => Kind == "support";
  public bool HasUnread => Unread > 0;
  /// <summary>Whether to show online / last active for the other person: not for a member's own "Front desk" thread (staff come and go) and not once a hire has ended.</summary>
  public bool ShowsPresence => WithId is not null && (IsSupport || CanSend);
  public string UnreadText => Unread > 99 ? "99+" : Unread.ToString();
  public string Preview => LastBody is null ? "No messages yet" : LastFromMe ? "You: " + LastBody : LastBody;
}

// one row of chat_messages(): a message with its delivery times and, in the front desk thread for staff only, who sent it
public class ChatLine : BaseModel
{
  [Column("message_id")] public long MessageId { get; set; }
  [Column("sender_id")] public string SenderId { get; set; } = "";
  [Column("body")] public string Body { get; set; } = "";
  [Column("sent_at")] public DateTime SentAt { get; set; }
  [Column("delivered_at")] public DateTime? DeliveredAt { get; set; }
  [Column("read_at")] public DateTime? ReadAt { get; set; }
  [Column("sender_name")] public string? SenderName { get; set; }
  [Column("attachment_path")] public string? AttachmentPath { get; set; }
}

// one row of chat_search()
public class ChatSearchHit : BaseModel
{
  [Column("kind")] public string Kind { get; set; } = "coach";
  [Column("coach_id")] public string? CoachId { get; set; }
  [Column("member_id")] public string MemberId { get; set; } = "";
  [Column("with_id")] public string? WithId { get; set; }
  [Column("with_name")] public string WithName { get; set; } = "";
  [Column("snippet")] public string? Snippet { get; set; }
  [Column("sent_at")] public DateTime? SentAt { get; set; }
}

[Table("shift_posts")] public class ShiftPost : BaseModel
{ [PrimaryKey("shift_post_id")] public int Id { get; set; } [Column("coach_id", ignoreOnUpdate: true)] public string CoachId { get; set; } = ""; [Column("starts_at")] public DateTime Start { get; set; }
  [Column("ends_at")] public DateTime End { get; set; } [Column("note")] public string? Note { get; set; } [Column("status", ignoreOnInsert: true)] public string Status { get; set; } = "scheduled"; }

public record RevenueRow(DateTime Day, string Kind, string Method, long Payments, decimal Total);
