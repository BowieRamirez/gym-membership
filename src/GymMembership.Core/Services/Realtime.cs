namespace GymMembership.Core.Services;

/// <summary>"Active 2 h ago", in words. Empty when the person has never been seen.</summary>
public static class ActiveAgo
{
    public static string Text(DateTime? seen, DateTime nowUtc)
    {
        if (seen is not { } s) return "";
        var utc = s.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(s, DateTimeKind.Utc) : s.ToUniversalTime();   // the server sends UTC
        var ago = nowUtc - utc;
        if (ago < TimeSpan.FromMinutes(2)) return "Active just now";   // also covers a clock a little ahead
        if (ago < TimeSpan.FromHours(1)) return $"Active {(int)ago.TotalMinutes} min ago";
        if (ago < TimeSpan.FromHours(24)) return $"Active {(int)ago.TotalHours} h ago";
        return $"Active {(int)ago.TotalDays} d ago";
    }
}
