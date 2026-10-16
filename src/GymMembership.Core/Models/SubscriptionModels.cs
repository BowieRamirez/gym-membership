using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;

namespace GymMembership.Core.Models;

[Table("membership_terms")]
public class MembershipTerm : BaseModel
{
    [PrimaryKey("term_id")] public int Id { get; set; }
    [Column("member_id")] public string MemberId { get; set; } = "";
    [Column("user_membership_package_id")] public int MembershipId { get; set; }
    [Column("payment_id")] public int? PaymentId { get; set; }
    [Column("membership_package_id")] public int PackageId { get; set; }
    [Column("package_name")] public string PackageName { get; set; } = "";
    [Column("duration_days")] public int DurationDays { get; set; }
    [Column("amount_paid")] public decimal AmountPaid { get; set; }
    [Column("starts_at")] public DateTime StartsAt { get; set; }
    [Column("ends_at")] public DateTime EndsAt { get; set; }
    [Column("snapshot_source")] public string SnapshotSource { get; set; } = "purchase";
    [Column("cancelled_at")] public DateTime? CancelledAt { get; set; }
    [Column("cancelled_by")] public string? CancelledBy { get; set; }
    [Column("cancellation_reason")] public string? CancellationReason { get; set; }
    [Column("created_at")] public DateTime CreatedAt { get; set; }
}

[Table("refund_requests")]
public class RefundRequest : BaseModel
{
    [PrimaryKey("refund_request_id")] public int Id { get; set; }
    [Column("term_id")] public int TermId { get; set; }
    [Column("payment_id")] public int PaymentId { get; set; }
    [Column("member_id")] public string MemberId { get; set; } = "";
    [Column("amount")] public decimal Amount { get; set; }
    [Column("reason")] public string Reason { get; set; } = "";
    [Column("requested_at")] public DateTime RequestedAt { get; set; }
    [Column("status")] public string Status { get; set; } = "pending";
    [Column("decided_by")] public string? DecidedBy { get; set; }
    [Column("decided_at")] public DateTime? DecidedAt { get; set; }
    [Column("decision_reason")] public string? DecisionReason { get; set; }
    [Column("paid_by")] public string? PaidBy { get; set; }
    [Column("paid_at")] public DateTime? PaidAt { get; set; }
    [Column("repayment_method")] public string? RepaymentMethod { get; set; }
    [Column("repayment_reference")] public string? RepaymentReference { get; set; }
}
