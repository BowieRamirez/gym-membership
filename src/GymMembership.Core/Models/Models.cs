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

public static class PayKind
{
    public const string Membership = "membership", Renewal = "renewal", Amenity = "amenity", WalkIn = "walk_in", CoachAddon = "coach_addon";
    /// <summary>What a kind is called on screen (9.8: Revenue showed the codes). Extension, as the payment lists say for a renewal.</summary>
    public static string Name(string k) => k switch { Membership => "Membership", Renewal => "Extension", Amenity => "Amenity", WalkIn => "Day pass", CoachAddon => "Coach add-on", _ => k };   // cashier plan Y3
}
public static class PayMethod
{
    public const string Qr = "qr", Cash = "cash", Gcash = "gcash";   // 0055: no bank transfer
    /// <summary>What a method is called on screen and in the PDF.</summary>
    public static string Name(string m) => m switch { Cash => "Cash", Gcash => "GCash", Qr => "Online", _ => m };   // Xendit plan X7: qr is paid online
}

[Table("profiles")] public class Profile : BaseModel
{ [PrimaryKey("id")] public string Id { get; set; } = ""; [Column("username")] public string Username { get; set; } = ""; [Column("full_name")] public string FullName { get; set; } = "";
  [Column("phone")] public string? Phone { get; set; } [Column("is_active")] public bool IsActive { get; set; } = true; [Column("must_change_password")] public bool MustChangePassword { get; set; }
  // written only by the set_my_avatar function
  [Column("avatar_path", ignoreOnInsert: true, ignoreOnUpdate: true)] public string? AvatarPath { get; set; }
  [Column("deleted_at", ignoreOnInsert: true, ignoreOnUpdate: true)] public DateTime? DeletedAt { get; set; }
  [Column("deleted_by", ignoreOnInsert: true, ignoreOnUpdate: true)] public string? DeletedBy { get; set; }
  [Column("deletion_reason", ignoreOnInsert: true, ignoreOnUpdate: true)] public string? DeletionReason { get; set; }
  // full_name is built from these (0035); locked after sign-up; only correct_name changes them (0040)
  [Column("first_name", ignoreOnInsert: true, ignoreOnUpdate: true)] public string FirstName { get; set; } = "";
  [Column("last_name", ignoreOnInsert: true, ignoreOnUpdate: true)] public string LastName { get; set; } = "";
  // the sign-in email this person proved with a code; written only by confirm_my_email
  [Column("confirmed_email", ignoreOnInsert: true, ignoreOnUpdate: true)] public string? ConfirmedEmail { get; set; } }

// 0140: the name and photo of anyone the caller may see (same rows as profiles used to give), without email, phone or account fields.
// Full profile rows are only the caller's own and staff's.
[Table("public_profiles")] public class PublicProfile : BaseModel
{ [PrimaryKey("id")] public string Id { get; set; } = ""; [Column("full_name")] public string FullName { get; set; } = "";
  [Column("first_name")] public string FirstName { get; set; } = ""; [Column("last_name")] public string LastName { get; set; } = "";
  [Column("avatar_path")] public string? AvatarPath { get; set; } [Column("is_active")] public bool IsActive { get; set; } = true;
  public static PublicProfile Of(Profile p) => new() { Id = p.Id, FullName = p.FullName, FirstName = p.FirstName, LastName = p.LastName, AvatarPath = p.AvatarPath, IsActive = p.IsActive }; }

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
  [Column("payment_expiry_hours")] public int PaymentExpiryHours { get; set; } = 48; [Column("receipt_prefix")] public string ReceiptPrefix { get; set; } = "OR";
  // one opening and closing time for every day, Manila wall-clock (0028)
  [Column("opens_at")] public TimeSpan OpensAt { get; set; } = TimeSpan.FromHours(6); [Column("closes_at")] public TimeSpan ClosesAt { get; set; } = TimeSpan.FromHours(22);
  [Column("opening_float")] public decimal OpeningFloat { get; set; } = 5000;
  [Column("down_payment_percent")] public int DownPaymentPercent { get; set; } = 50;
  [Column("max_coach_sessions_per_day")] public int MaxCoachSessionsPerDay { get; set; } = 1;
  [Column("max_upcoming_coach_sessions")] public int MaxUpcomingCoachSessions { get; set; } = 1;
  [Column("max_coach_hours_per_day")] public int? MaxCoachHoursPerDay { get; set; } = 6; }   // G13 (0064): of an amenity booking's price   // F2 (0050): the drawer float a cashier starts a shift with

/// <summary>G16 (0065): a whole Manila day the gym is closed; written only by close_gym_day and reopen_gym_day.</summary>
[Table("gym_closures")] public class GymClosure : BaseModel
{ [PrimaryKey("closed_on", true)] public DateTime ClosedOn { get; set; } [Column("reason")] public string Reason { get; set; } = "";
  [Column("closed_by")] public string ClosedBy { get; set; } = ""; [Column("closed_at")] public DateTime ClosedAt { get; set; }
  public DateOnly Day => DateOnly.FromDateTime(ClosedOn); }

[Table("membership_packages")] public class MembershipPackage : BaseModel
{ [PrimaryKey("membership_package_id")] public int Id { get; set; } [Column("name")] public string Name { get; set; } = ""; [Column("description")] public string? Description { get; set; }
  [Column("price")] public decimal Price { get; set; } [Column("duration_days")] public int DurationDays { get; set; } [Column("is_active")] public bool IsActive { get; set; } = true;
  [Column("is_day_pass")] public bool IsDayPass { get; set; }   // 0098: its term ends at midnight in Manila on the day it is paid
  [Column("includes_coach")] public bool IncludesCoach { get; set; }   // 0119: E3
  [Column("coach_addon_price")] public decimal? CoachAddonPrice { get; set; }   // 0119: E4
  [System.Text.Json.Serialization.JsonIgnore] public string LengthText => IsDayPass ? "Today only, until midnight" : $"{DurationDays} days of access";
  [System.Text.Json.Serialization.JsonIgnore] public string BuyText => IsDayPass ? "Buy day pass" : "Choose next term";
  [System.Text.Json.Serialization.JsonIgnore] public string CoachText => IncludesCoach ? "Coach included" : CoachAddonPrice is { } p ? $"Add a coach: \u20B1{p:N0}" : "";
  // G6/G9 (0061): null = a draft; set only by publish_catalog_item. A version points at the package it replaces.
  [Column("published_at", ignoreOnInsert: true, ignoreOnUpdate: true)] public DateTime? PublishedAt { get; set; }
  [Column("replaces_id", ignoreOnInsert: true, ignoreOnUpdate: true)] public int? ReplacesId { get; set; }
  [Column("benefits")] public List<string> Benefits { get; set; } = [];
  [Column("image_path", ignoreOnInsert: true, ignoreOnUpdate: true)] public string? ImagePath { get; set; }   // 0013; set only by the photo upload
  [System.Text.Json.Serialization.JsonIgnore] public CatalogState State => Catalog.StateOf(IsActive, PublishedAt); }

