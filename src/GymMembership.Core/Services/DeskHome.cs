using GymMembership.Core.Models;

namespace GymMembership.Core.Services;

/// <summary>What the front desk home shows: today's numbers. Each one is a shortcut to the screen that handles it.</summary>
public sealed record DeskSummary(int CheckInsToday, int PaymentsToVerify, int AmenitiesAvailable, int AmenitiesInUse, int AmenitiesUnavailable,
                                 int UnreadMessages, int UnclaimedChats)
{
    public int AmenitiesTotal => AmenitiesAvailable + AmenitiesInUse + AmenitiesUnavailable;
}

public interface IDeskHomeService { Task<AppResult<DeskSummary>> SummaryAsync(); }

public sealed class DeskHomeService(ISupabaseGateway gw, IChatService chat) : IDeskHomeService
{
    public async Task<AppResult<DeskSummary>> SummaryAsync()
    {
        var threads = await chat.ThreadsAsync();
        if (!threads.Ok) return AppResult<DeskSummary>.Fail(threads.Error, threads.Message);

        return await Safe.RunAsync(async () =>
        {
            var startOfToday = DateTime.Today.ToUniversalTime();   // midnight where the desk is
            var checkIns = await gw.ListAsync<GymCheckIn>(c => c.CheckedInAt >= startOfToday);
            var pending = await gw.ListAsync<Payment>(p => p.Status == "pending");
            var amenities = await gw.ListAsync<Amenity>(a => a.IsActive == true);
            var inbox = threads.Value!.Where(t => t.IsSupport && t.WithId is not null).ToList();   // a member's own "Front desk" thread has no WithId
            return new DeskSummary(
                checkIns.Count, pending.Count,
                amenities.Count(a => a.Availability == "available"), amenities.Count(a => a.Availability == "in_use"), amenities.Count(a => a.Availability == "unavailable"),
                inbox.Sum(t => t.Unread), inbox.Count(t => t.Status == "open" && t.ClaimedBy is null));
        });
    }
}
