using System.Globalization;
using GymMembership.Core.Models;

namespace GymMembership.Core.Services;

// ===================== Amenity reservations (0029, 0052) =====================
/// <summary>One Manila day of bookings, as amenity_grid returns it. Times are UTC instants.</summary>
public sealed class AmenityGrid
{
    public DateTime Now { get; set; }
    public DateTime Opens { get; set; }
    public DateTime Closes { get; set; }
    public List<GridAmenity> Amenities { get; set; } = [];
    public List<GridBlock> Blocks { get; set; } = [];
}
public sealed class GridAmenity
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Status { get; set; } = "available";
    public DateTime? Back { get; set; }
}
/// <summary>Time an amenity is held (a booking or an unpaid hold): in use from Starts to Ends, then cooling down until Cooldown. Id is set only on your own.</summary>
public sealed class GridBlock
{
    public int Amenity { get; set; }
    public DateTime Starts { get; set; }
    public DateTime Ends { get; set; }
    public DateTime Cooldown { get; set; }
    public bool Mine { get; set; }
    public int? Id { get; set; }
}

/// <summary>The booking rules the server applies, so the sheet only offers what will go through (it rechecks everything anyway).</summary>
public static class BookingSlots
{
    public static readonly int[] Minutes = [30, 60, 90, 120];

    // one 30-minute cell: open, yours, booked, cooldown, blocked (its cooldown would run into the next booking), closed, past or outside gym hours
    public static string StateOf(AmenityGrid g, GridAmenity a, DateTime t)
    {
        if (t < g.Opens || t >= g.Closes) return "outside";
        var end = t.AddMinutes(30);
        var blocks = g.Blocks.Where(b => b.Amenity == a.Id).ToList();
        if (blocks.FirstOrDefault(b => b.Starts < end && t < b.Ends) is { } use) return use.Mine ? "yours" : "booked";
        if (t <= g.Now) return "past";
        if (blocks.Any(b => b.Ends < end && t < b.Cooldown)) return "cooldown";
        if (a.Status == "unavailable" && (a.Back is null || a.Back > t)) return "closed";
        if (blocks.Any(b => b.Starts >= end && b.Starts < end.AddMinutes(30))) return "blocked";
        return "open";
    }

    /// <summary>
    /// The lengths a booking starting at t can have. Every cell it covers must be free, and its last cell open (not "blocked"), so the
    /// 30-minute cooldown after it never runs into the next booking. Paying first (F12) also needs the start more than 1 hour away.
    /// </summary>
    public static IReadOnlyList<int> Lengths(AmenityGrid g, int amenityId, DateTime t, bool payFirst)
    {
        var a = g.Amenities.FirstOrDefault(x => x.Id == amenityId);
        if (a is null || (payFirst && t.AddHours(-1) <= g.Now)) return [];
        var fits = new List<int>();
        for (var n = 1; n <= 4; n++)
        {
            var last = StateOf(g, a, t.AddMinutes(30 * (n - 1)));
            if (last == "open") fits.Add(30 * n);
            else if (last != "blocked") break;
        }
        return fits;
    }
}

/// <summary>One of the member's bookings and what they can do with it now (F12, F13).</summary>
public sealed record BookingRow(Reservation Booking, string Amenity, DateTime NowUtc, Payment? Payment = null, RefundRequest? Refund = null)
{
    static readonly CultureInfo En = CultureInfo.InvariantCulture;
    static DateTime Manila(DateTime utc) => utc.ToUniversalTime().AddHours(8);   // Manila has no daylight saving
    static string At(DateTime utc) => Manila(utc).ToString("ddd d MMM, h:mm tt", En);
    DateTime Start => Booking.StartsAt.ToUniversalTime();
    DateTime End => Booking.EndsAt.ToUniversalTime();
    string Price => $"₱{(Payment?.Amount ?? 0):N2}";