public enum CatalogState { Draft, Published, Archived }

public static class Catalog
{
    public static CatalogState StateOf(bool isActive, DateTime? publishedAt) =>
        !isActive ? CatalogState.Archived : publishedAt is null ? CatalogState.Draft : CatalogState.Published;
}

/// <summary>G9: what a new version changes, for the side-by-side check before publishing.</summary>
public sealed record PackageChange(string Field, string Old, string New);

public static class PackageChanges
{
    public static IReadOnlyList<PackageChange> Between(MembershipPackage old, MembershipPackage next) =>
        new[]
        {
            new PackageChange("Name", old.Name, next.Name),
            new PackageChange("Description", old.Description ?? "", next.Description ?? ""),
            new PackageChange("Price", $"₱{old.Price:N2}", $"₱{next.Price:N2}"),
            new PackageChange("Duration", $"{old.DurationDays} days", $"{next.DurationDays} days"),
            new PackageChange("Benefits", string.Join("; ", old.Benefits), string.Join("; ", next.Benefits)),
            new PackageChange("Photo", old.ImagePath is null ? "None" : "Same", next.ImagePath is null ? "None" : next.ImagePath == old.ImagePath ? "Same" : "New photo"),
        }.Where(c => c.Old != c.New).ToList();
}

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
  // 0141: an online payment for a booking's balance, started at the front desk; written only by start_booking_balance_online
  [Column("balance_reservation_id", ignoreOnInsert: true, ignoreOnUpdate: true)] public int? BalanceReservationId { get; set; }
  [Column("user_membership_package_id")] public int? UserMembershipPackageId { get; set; } [Column("renewal_id")] public int? RenewalId { get; set; } [Column("user_amenity_id")] public int? UserAmenityId { get; set; }
  [Column("reference_no")] public string? ReferenceNo { get; set; } [Column("proof_path")] public string? ProofPath { get; set; } [Column("expires_at")] public DateTime? ExpiresAt { get; set; }
  [Column("recorded_by")] public string? RecordedBy { get; set; } [Column("verified_by")] public string? VerifiedBy { get; set; } [Column("verified_at")] public DateTime? VerifiedAt { get; set; }
  [Column("receipt_no")] public string? ReceiptNo { get; set; } [Column("rejected_reason")] public string? RejectedReason { get; set; }
  [Column("voided_by")] public string? VoidedBy { get; set; } [Column("voided_at")] public DateTime? VoidedAt { get; set; } [Column("void_reason")] public string? VoidReason { get; set; }
  [Column("membership_package_snapshot_id", ignoreOnInsert: true, ignoreOnUpdate: true)] public int? PackageSnapshotId { get; set; }
  // 0075: the Xendit checkout; written only by the xendit-checkout and xendit-webhook functions
  [Column("xendit_id", ignoreOnInsert: true, ignoreOnUpdate: true)] public string? XenditId { get; set; }
  [Column("online_channel", ignoreOnInsert: true, ignoreOnUpdate: true)] public string? OnlineChannel { get; set; }
  [Column("membership_package_name", ignoreOnInsert: true, ignoreOnUpdate: true)] public string? PackageSnapshotName { get; set; }
  [Column("membership_duration_days", ignoreOnInsert: true, ignoreOnUpdate: true)] public int? PackageSnapshotDays { get; set; }
  [Column("cash_shift_id", ignoreOnInsert: true, ignoreOnUpdate: true)] public int? CashShiftId { get; set; }   // F2 (0050): the drawer that took this cash
  [Column("created_at")] public DateTime CreatedAt { get; set; } }

/// <summary>D8 (0117): append-only supervisor investigation note on a closed cash shift.</summary>
public sealed class CashShiftNote
{
    [System.Text.Json.Serialization.JsonPropertyName("note_id")] public int Id { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("author")] public string Author { get; set; } = "";
    [System.Text.Json.Serialization.JsonPropertyName("note")] public string Note { get; set; } = "";
    [System.Text.Json.Serialization.JsonPropertyName("resolved")] public bool Resolved { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("created_at")] public DateTime CreatedAt { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public string Summary => $"{Author} ({CreatedAt.ToLocalTime():d MMM, h:mm tt}): {Note}" + (Resolved ? " [Resolved]" : "");
}

/// <summary>F2 (0050): a cash-drawer shift. my_cash_shift() fills the open one; cash_shifts_list() adds the cashier, count and difference.</summary>
public sealed class CashShift : BaseModel
{ [Column("shift_id")] public int Id { get; set; } [Column("cashier")] public string? Cashier { get; set; }
  [Column("opened_at")] public DateTime OpenedAt { get; set; } [Column("closed_at")] public DateTime? ClosedAt { get; set; }
  [Column("opening_float")] public decimal OpeningFloat { get; set; } [Column("cash_in")] public decimal CashIn { get; set; } [Column("cash_out")] public decimal CashOut { get; set; }
  [Column("expected")] public decimal Expected { get; set; } [Column("counted")] public decimal? Counted { get; set; } [Column("difference")] public decimal? Difference { get; set; }
  [Column("shortage")] public decimal Shortage { get; set; } [Column("overage")] public decimal Overage { get; set; }
  [Column("open_note")] public string? OpenNote { get; set; } [Column("close_note")] public string? CloseNote { get; set; }
  [Column("close_reason")] public string? CloseReason { get; set; } [Column("resolved_at")] public DateTime? ResolvedAt { get; set; }
  [Column("notes")] public List<CashShiftNote> Notes { get; set; } = new();
  // the Revenue table's words
  [System.Text.Json.Serialization.JsonIgnore] public bool IsOpen => ClosedAt is null;
  [System.Text.Json.Serialization.JsonIgnore] public bool IsOff => Difference is { } d && d != 0;
  [System.Text.Json.Serialization.JsonIgnore] public bool HasShortage => Shortage > 0;
  [System.Text.Json.Serialization.JsonIgnore] public bool HasOverage => Overage > 0;
  [System.Text.Json.Serialization.JsonIgnore] public bool IsResolved => ResolvedAt is not null;
  [System.Text.Json.Serialization.JsonIgnore] public string SpanText => $"{OpenedAt.ToLocalTime():ddd d MMM, h:mm tt}" + (ClosedAt is { } c ? $" to {c.ToLocalTime():h:mm tt}" : "");
  [System.Text.Json.Serialization.JsonIgnore] public string DifferenceText => Difference is { } d ? d == 0 ? "Matches" : CashCount.Words(d) : "–";
  [System.Text.Json.Serialization.JsonIgnore] public string ShortageText => Shortage > 0 ? $"₱{Shortage:N2}" : "–";
  [System.Text.Json.Serialization.JsonIgnore] public string OverageText => Overage > 0 ? $"₱{Overage:N2}" : "–";
  [System.Text.Json.Serialization.JsonIgnore] public string CloseReasonText => ReasonLabel(CloseReason);
  [System.Text.Json.Serialization.JsonIgnore] public string NoteText
  {
      get
      {
          var parts = new List<string>();
          if (!string.IsNullOrWhiteSpace(CloseReasonText)) parts.Add(CloseReasonText);
          if (!string.IsNullOrWhiteSpace(CloseNote)) parts.Add(CloseNote);
          if (!string.IsNullOrWhiteSpace(OpenNote)) parts.Add("Opened: " + OpenNote);
          if (Notes.Count > 0) parts.Add(string.Join(" | ", Notes.Select(n => n.Summary)));
          if (IsResolved) parts.Add("Resolved");
          return parts.Count > 0 ? string.Join(" · ", parts) : "–";
      }
  }

