using System.Globalization;
using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;
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
    /// <summary>G13 (0064): your own request, waiting for its down payment; it holds nothing for anyone else.</summary>
    public bool Pending { get; set; }
}

/// <summary>G14 (0064): a coach who runs the amenity and is free for the whole time.</summary>
public sealed class FreeCoach : BaseModel
{ [Column("coach_id")] public string CoachId { get; set; } = ""; [Column("name")] public string Name { get; set; } = ""; [Column("avatar_path")] public string? AvatarPath { get; set; } }

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

/// <summary>G13, G14, G4 (0064): what a booking costs, as book_amenity works it out.</summary>
public sealed record PriceSplit(decimal Total, decimal Cut, decimal Due, decimal Down, decimal Balance);

public static class BookingPrice
{
    /// <summary>Price plus the coach fee (with a coach), less the code's percent; the down payment is the Gym settings percent of that,
    /// rounded to the centavo as the database rounds (half away from zero); the balance is the rest.</summary>
    public static PriceSplit Split(decimal price, decimal coachFee, bool withCoach, int percent, int codePercent = 0)
    {
        var total = price + (withCoach ? coachFee : 0);
        var cut = Math.Round(total * codePercent / 100m, 2, MidpointRounding.AwayFromZero);
        var due = total - cut;
        var down = Math.Round(due * percent / 100m, 2, MidpointRounding.AwayFromZero);
        return new(total, cut, due, down, due - down);
    }
}

/// <summary>G13 (0064): a confirmed booking with a balance still to pay at the desk.</summary>
public sealed record BalanceRow(Reservation Booking, string Member, string Amenity, string? Coach)
{
    static DateTime Manila(DateTime utc) => utc.ToUniversalTime().AddHours(8);
    public string When => $"{Manila(Booking.StartsAt).ToString("h:mm tt", CultureInfo.InvariantCulture)} to {Manila(Booking.EndsAt).ToString("h:mm tt", CultureInfo.InvariantCulture)}";
    public string CoachText => Coach ?? "Solo";
    public string PaidText => Booking.DownPayment > 0 ? $"₱{Booking.DownPayment:N2} down" : "With a pass";
    public string BalanceText => $"₱{Booking.Balance:N2}";
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
    string Down => $"₱{Booking.DownPayment:N2}";
    /// <summary>G13: the member gets the down payment back if they cancel 24 hours or more before the start.</summary>
    bool Refundable => Start.AddHours(-24) >= NowUtc;
    bool Confirmed => Booking.Status == "confirmed";
    public bool Paid => Payment?.Status == Status.Verified;
    public bool InUse => Confirmed && Start <= NowUtc && NowUtc < End;
    public bool Ended => Confirmed && End <= NowUtc;
    /// <summary>G13 (0064): any time before the start, until the balance is paid at the desk.</summary>
    public bool CanCancel => Refund is null && (IsWaiting || Confirmed) && Start > NowUtc && Booking.BalancePaymentId is null;
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
        { Status: "approved", Kind: "cancel" } when Booking.CancelReason is "Slot taken" or "Coach not free" or "You have another booking then" => $"{Booking.CancelReason} · refund approved",
        { Status: "approved", Kind: "cancel" } when Booking.CancelReason is "Slot taken" or "Coach not free" or "You have another booking then" => $"{Booking.CancelReason} · refund approved",
        { Status: "approved", Kind: "cancel" } => "Cancelled · refund approved",
        { Status: "approved" } => "Refund approved",
        _ => IsWaiting ? "Waiting for down payment" : InUse ? "In use" : Ended ? "Ended" : "Confirmed"
    };
    public string Detail => Refund switch
    {
        { Status: "pending" } => "The front desk is checking it. You'll get a notification.",
        { Status: "rejected" } r => $"\"{r.DecisionReason}\" (Front desk)",
        { Status: "paid" } => $"{Price} repaid.",
        { Status: "approved", Kind: "no_show" } r => $"{Price} to be repaid at the front desk. \"{r.DecisionReason}\" (Front desk)",
        { Status: "approved", Kind: "cancel" } when Booking.CancelReason is "Slot taken" or "Coach not free" or "You have another booking then"
            => $"Another booking's down payment was verified first. {Price} to be repaid at the front desk.",
        { Status: "approved" } => $"{Price} to be repaid at the front desk.",
        _ when IsWaiting && Payment is { } p => (p.Method == PayMethod.Cash
            ? $"Down payment {Price} at the front desk by {At(p.ExpiresAt ?? Start)}."
            : p.ProofPath is null ? $"Down payment {Price} by GCash. Pay by {At(p.ExpiresAt ?? Start)}."
            : $"Down payment {Price} by GCash. Proof sent; the front desk is checking it.") + " Not yours until it's verified.",
        _ when InUse => $"Ends at {Manila(End).ToString("h:mm tt", En)}.",
        _ when CanClaim => $"Didn't go? Tell the front desk by {At(End.AddHours(24))} and they'll check for a refund.",
        _ when Ended => "",
        _ when Paid && Booking.BalancePaymentId is not null => "Paid in full.",
        _ when Paid => $"Down payment {Down} paid. Balance ₱{Booking.Balance:N2} at the desk. "
            + (Refundable ? $"Cancel by {At(Start.AddHours(-24))} to get the {Down} down payment back." : $"Cancelling now keeps the {Down} down payment."),
        _ when Booking.Balance > 0 && Booking.BalancePaymentId is null => $"Booked with your pass. Coach fee ₱{Booking.Balance:N2} at the desk.",
        _ => Booking.CoachId is null ? "Booked with your pass." : "With your coach."
    };
}