    public string When => $"{Manila(Start).ToString("ddd d MMM, h:mm tt", En)} to {Manila(End).ToString("h:mm tt", En)}";
    public string Title => $"{Amenity} · {When}";
    public bool IsWaiting => Booking.Status == "awaiting_payment";
    bool Confirmed => Booking.Status == "confirmed";
    public bool Paid => Payment?.Status == Status.Verified;
    public bool InUse => Confirmed && Start <= NowUtc && NowUtc < End;
    public bool Ended => Confirmed && End <= NowUtc;
    /// <summary>Before the start; a paid booking only until 1 hour before it (the member's refund window).</summary>
    public bool CanCancel => Refund is null && (IsWaiting || Confirmed) && Start > NowUtc && (!Paid || Start.AddHours(-1) >= NowUtc);
    public bool CanEnd => InUse;
    /// <summary>"I didn't use it": a paid booking, from its end for 24 hours, once.</summary>
    public bool CanClaim => Ended && Paid && Refund is null && NowUtc <= End.AddHours(24);
    public bool CanSendProof => IsWaiting && Payment is { Method: PayMethod.Qr, ProofPath: null };

    /// <summary>wait (yellow), good (green) or plain, for the status chip.</summary>
    public string Tone => IsWaiting && Refund is null ? "wait" : Refund?.Status is "approved" or "paid" || (Confirmed && !Ended && Refund is null) ? "good" : "plain";
    public string StatusText => Refund switch
    {
        { Status: "pending" } => "Refund claim sent",
        { Status: "rejected" } => "Claim declined",
        { Status: "paid" } => "Refund repaid",
        { Status: "approved", Kind: "cancel" } => "Cancelled · refund approved",
        { Status: "approved" } => "Refund approved",
        _ => IsWaiting ? "Waiting for payment" : InUse ? "In use" : Ended ? "Ended" : "Confirmed"
    };
    public string Detail => Refund switch
    {
        { Status: "pending" } => "The front desk is checking it. You'll get a notification.",
        { Status: "rejected" } r => $"\"{r.DecisionReason}\" (Front desk)",
        { Status: "paid" } => $"{Price} repaid.",
        { Status: "approved", Kind: "no_show" } r => $"{Price} to be repaid at the front desk. \"{r.DecisionReason}\" (Front desk)",
        { Status: "approved" } => $"{Price} to be repaid at the front desk.",
        _ when IsWaiting && Payment is { } p => p.Method == PayMethod.Cash
            ? $"{Price} at the front desk by {At(p.ExpiresAt ?? Start)}, or the time is released."
            : p.ProofPath is null ? $"{Price} by GCash or QR. Pay by {At(p.ExpiresAt ?? Start)} or the time is released."
            : $"{Price} by GCash or QR. Proof sent; the front desk is checking it.",
        _ when InUse => $"Ends at {Manila(End).ToString("h:mm tt", En)}.",
        _ when CanClaim => $"Didn't go? Tell the front desk by {At(End.AddHours(24))} and they'll check for a refund.",
        _ when Ended => "",
        _ when Paid => Start.AddHours(-1) >= NowUtc ? $"Paid {Price}. Cancel by {At(Start.AddHours(-1))} for a full refund." : $"Paid {Price}. It starts within the hour, so it can't be cancelled now.",
        _ => Booking.CoachId is null ? "Booked with your pass." : "With your coach."
    };
}

public interface IReservationService
{
    Task<AppResult<AmenityGrid>> GridAsync(DateOnly manilaDay);
    /// <summary>F12 (0052): books with a pass or session if the member has one; otherwise holds the time and creates the payment (method qr or cash).</summary>
    Task<AppResult<Reservation>> BookAsync(int amenityId, DateTime startUtc, int minutes, string method);
    /// <summary>Upcoming bookings soonest first, then ones that ended in the last day or have a refund still open.</summary>
    Task<AppResult<IReadOnlyList<BookingRow>>> MyBookingsAsync();
    Task<AppResult<bool>> CancelAsync(int reservationId);
    Task<AppResult<bool>> EndAsync(int reservationId);
    /// <summary>F13: "I didn't use it", within 24 hours of the end of a paid booking.</summary>
    Task<AppResult<bool>> ClaimAsync(int reservationId, string reason);
}