  public static string ReasonLabel(string? code) => code switch
  {
      "wrong_change" => "Wrong change given",
      "unrecorded_sale" => "Sale not recorded",
      "counting_mistake" => "Counting mistake",
      "float_wrong" => "Float was wrong",
      "other" => "Other",
      _ => ""
  };
}

public static class CashCount
{
    /// <summary>The count against the expected cash, as the cashier says it: "Matches the expected cash", "₱50.00 short", "₱25.50 over".</summary>
    public static string Words(decimal difference) =>
        difference == 0 ? "Matches the expected cash" : $"₱{Math.Abs(difference):N2} {(difference < 0 ? "short" : "over")}";
}

[Table("amenities")] public class Amenity : BaseModel
{ [PrimaryKey("amenity_id")] public int Id { get; set; } [Column("name")] public string Name { get; set; } = ""; [Column("description")] public string? Description { get; set; }
  [Column("price")] public decimal Price { get; set; } [Column("duration_days")] public int? DurationDays { get; set; } [Column("is_active")] public bool IsActive { get; set; } = true;
  [Column("image_path", ignoreOnInsert: true, ignoreOnUpdate: true)] public string? ImagePath { get; set; }
  [Column("published_at", ignoreOnInsert: true, ignoreOnUpdate: true)] public DateTime? PublishedAt { get; set; }   // G6/G7 (0061): null = a draft
  [System.Text.Json.Serialization.JsonIgnore] public CatalogState State => Catalog.StateOf(IsActive, PublishedAt);
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
{ [Column("is_session", ignoreOnInsert: true, ignoreOnUpdate: true)] public bool IsSession { get; set; }
  [PrimaryKey("user_amenity_id")] public int Id { get; set; } [Column("member_id")] public string MemberId { get; set; } = ""; [Column("amenity_id")] public int AmenityId { get; set; }
  [Column("status")] public string Status { get; set; } = "pending"; [Column("availed_at")] public DateTime AvailedAt { get; set; } [Column("expires_at")] public DateTime? ExpiresAt { get; set; } }

// members insert (user_amenity_id, proof_path, notes); status and times are set by the database and by verify_amenity_usage
[Table("amenity_usages")] public class AmenityUsage : BaseModel
{ [PrimaryKey("amenity_usage_id")] public int Id { get; set; } [Column("user_amenity_id")] public int UserAmenityId { get; set; } [Column("used_at", ignoreOnInsert: true, ignoreOnUpdate: true)] public DateTime UsedAt { get; set; }
  [Column("proof_path")] public string? ProofPath { get; set; } [Column("status", ignoreOnInsert: true, ignoreOnUpdate: true)] public string Status { get; set; } = "pending"; }

// keyed by the coach's Auth uuid. A coach edits bio, specialty, rate and availability only
[Table("coaches")] public class Coach : BaseModel
{ [PrimaryKey("user_id")] public string UserId { get; set; } = ""; [Column("bio")] public string? Bio { get; set; }
  [Column("specialty")] public string? Specialty { get; set; } [Column("is_available")] public bool IsAvailable { get; set; } = true; }

[Table("members")] public class Member : BaseModel
{ [PrimaryKey("user_id")] public string UserId { get; set; } = ""; [Column("goals")] public string? Goals { get; set; } }

[Table("coach_hires")] public class CoachHire : BaseModel
{ [PrimaryKey("coach_hire_id")] public int Id { get; set; } [Column("member_id")] public string MemberId { get; set; } = ""; [Column("coach_id")] public string CoachId { get; set; } = "";
  [Column("status")] public string Status { get; set; } = "active"; [Column("hired_at")] public DateTime HiredAt { get; set; } [Column("ended_at")] public DateTime? EndedAt { get; set; } }

[Table("time_requests")] public class TimeRequest : BaseModel
{ [PrimaryKey("time_request_id")] public int Id { get; set; } [Column("coach_id")] public string CoachId { get; set; } = ""; [Column("member_id")] public string? MemberId { get; set; }
  [Column("requested_by")] public string RequestedBy { get; set; } = ""; [Column("requested_start")] public DateTime RequestedStart { get; set; } [Column("requested_end")] public DateTime RequestedEnd { get; set; }
  [Column("status")] public string Status { get; set; } = "pending"; [Column("message")] public string? Message { get; set; }
  [Column("training_session_id")] public int? SessionId { get; set; }  // set when the request moves an existing session
  // 0031: optional amenity, and the coach's current counter-proposal (version guards against accepting a stale one)
  [Column("amenity_id")] public int? AmenityId { get; set; } [Column("proposed_start")] public DateTime? ProposedStart { get; set; } [Column("proposed_end")] public DateTime? ProposedEnd { get; set; }
  [Column("proposed_amenity_id")] public int? ProposedAmenityId { get; set; } [Column("proposal_reason")] public string? ProposalReason { get; set; }
  [Column("proposal_expires_at")] public DateTime? ProposalExpiresAt { get; set; } [Column("decline_reason")] public string? DeclineReason { get; set; }
  [Column("version")] public int Version { get; set; } }

[Table("training_sessions")] public class TrainingSession : BaseModel
{ [PrimaryKey("training_session_id")] public int Id { get; set; } [Column("coach_id")] public string CoachId { get; set; } = ""; [Column("member_id")] public string MemberId { get; set; } = "";
  [Column("title")] public string? Title { get; set; } [Column("scheduled_start")] public DateTime ScheduledStart { get; set; } [Column("scheduled_end")] public DateTime ScheduledEnd { get; set; }
  [Column("status")] public string Status { get; set; } = "scheduled";
  [Column("payment_id")] public int? PaymentId { get; set; }
  [Column("held_until")] public DateTime? HeldUntil { get; set; }
  [Column("is_paid_addon")] public bool IsPaidAddon { get; set; }
  [Column("session_code")] public string? SessionCode { get; set; }
  [Column("walk_in_name")] public string? WalkInName { get; set; }
  [Column("checked_in_at")] public DateTime? CheckedInAt { get; set; }
  [Column("coach_arrived_at")] public DateTime? CoachArrivedAt { get; set; } }

[Table("session_attendance")] public class SessionAttendance : BaseModel
{ [PrimaryKey("training_session_id")] public int SessionId { get; set; } [Column("status")] public string Status { get; set; } = "present";
  [Column("checked_in_at")] public DateTime? CheckedInAt { get; set; } [Column("notes")] public string? Notes { get; set; } }

[Table("damage_reports")] public class DamageReport : BaseModel
{
  [PrimaryKey("damage_report_id")] public int Id { get; set; }
  [Column("training_session_id")] public int? TrainingSessionId { get; set; }
  [Column("amenity_id")] public int? AmenityId { get; set; }
  [Column("coach_id")] public string? CoachId { get; set; }
  [Column("reported_by")] public string ReportedBy { get; set; } = "";
  [Column("description")] public string Description { get; set; } = "";
  [Column("photo_path")] public string? PhotoPath { get; set; }
  [Column("status")] public string Status { get; set; } = "open";
  [Column("review_note")] public string? ReviewNote { get; set; }
  [Column("reviewed_by")] public string? ReviewedBy { get; set; }
  [Column("reviewed_at")] public DateTime? ReviewedAt { get; set; }
  [Column("created_at")] public DateTime CreatedAt { get; set; }
}

[Table("gym_check_ins")] public class GymCheckIn : BaseModel
{ [Column("method", ignoreOnInsert: true, ignoreOnUpdate: true)] public string? Method { get; set; }
  [Column("client_ref", ignoreOnInsert: true, ignoreOnUpdate: true)] public string? ClientRef { get; set; }
  [Column("recorded_by", ignoreOnInsert: true, ignoreOnUpdate: true)] public string? RecordedBy { get; set; }
  [Column("reason", ignoreOnInsert: true, ignoreOnUpdate: true)] public string? Reason { get; set; }   // F8: why an Admin skipped the PIN
  [Column("id_type", ignoreOnInsert: true, ignoreOnUpdate: true)] public string? IdType { get; set; }   // G20 (0066): the ID the desk looked at
  [PrimaryKey("check_in_id")] public int Id { get; set; } [Column("member_id")] public string? MemberId { get; set; } [Column("payment_id")] public int? PaymentId { get; set; }
  [Column("checked_in_at")] public DateTime CheckedInAt { get; set; } }

// unread while ReadAt is null; the app may only set read_at. (No computed IsRead property: every public property is sent in a write.)
[Table("user_notifications")] public class UserNotification : BaseModel
{ [PrimaryKey("notification_id")] public int Id { get; set; } [Column("user_id", ignoreOnUpdate: true)] public string UserId { get; set; } = ""; [Column("type", ignoreOnUpdate: true)] public string Type { get; set; } = "";
  [Column("title", ignoreOnUpdate: true)] public string Title { get; set; } = ""; [Column("body", ignoreOnUpdate: true)] public string? Body { get; set; } [Column("read_at")] public DateTime? ReadAt { get; set; }
  [Column("created_at", ignoreOnUpdate: true)] public DateTime CreatedAt { get; set; } }

[Table("audit_log")] public class AuditEntry : BaseModel
{ [PrimaryKey("audit_id")] public int Id { get; set; } [Column("actor_id")] public string? ActorId { get; set; } [Column("action")] public string Action { get; set; } = "";
  [Column("entity")] public string Entity { get; set; } = ""; [Column("entity_id")] public string? EntityId { get; set; } [Column("details")] public Dictionary<string, object>? Details { get; set; }
  [Column("created_at")] public DateTime CreatedAt { get; set; }
  // 0045 fills these inside audit(); the app never writes the log (the demo stores them here)
  [Column("ip_address")] public string? IpAddress { get; set; } [Column("role")] public string? Role { get; set; } }

[Table("discounts")] public class Discount : BaseModel
{ [PrimaryKey("discount_id")] public int Id { get; set; } [Column("code")] public string Code { get; set; } = ""; [Column("percent")] public int Percent { get; set; }
  [Column("starts_at")] public DateTime StartsAt { get; set; } = DateTime.UtcNow; [Column("expires_at")] public DateTime ExpiresAt { get; set; } [Column("is_active")] public bool IsActive { get; set; } = true;
  [Column("max_uses")] public int? MaxUses { get; set; }   // null: a code from before limits existed, unlimited
  [Column("show_on_packages")] public bool ShowOnPackages { get; set; }   // F5/G4: off keeps the code private
  [Column("paused")] public bool Paused { get; set; }   // G1 (0060): turned off for now; still on the Discounts page
  [Column("created_at")] public DateTime CreatedAt { get; set; } }

/// <summary>G3 (0060): a package or amenity a code fits; ItemId null means every package (or every amenity).</summary>
[Table("discount_items")] public class DiscountItem : BaseModel
{ [Column("discount_id")] public int DiscountId { get; set; } [Column("kind")] public string Kind { get; set; } = ""; [Column("item_id")] public int? ItemId { get; set; }
  public static DiscountItem Package(int? id) => new() { Kind = "package", ItemId = id };
  public static DiscountItem Amenity(int? id) => new() { Kind = "amenity", ItemId = id };
  public static DiscountItem CoachAddon() => new() { Kind = "coach", ItemId = null };
  /// <summary>Every package and every amenity: what every code fitted before 0060.</summary>
  public static IReadOnlyList<DiscountItem> Everything => [Package(null), Amenity(null)]; }

/// <summary>G4 (0060): a code this person can still use, from my_discount_codes(), with what it fits.</summary>
public sealed class AvailableCode : BaseModel
{ [Column("code")] public string Code { get; set; } = ""; [Column("percent")] public int Percent { get; set; }
  [Column("expires_at")] public DateTime ExpiresAt { get; set; } [Column("created_at")] public DateTime CreatedAt { get; set; }
  [Column("all_packages")] public bool AllPackages { get; set; } [Column("package_ids")] public List<int> PackageIds { get; set; } = [];
  [Column("all_amenities")] public bool AllAmenities { get; set; } [Column("amenity_ids")] public List<int> AmenityIds { get; set; } = [];
  /// <summary>Filled by the service from the catalog names: "All packages", "Monthly and Annual", "Pool".</summary>
  [System.Text.Json.Serialization.JsonIgnore] public string FitsText { get; set; } = "";
  [System.Text.Json.Serialization.JsonIgnore] public bool FitsAnyPackage => AllPackages || PackageIds.Count > 0;
  /// <summary>G4: made in the last 7 days.</summary>
  public bool IsNewAt(DateTime nowUtc) => nowUtc - CreatedAt.ToUniversalTime() < TimeSpan.FromDays(7);
  [System.Text.Json.Serialization.JsonIgnore] public bool IsNew => IsNewAt(DateTime.UtcNow);
  /// <summary>"For all packages", "For Monthly, Annual": the staff wording, lower-cased where it runs on from "For".</summary>
  [System.Text.Json.Serialization.JsonIgnore] public string ForText => GymMembership.Core.Services.DiscountFits.For(FitsText);
  /// <summary>ExpiresAt is the midnight that ends the last day, as on the Discounts page.</summary>
  [System.Text.Json.Serialization.JsonIgnore] public string LastDayText => $"Last day {ExpiresAt.AddTicks(-1).ToLocalTime():d MMM yyyy}"; }

/// <summary>Owner fixes 2026-11-19 Q12 (0107): result of check_discount_code as the member types a code.</summary>
public sealed class CodeCheck : BaseModel
{
    [Column("percent")] public int? Percent { get; set; }
    [Column("amount_off")] public decimal? AmountOff { get; set; }
    [Column("you_pay")] public decimal? YouPay { get; set; }
    [Column("reason")] public string? Reason { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public bool Works => Reason is null && Percent is > 0;
}

// one conversation = one coach + one member. A message is sent as one of the two (sender_id) and only read_at changes afterwards
[Table("messages")] public class ChatMessage : BaseModel
{ [PrimaryKey("message_id")] public int Id { get; set; } [Column("coach_id")] public string CoachId { get; set; } = ""; [Column("member_id")] public string MemberId { get; set; } = "";
  [Column("sender_id")] public string SenderId { get; set; } = ""; [Column("body")] public string Body { get; set; } = "";
  [Column("sent_at", ignoreOnInsert: true, ignoreOnUpdate: true)] public DateTime SentAt { get; set; } [Column("read_at", ignoreOnInsert: true)] public DateTime? ReadAt { get; set; }
  [Column("delivered_at", ignoreOnInsert: true, ignoreOnUpdate: true)] public DateTime? DeliveredAt { get; set; }
  [Column("attachment_path", ignoreOnUpdate: true)] public string? AttachmentPath { get; set; }
  // F11 (0051): set only by the server for a time request's message; the app may not write them (0006 grants only the columns above)
  [Column("kind", ignoreOnInsert: true, ignoreOnUpdate: true)] public string Kind { get; set; } = "text";
  [Column("time_request_id", ignoreOnInsert: true, ignoreOnUpdate: true)] public int? TimeRequestId { get; set; } }

// the front desk conversation: one thread per member. The app never reads these tables; it uses chat_threads, chat_messages and send_support_message.
// The demo keeps its data here.
[Table("support_threads")] public class SupportThread : BaseModel
{ [PrimaryKey("member_id")] public string MemberId { get; set; } = ""; [Column("queue")] public string Queue { get; set; } = ChatQueue.FrontDesk; [Column("claimed_by")] public string? ClaimedBy { get; set; } [Column("status")] public string Status { get; set; } = "open";
  [Column("last_message_at")] public DateTime LastMessageAt { get; set; } = DateTime.UtcNow; }

[Table("support_messages")] public class SupportMessage : BaseModel
{ [PrimaryKey("message_id")] public int Id { get; set; } [Column("member_id")] public string MemberId { get; set; } = ""; [Column("sender_id")] public string SenderId { get; set; } = "";
  [Column("queue")] public string Queue { get; set; } = ChatQueue.FrontDesk;
  [Column("body")] public string Body { get; set; } = ""; [Column("sent_at")] public DateTime SentAt { get; set; } [Column("delivered_at")] public DateTime? DeliveredAt { get; set; }
  [Column("read_at")] public DateTime? ReadAt { get; set; }
  [Column("attachment_path")] public string? AttachmentPath { get; set; } }

/// <summary>F9 (0051): the gym's three queues a member or coach can write to. Front desk is the old front desk thread.</summary>
public static class ChatQueue
{
    public const string FrontDesk = "front_desk", Admin = "admin", SystemAdmin = "system_admin";
    public static readonly string[] All = [FrontDesk, Admin, SystemAdmin];
    public static string Name(string? queue) => queue switch { Admin => "Admin", SystemAdmin => "System admin", _ => "Front desk" };
}

// one row of chat_threads(): a coach chat ("coach"), a gym thread ("support", with its queue) or the read-only "announcements". A function result, never written.
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
  [Column("queue")] public string? Queue { get; set; }

  /// <summary>The queue is added only past the Front desk, so coach and Front desk keys stay as before (the typing channel is named by it).</summary>
  public string Key => Queue is null or ChatQueue.FrontDesk ? $"{Kind}|{CoachId}|{MemberId}" : $"{Kind}|{CoachId}|{MemberId}|{Queue}";
  public bool IsSupport => Kind == "support";
  public bool IsAnnouncements => Kind == "announcements";
  /// <summary>A gym thread of the signed-in member or coach themself (WithId null), not one in a staff inbox.</summary>
  public bool IsMyGymThread => IsSupport && WithId is null;
  /// <summary>Before anything is written to Admin or System admin there is no thread yet: this stands in for it until the first message.</summary>
  public static ChatThread ForQueue(string me, string queue) =>
      new() { Kind = "support", MemberId = me, Queue = queue, WithName = ChatQueue.Name(queue), CanSend = true, Status = "open" };
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
  [Column("kind")] public string Kind { get; set; } = "text";                 // F11: "request" for an automatic message about a time request
  [Column("time_request_id")] public long? TimeRequestId { get; set; }
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
  [Column("queue")] public string? Queue { get; set; }
}

/// <summary>F10 (0051): one posted announcement, from announcements_list(), for the staff Announcements page.</summary>
public sealed class AnnouncementRow : BaseModel
{
  [Column("announcement_id")] public long Id { get; set; }
  [Column("author_name")] public string? AuthorName { get; set; }
  [Column("audience")] public string Audience { get; set; } = "everyone";
  [Column("body")] public string Body { get; set; } = "";
  [Column("posted_at")] public DateTime PostedAt { get; set; }
  [System.Text.Json.Serialization.JsonIgnore] public string AudienceText => Audience switch { "members" => "Members", "coaches" => "Coaches", _ => "Everyone" };
  [System.Text.Json.Serialization.JsonIgnore] public string WhenText => $"{PostedAt.ToLocalTime():ddd d MMM, h:mm tt}";
}

[Table("shift_posts")] public class ShiftPost : BaseModel
{ [PrimaryKey("shift_post_id")] public int Id { get; set; } [Column("coach_id", ignoreOnUpdate: true)] public string CoachId { get; set; } = ""; [Column("starts_at")] public DateTime Start { get; set; }
  [Column("ends_at")] public DateTime End { get; set; } [Column("note")] public string? Note { get; set; } [Column("status", ignoreOnInsert: true)] public string Status { get; set; } = "scheduled"; }

// a coach's physical visit (0032): open while CheckedOutAt is null. Written only through coach_check_in / coach_check_out and the closing job.
[Table("coach_visits")] public class CoachVisit : BaseModel
{ [PrimaryKey("coach_visit_id")] public int Id { get; set; } [Column("coach_id")] public string CoachId { get; set; } = ""; [Column("checked_in_at")] public DateTime CheckedInAt { get; set; }
  [Column("checked_in_by")] public string CheckedInBy { get; set; } = ""; [Column("checked_out_at")] public DateTime? CheckedOutAt { get; set; }
  [Column("checked_out_by")] public string? CheckedOutBy { get; set; } [Column("auto_closed")] public bool AutoClosed { get; set; } }

// F14 (0052): a coach who runs an amenity. Written only through set_runs_amenity.
[Table("coach_amenities")] public class CoachAmenity : BaseModel
{ [PrimaryKey("amenity_id")] public int AmenityId { get; set; } [Column("coach_id")] public string CoachId { get; set; } = ""; }

// a member's old permanent QR code (0034). F6 (0049): no longer scans; kept for history.
[Table("member_codes")] public class MemberCode : BaseModel
{ [PrimaryKey("user_id")] public string UserId { get; set; } = ""; [Column("code")] public string Code { get; set; } = ""; }

/// <summary>F6 (0049): the one-use QR pass from issue_member_pass(), good for 5 minutes.</summary>
public sealed class MemberPass : BaseModel
{ [Column("code")] public string Code { get; set; } = ""; [Column("expires_at")] public DateTime ExpiresAt { get; set; } }

// one amenity booking (0029). Written only through book_slots / cancel_reservation / end_reservation / extend_reservation.
[Table("reservations")] public class Reservation : BaseModel
{ [Column("user_amenity_id", ignoreOnInsert: true, ignoreOnUpdate: true)] public int? UserAmenityId { get; set; }
  [Column("payment_id", ignoreOnInsert: true, ignoreOnUpdate: true)] public int? PaymentId { get; set; }   // F12 (0052): a booking paid for on its own; null for pass and coach bookings
  [PrimaryKey("reservation_id")] public int Id { get; set; } [Column("member_id")] public string MemberId { get; set; } = ""; [Column("amenity_id")] public int AmenityId { get; set; }
  [Column("walk_in_name", ignoreOnInsert: true, ignoreOnUpdate: true)] public string? WalkInName { get; set; }   // 0091: a desk booking for someone with no account (MemberId is then null)
  [Column("coach_id")] public string? CoachId { get; set; } [Column("training_session_id")] public int? TrainingSessionId { get; set; }
  [Column("starts_at")] public DateTime StartsAt { get; set; } [Column("ends_at")] public DateTime EndsAt { get; set; } [Column("status")] public string Status { get; set; } = "confirmed";
  [Column("cancelled_at")] public DateTime? CancelledAt { get; set; } [Column("cancelled_by")] public string? CancelledBy { get; set; } [Column("cancel_reason")] public string? CancelReason { get; set; }
  // G13 (0064): what the booking costs, the part paid to reserve it, and the balance's payment once the desk takes it
  [Column("total_price", ignoreOnInsert: true, ignoreOnUpdate: true)] public decimal TotalPrice { get; set; }
  [Column("down_payment", ignoreOnInsert: true, ignoreOnUpdate: true)] public decimal DownPayment { get; set; }
  [Column("balance_payment_id", ignoreOnInsert: true, ignoreOnUpdate: true)] public int? BalancePaymentId { get; set; }
  [Column("held_until", ignoreOnInsert: true, ignoreOnUpdate: true)] public DateTime? HeldUntil { get; set; }   // 0148: an unpaid booking holds its slot until then
  [System.Text.Json.Serialization.JsonIgnore] public decimal Balance => TotalPrice - DownPayment; }

public record RevenueRow(DateTime Day, string Kind, string Method, long Payments, decimal Total)
{
    /// <summary>"Membership · QR · 1 payment" (9.8: was "membership · qr · 1 payment(s)").</summary>
    public string Summary => $"{PayKind.Name(Kind)} · {PayMethod.Name(Method)} · {Payments} {(Payments == 1 ? "payment" : "payments")}";
}

/// <summary>Phase 17 (0136): staff salary row for the Salaries tab.</summary>
public sealed class StaffSalary
{
    [System.Text.Json.Serialization.JsonPropertyName("user_id")] public string UserId { get; set; } = "";
    [System.Text.Json.Serialization.JsonPropertyName("full_name")] public string FullName { get; set; } = "";
    [System.Text.Json.Serialization.JsonPropertyName("role")] public string Role { get; set; } = "";
    [System.Text.Json.Serialization.JsonPropertyName("amount")] public decimal? Amount { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("pay_type")] public string PayType { get; set; } = "fixed";
    [System.Text.Json.Serialization.JsonPropertyName("personal")] public bool Personal { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("deductions_on")] public bool DeductionsOn { get; set; } = true;   // 0144, T4
    [System.Text.Json.Serialization.JsonPropertyName("contributions_on")] public bool ContributionsOn { get; set; } = true;   // 0145, U1
    [System.Text.Json.Serialization.JsonPropertyName("tax_on")] public bool TaxOn { get; set; } = true;
    [System.Text.Json.Serialization.JsonPropertyName("deductions_reason")] public string? DeductionsReason { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("updated_at")] public DateTime? UpdatedAt { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("updated_by_name")] public string? UpdatedByName { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public bool NoSalary => Amount is null;
    [System.Text.Json.Serialization.JsonIgnore] public string AmountText => Amount is { } a ? PayTypes.RateText(PayType, a) : "No salary set";
    [System.Text.Json.Serialization.JsonIgnore] public string RoleLabel => Role switch { "employee" => "Front desk", "coach" => "Coach", "subadmin" => "Admin", "admin" => "System admin", _ => Role };
}

/// <summary>Phase 19 (0137): role-level fixed salary for Front desk (employee) or Coach.</summary>
public sealed class RoleSalary
{
    [System.Text.Json.Serialization.JsonPropertyName("role")] public string Role { get; set; } = "";
    [System.Text.Json.Serialization.JsonPropertyName("amount")] public decimal? Amount { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("updated_at")] public DateTime? UpdatedAt { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("updated_by_name")] public string? UpdatedByName { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("pay_type")] public string PayType { get; set; } = "fixed";
    [System.Text.Json.Serialization.JsonIgnore] public string RoleLabel => Role == "coach" ? "Coach" : "Front desk";
}

/// <summary>Phase 17 (0136): one staff member's row inside a payroll period.</summary>
public sealed class PayrollRow
{
    [System.Text.Json.Serialization.JsonPropertyName("user_id")] public string UserId { get; set; } = "";
    [System.Text.Json.Serialization.JsonPropertyName("full_name")] public string FullName { get; set; } = "";
    [System.Text.Json.Serialization.JsonPropertyName("role")] public string Role { get; set; } = "";
    [System.Text.Json.Serialization.JsonPropertyName("sessions_count")] public int SessionsCount { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("hours")] public decimal Hours { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("salary")] public decimal Salary { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("bonuses")] public decimal Bonuses { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("deductions")] public decimal Deductions { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("net")] public decimal Net { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("no_salary")] public bool NoSalary { get; set; }
    // 0142 (H1-H17): how the pay is worked out
    [System.Text.Json.Serialization.JsonPropertyName("pay_type")] public string PayType { get; set; } = "fixed";
    [System.Text.Json.Serialization.JsonPropertyName("rate")] public decimal Rate { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("personal")] public bool Personal { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("logged")] public decimal Logged { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("adjustment")] public decimal Adjustment { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("payable")] public decimal Payable { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("base_pay")] public decimal BasePay { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("gross")] public decimal Gross { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("pending")] public int Pending { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("flagged")] public int Flagged { get; set; }
    // 0144 (T1-T12): government deductions; Deductions and Net include them, ManualDeductions doesn't
    [System.Text.Json.Serialization.JsonPropertyName("deductions_on")] public bool DeductionsOn { get; set; } = true;
    [System.Text.Json.Serialization.JsonPropertyName("manual_deductions")] public decimal ManualDeductions { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("sss")] public decimal Sss { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("philhealth")] public decimal PhilHealth { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("pagibig")] public decimal PagIbig { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("tax")] public decimal Tax { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("gov_total")] public decimal GovTotal { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("gov_cut")] public bool GovCut { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("month_gross")] public decimal MonthGross { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("month_base")] public decimal MonthBase { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("taxable")] public decimal Taxable { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("msc")] public decimal Msc { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("pagibig_base")] public decimal PagIbigBase { get; set; }
    // 0145 (U1-U6): two switches, non-taxable pay, the year's 13th month
    [System.Text.Json.Serialization.JsonPropertyName("contributions_on")] public bool ContributionsOn { get; set; } = true;
    [System.Text.Json.Serialization.JsonPropertyName("tax_on")] public bool TaxOn { get; set; } = true;
    [System.Text.Json.Serialization.JsonPropertyName("deductions_reason")] public string? DeductionsReason { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("nontaxable")] public decimal Nontaxable { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("thirteenth_used")] public decimal ThirteenthUsed { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public string RoleLabel => Role switch { "employee" => "Front desk", "coach" => "Coach", "subadmin" => "Admin", "admin" => "System admin", _ => Role };
    /// <summary>"41.5 h × ₱80.00", "12 sessions × ₱150.00" or "Fixed".</summary>
    [System.Text.Json.Serialization.JsonIgnore] public string BasisText => PayTypes.BasisText(PayType, Payable, Rate);
    [System.Text.Json.Serialization.JsonIgnore] public string HoursText => Role switch
    {
        "coach" => $"{SessionsCount} {(SessionsCount == 1 ? "session" : "sessions")} · {Hours:0.##} h",
        "employee" => $"{Hours:0.##} h at the desk",
        _ => "–"
    };
}

/// <summary>Phase 17 (0136): a twice-monthly payroll period (1st-15th or 16th-end) and its rows.</summary>
public sealed class PayrollPeriod
{
    [System.Text.Json.Serialization.JsonPropertyName("period_id")] public int PeriodId { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("starts_on")] public DateOnly StartsOn { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("ends_on")] public DateOnly EndsOn { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("paid_at")] public DateTime? PaidAt { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("paid_by_name")] public string? PaidByName { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("total_net")] public decimal TotalNet { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("total_gross")] public decimal TotalGross { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("total_gov")] public decimal TotalGov { get; set; }
    // 0145 (U7): draft / approved / paid_out; IsPaid stays "locked" (approved or paid out)
    [System.Text.Json.Serialization.JsonPropertyName("status")] public string Status { get; set; } = "draft";
    [System.Text.Json.Serialization.JsonPropertyName("paid_out_at")] public DateTime? PaidOutAt { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("paid_out_by_name")] public string? PaidOutByName { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("rows")] public List<PayrollRow> Rows { get; set; } = new();
    [System.Text.Json.Serialization.JsonIgnore] public bool IsPaid => PaidAt is not null;
}

/// <summary>0146: one paid-out payroll period summary for the Paid out table.</summary>
public sealed class PaidOutPeriod
{
    [System.Text.Json.Serialization.JsonPropertyName("period_id")] public int PeriodId { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("starts_on")] public DateOnly StartsOn { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("ends_on")] public DateOnly EndsOn { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("paid_at")] public DateTime? PaidAt { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("paid_by_name")] public string? PaidByName { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("paid_out_at")] public DateTime PaidOutAt { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("paid_out_by_name")] public string? PaidOutByName { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("people")] public int People { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("base_pay")] public decimal BasePay { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("bonuses")] public decimal Bonuses { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("gross")] public decimal Gross { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("deductions")] public decimal Deductions { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("gov_total")] public decimal GovTotal { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("net")] public decimal Net { get; set; }
}

/// <summary>Phase 17 (0136): one salary, bonus or deduction line in a payroll period.</summary>
public sealed class PayrollLine
{
    [System.Text.Json.Serialization.JsonPropertyName("line_id")] public int LineId { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("kind")] public string Kind { get; set; } = "";
    [System.Text.Json.Serialization.JsonPropertyName("amount")] public decimal Amount { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("note")] public string? Note { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("category")] public string? Category { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("pay_type")] public string? PayType { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("quantity")] public decimal? Quantity { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("rate")] public decimal? Rate { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("created_by_name")] public string? CreatedByName { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("created_at")] public DateTime CreatedAt { get; set; }
    [System.Text.Json.Serialization.JsonIgnore] public string CategoryLabel => PayTypes.CategoryLabel(Category);
}

/// <summary>0142 (H12): a shift, coach check-in or session that can pay someone, with its review status.</summary>
public sealed class PayrollTimeRecord
{
    [System.Text.Json.Serialization.JsonPropertyName("source")] public string Source { get; set; } = "";
    [System.Text.Json.Serialization.JsonPropertyName("source_id")] public long SourceId { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("starts_at")] public DateTime StartsAt { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("ends_at")] public DateTime? EndsAt { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("hours")] public decimal Hours { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("label")] public string Label { get; set; } = "";
    [System.Text.Json.Serialization.JsonPropertyName("status")] public string Status { get; set; } = "pending";
    [System.Text.Json.Serialization.JsonPropertyName("reason")] public string? Reason { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("flags")] public List<string> Flags { get; set; } = new();
    // 0152: StartsAt is the payable start (the shift start when in early); the real check-in and the shift it was against
    [System.Text.Json.Serialization.JsonPropertyName("actual_start")] public DateTime? ActualStart { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("expected_start")] public DateTime? ExpectedStart { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("expected_end")] public DateTime? ExpectedEnd { get; set; }
}

/// <summary>0142 (H5): +/- hours (or sessions) for one person in one period, with a note.</summary>
public sealed class PayrollHourAdjustment
{
    [System.Text.Json.Serialization.JsonPropertyName("adjustment_id")] public int AdjustmentId { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("quantity")] public decimal Quantity { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("note")] public string Note { get; set; } = "";
    [System.Text.Json.Serialization.JsonPropertyName("created_by_name")] public string? CreatedByName { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("created_at")] public DateTime CreatedAt { get; set; }
}

/// <summary>0142: words for pay types, rates and line categories.</summary>
public static class PayTypes
{
    public static string Money(decimal v) => "₱" + v.ToString("#,##0.00", System.Globalization.CultureInfo.InvariantCulture);
    public static string Label(string payType) => payType switch { "hourly" => "Hourly", "per_session" => "Per session", _ => "Fixed" };
    public static string RateText(string payType, decimal rate) => payType switch
    {
        "hourly" => $"{Money(rate)} per hour",
        "per_session" => $"{Money(rate)} per session",
        _ => $"{Money(rate)} per period"
    };
    public static string BasisText(string payType, decimal quantity, decimal rate) => payType switch
    {
        "hourly" => $"{quantity.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)} h × {Money(rate)}",
        "per_session" => $"{quantity.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)} {(quantity == 1 ? "session" : "sessions")} × {Money(rate)}",
        _ => "Fixed"
    };
    /// <summary>"Approved 40.5 h · corrections +1 h · payable 41.5 h × ₱80.00 = ₱3,320.00".</summary>
    public static string BasisSentence(PayrollRow r)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        if (r.PayType == "fixed") return r.NoSalary ? "No salary set" : $"Fixed {RateText("fixed", r.Rate)}";
        string Q(decimal q) => r.PayType == "hourly" ? $"{q.ToString("0.##", inv)} h" : $"{q.ToString("0.##", inv)} {(Math.Abs(q) == 1 ? "session" : "sessions")}";
        var corr = r.Adjustment == 0 ? "" : $" · corrections {(r.Adjustment > 0 ? "+" : "")}{Q(r.Adjustment)}";
        return $"Approved {Q(r.Logged)}{corr} · payable {Q(r.Payable)} × {Money(r.Rate)} = {Money(r.BasePay)}";
    }

    public static string CategoryLabel(string? category) => category switch
    {
        "regular_bonus" => "Regular bonus", "overtime" => "Overtime adjustment", "holiday" => "Holiday pay adjustment",
        "other" => "Other adjustment", "thirteenth_month" => "13th month / other benefits", "de_minimis" => "De minimis",
        "retro" => "Retro pay / adjustment", "sss" => "SSS", "philhealth" => "PhilHealth", "pagibig" => "Pag-IBIG", "tax" => "Withholding tax", _ => ""
    };
    public static bool IsGovernment(string? category) => category is "sss" or "philhealth" or "pagibig" or "tax";
}

/// <summary>0144 (T2, T3, T5): employee shares and BIR semi-monthly withholding, the same numbers as payroll_deduction_rates.
/// Used by the demo gateway; the live app gets the amounts from the server.</summary>
public static class PayrollDeductions
{
    static readonly (decimal Over, decimal Base, decimal Rate)[] Brackets =
        [(0, 0, 0), (10417, 0, 0.15m), (16667, 937.50m, 0.20m), (33333, 4270.70m, 0.25m), (83333, 16770.70m, 0.30m), (333333, 91770.70m, 0.35m)];
    static decimal R(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);
    public static decimal Msc(decimal monthPay) => Math.Clamp(500 * Math.Round(monthPay / 500, MidpointRounding.AwayFromZero), 5000, 35000);
    public static decimal Sss(decimal monthPay) => R(0.05m * Msc(monthPay));
    public static decimal PhilHealth(decimal monthBase) => R(0.025m * Math.Clamp(monthBase, 10000, 100000));
    public static decimal PagIbig(decimal monthPay) => R((monthPay <= 1500 ? 0.01m : 0.02m) * Math.Min(Math.Max(monthPay, 0), 10000));
    public static decimal Tax(decimal taxable)
    {
        var b = Brackets.Last(x => taxable > x.Over || x.Over == 0);
        return taxable > b.Over ? R(b.Base + b.Rate * (taxable - b.Over)) : 0;
    }
}
