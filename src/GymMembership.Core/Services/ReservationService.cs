using GymMembership.Core.Models;

namespace GymMembership.Core.Services;

// ===================== Amenity reservations (0029) =====================
/// <summary>One Manila day of the booking grid, as amenity_grid returns it. Times are UTC instants.</summary>
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
/// <summary>Time an amenity is held: in use from Starts to Ends, then cooling down until Cooldown. Id is set only on your own bookings.</summary>
public sealed class GridBlock
{
    public int Amenity { get; set; }
    public DateTime Starts { get; set; }
    public DateTime Ends { get; set; }
    public DateTime Cooldown { get; set; }
    public bool Mine { get; set; }
    public int? Id { get; set; }
}

public sealed record BookingRow(Reservation Booking, string Amenity, bool InUse)
{
    static DateTime Manila(DateTime utc) => utc.ToUniversalTime().AddHours(8);   // Manila has no daylight saving
    public string When => $"{Manila(Booking.StartsAt):ddd d MMM, HH:mm}–{Manila(Booking.EndsAt):HH:mm}";
    public bool CanCancel => !InUse;
    public bool CanEnd => InUse;
}

public interface IReservationService
{
    Task<AppResult<AmenityGrid>> GridAsync(DateOnly manilaDay);
    /// <summary>Books 30-minute slots, all or none. Touching slots of one amenity become one booking.</summary>
    Task<AppResult<int>> BookAsync(IReadOnlyCollection<(int AmenityId, DateTime StartUtc)> slots);
    /// <summary>This member's bookings that have not ended yet, soonest first.</summary>
    Task<AppResult<IReadOnlyList<BookingRow>>> MyBookingsAsync();
    Task<AppResult<bool>> CancelAsync(int reservationId);
    Task<AppResult<bool>> EndAsync(int reservationId);
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

    public async Task<AppResult<int>> BookAsync(IReadOnlyCollection<(int AmenityId, DateTime StartUtc)> slots)
    {
        if (slots.Count == 0) return AppResult<int>.Fail(AppErrorKind.Validation, "bad_input");
        var json = slots.Select(s => new Dictionary<string, object> { ["amenity"] = s.AmenityId, ["start"] = s.StartUtc.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ") }).ToList();
        return await Safe.RunAsync(async () => (await gw.RpcAsync<List<Reservation>>("book_slots", new() { ["p_slots"] = json }) ?? []).Count);
    }

    public Task<AppResult<IReadOnlyList<BookingRow>>> MyBookingsAsync() => Safe.RunAsync<IReadOnlyList<BookingRow>>(async () =>
    {
        var uid = gw.CurrentUserId; var now = DateTime.UtcNow;
        var mine = await gw.ListAsync<Reservation>(r => r.MemberId == uid && r.Status == "confirmed");
        var names = (await gw.ListAsync<Amenity>()).ToDictionary(a => a.Id, a => a.Name);
        return mine.Where(r => r.EndsAt.ToUniversalTime() > now).OrderBy(r => r.StartsAt)
            .Select(r => new BookingRow(r, names.GetValueOrDefault(r.AmenityId, "Amenity"), r.StartsAt.ToUniversalTime() <= now)).ToList();
    });

    public Task<AppResult<bool>> CancelAsync(int reservationId) => Safe.RunAsync(async () =>
    { await gw.RpcAsync("cancel_reservation", new() { ["p_id"] = reservationId, ["p_reason"] = null! }); return true; });

    public Task<AppResult<bool>> EndAsync(int reservationId) => Safe.RunAsync(async () =>
    { await gw.RpcAsync("end_reservation", new() { ["p_id"] = reservationId }); return true; });
}