public sealed class ReservationService(ISupabaseGateway gw) : IReservationService
{
    public Task<AppResult<AmenityGrid>> GridAsync(DateOnly manilaDay) => Safe.RunAsync(async () =>
    {
        var g = await gw.RpcAsync<AmenityGrid>("amenity_grid", new() { ["p_day"] = manilaDay.ToString("yyyy-MM-dd") });
        // the JSON may come back as local times; everything below compares UTC instants
        g.Now = g.Now.ToUniversalTime(); g.Opens = g.Opens.ToUniversalTime(); g.Closes = g.Closes.ToUniversalTime();
        foreach (var b in g.Blocks) { b.Starts = b.Starts.ToUniversalTime(); b.Ends = b.Ends.ToUniversalTime(); b.Cooldown = b.Cooldown.ToUniversalTime(); }
        foreach (var a in g.Amenities) a.Back = a.Back?.ToUniversalTime();
        return g;
    });

    public Task<AppResult<Reservation>> BookAsync(int amenityId, DateTime startUtc, int minutes, string method) => Safe.RunAsync(() =>
        gw.RpcAsync<Reservation>("book_amenity", new()
        {
            ["p_amenity"] = amenityId, ["p_start"] = startUtc.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ"), ["p_minutes"] = minutes, ["p_method"] = method
        }));

    public Task<AppResult<IReadOnlyList<BookingRow>>> MyBookingsAsync() => Safe.RunAsync<IReadOnlyList<BookingRow>>(async () =>
    {
        var uid = gw.CurrentUserId; var now = DateTime.UtcNow;
        var mine = await gw.ListAsync<Reservation>(r => r.MemberId == uid);
        var pays = (await gw.ListAsync<Payment>(p => p.MemberId == uid)).ToDictionary(p => p.Id);
        var refunds = (await gw.ListAsync<RefundRequest>(r => r.MemberId == uid)).Where(r => r.ReservationId is not null).ToDictionary(r => r.ReservationId!.Value);
        var names = (await gw.ListAsync<Amenity>()).ToDictionary(a => a.Id, a => a.Name);
        var rows = mine.Select(r => new BookingRow(r, names.GetValueOrDefault(r.AmenityId, "Amenity"), now,
            r.PaymentId is { } p ? pays.GetValueOrDefault(p) : null, refunds.GetValueOrDefault(r.Id))).ToList();
        bool Show(BookingRow b) => b.Refund is { } f
            ? f.Status is "pending" or "approved" || (f.PaidAt ?? f.DecidedAt ?? now) > now.AddDays(-3)
            : b.Booking.Status is "awaiting_payment" or "confirmed" && b.Booking.EndsAt.ToUniversalTime() > now.AddHours(-24);
        bool Over(BookingRow b) => b.Ended || b.Refund is not null;
        return rows.Where(Show).Where(b => !Over(b)).OrderBy(b => b.Booking.StartsAt)
            .Concat(rows.Where(Show).Where(Over).OrderByDescending(b => b.Booking.StartsAt)).ToList();
    });

    public Task<AppResult<bool>> CancelAsync(int reservationId) => Safe.RunAsync(async () =>
    { await gw.RpcAsync("cancel_reservation", new() { ["p_id"] = reservationId, ["p_reason"] = null! }); return true; });

    public Task<AppResult<bool>> EndAsync(int reservationId) => Safe.RunAsync(async () =>
    { await gw.RpcAsync("end_reservation", new() { ["p_id"] = reservationId }); return true; });

    public async Task<AppResult<bool>> ClaimAsync(int reservationId, string reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) return AppResult<bool>.Fail(AppErrorKind.Validation, "bad_input");
        return await Safe.RunAsync(async () =>
        { await gw.RpcAsync("claim_amenity_no_show", new() { ["p_id"] = reservationId, ["p_reason"] = reason.Trim() }); return true; });
    }
}