public interface IReservationService
{
    Task<AppResult<AmenityGrid>> GridAsync(DateOnly manilaDay);
    /// <summary>F12, G13 (0064): books with a pass or session if the member has one; otherwise a request with its down payment (qr or cash).
    /// G14: a coach who is free then; G4: a code that fits the amenity.</summary>
    Task<AppResult<Reservation>> BookAsync(int amenityId, DateTime startUtc, int minutes, string method, string? coachId = null, string? code = null);
    /// <summary>G14: coaches who run the amenity and are free for the whole time.</summary>
    Task<AppResult<IReadOnlyList<FreeCoach>>> FreeCoachesAsync(int amenityId, DateTime startUtc, int minutes);
    /// <summary>G13: the Gym settings percent paid to reserve.</summary>
    Task<AppResult<int>> DownPaymentPercentAsync();
    /// <summary>G13, the desk: one Manila day's confirmed bookings with a balance still to pay.</summary>
    Task<AppResult<IReadOnlyList<BalanceRow>>> BalancesAsync(DateOnly manilaDay);
    Task<AppResult<Payment>> TakeBalanceAsync(int reservationId, string method, string? reference);
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

    static string Utc(DateTime t) => t.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ");

    public Task<AppResult<Reservation>> BookAsync(int amenityId, DateTime startUtc, int minutes, string method, string? coachId = null, string? code = null) => Safe.RunAsync(() =>
    {
        Dictionary<string, object> args = new() { ["p_amenity"] = amenityId, ["p_start"] = Utc(startUtc), ["p_minutes"] = minutes, ["p_method"] = method };
        if (coachId is not null) args["p_coach"] = coachId;
        if (!string.IsNullOrWhiteSpace(code)) args["p_code"] = code.Trim();
        return gw.RpcAsync<Reservation>("book_amenity", args);
    });

    public Task<AppResult<IReadOnlyList<FreeCoach>>> FreeCoachesAsync(int amenityId, DateTime startUtc, int minutes) => Safe.RunAsync<IReadOnlyList<FreeCoach>>(async () =>
        await gw.RpcAsync<List<FreeCoach>>("free_coaches", new() { ["p_amenity"] = amenityId, ["p_start"] = Utc(startUtc), ["p_end"] = Utc(startUtc.AddMinutes(minutes)) }));

    public Task<AppResult<int>> DownPaymentPercentAsync() => Safe.RunAsync(async () => (await gw.ListAsync<GymSettings>()).First().DownPaymentPercent);

    public Task<AppResult<IReadOnlyList<BalanceRow>>> BalancesAsync(DateOnly manilaDay) => Safe.RunAsync<IReadOnlyList<BalanceRow>>(async () =>
    {
        var from = DateTime.SpecifyKind(manilaDay.ToDateTime(TimeOnly.MinValue).AddHours(-8), DateTimeKind.Utc);   // Manila has no daylight saving
        var to = from.AddDays(1);
        var due = (await gw.ListAsync<Reservation>(r => r.Status == "confirmed" && r.StartsAt >= from && r.StartsAt < to))
            .Where(r => r.Balance > 0 && r.BalancePaymentId is null).OrderBy(r => r.StartsAt).ToList();
        if (due.Count == 0) return [];
        var names = await Directory_.UserNamesAsync(gw);
        var amenities = (await gw.ListAsync<Amenity>()).ToDictionary(a => a.Id, a => a.Name);
        return due.Select(r => new BalanceRow(r, names.GetValueOrDefault(r.MemberId, "Member"), amenities.GetValueOrDefault(r.AmenityId, "Amenity"),
            r.CoachId is { } c ? names.GetValueOrDefault(c) : null)).ToList();
    });

    public Task<AppResult<Payment>> TakeBalanceAsync(int reservationId, string method, string? reference) => Safe.RunAsync(async () =>
    {
        Dictionary<string, object> args = new() { ["p_reservation_id"] = reservationId, ["p_method"] = method };
        if (!string.IsNullOrWhiteSpace(reference)) args["p_reference"] = reference.Trim();
        return await gw.RpcAsync<Payment>("record_booking_balance", args);
    });

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
