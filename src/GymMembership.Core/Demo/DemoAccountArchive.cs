using GymMembership.Core.Models;

namespace GymMembership.Core.Demo;

public sealed partial class DemoGateway
{
    void RequireArchiveAdmin()
    {
        Require("users:manage");
        if (!RolesOf(Uid).Contains("admin")) throw Err("forbidden");
    }

    void CheckAccountRemoval(Profile profile)
    {
        if (profile.Id == Uid) throw Err("self_archive");
        if (RolesOf(profile.Id).Contains("admin") &&
            !All<Profile>().Any(p => p.Id != profile.Id && p.IsActive && RolesOf(p.Id).Contains("admin")))
            throw Err("last_active_admin");
    }

    void ArchiveAccount(string userId, string reason)
    {
        RequireArchiveAdmin();
        reason = reason.Trim();
        if (reason.Length is < 1 or > 500) throw Err("bad_archive_reason");
        var profile = All<Profile>().FirstOrDefault(p => p.Id == userId) ?? throw Err("not_found");
        CheckAccountRemoval(profile);
        if (profile.DeletedAt is not null) throw Err("already_processed");
        if (All<TrainingSession>().Any(s => s.Status == Status.Scheduled && (s.MemberId == userId || s.CoachId == userId)) ||
            All<CoachHire>().Any(h => h.Status == Status.Active && (h.MemberId == userId || h.CoachId == userId)) ||
            All<Payment>().Any(p => p.Status == Status.Pending && p.MemberId == userId))
            throw Err("account_has_commitments");
        profile.IsActive = false;
        profile.DeletedAt = SubscriptionNow; profile.DeletedBy = Uid; profile.DeletionReason = reason;
        Add(new AuditEntry { ActorId = Uid, Action = "user.archive", Entity = "profiles", EntityId = userId,
            CreatedAt = SubscriptionNow, Details = new() { ["reason"] = reason } });
    }

    void RestoreAccount(string userId)
    {
        RequireArchiveAdmin();
        var profile = All<Profile>().FirstOrDefault(p => p.Id == userId) ?? throw Err("not_found");
        if (profile.DeletedAt is null) throw Err("not_archived");
        Add(new AuditEntry { ActorId = Uid, Action = "user.restore", Entity = "profiles", EntityId = userId,
            CreatedAt = SubscriptionNow, Details = new() { ["reason"] = profile.DeletionReason ?? "" } });
        profile.IsActive = true;
        profile.DeletedAt = null; profile.DeletedBy = null; profile.DeletionReason = null;
    }
}
