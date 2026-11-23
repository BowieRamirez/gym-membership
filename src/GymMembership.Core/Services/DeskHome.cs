using GymMembership.Core.Models;

namespace GymMembership.Core.Services;

/// <summary>What the front desk home shows: today's numbers. Each one is a shortcut to the screen that handles it.</summary>
public sealed record DeskSummary(int CheckInsToday, int CashToTake, int AmenitiesAvailable, int AmenitiesInUse, int AmenitiesUnavailable,
                                 int UnreadMessages, int UnclaimedChats)
{
    public int AmenitiesTotal => AmenitiesAvailable + AmenitiesInUse + AmenitiesUnavailable;
}

public interface IDeskHomeService { Task<AppResult<DeskSummary>> SummaryAsync(); }

public sealed class DeskHomeService(ISupabaseGateway gw, IChatService chat) : IDeskHomeService
{
    public async Task<AppResult<DeskSummary>> SummaryAsync()
    {
        var threadsTask = chat.ThreadsAsync();
        var startOfToday = DateTime.Today.ToUniversalTime();   // midnight where the desk is
        var dataTask = Safe.RunAsync(async () =>
        {
            var checkInsTask = gw.ListAsync<GymCheckIn>(c => c.CheckedInAt >= startOfToday);
            var pendingTask = gw.ListAsync<Payment>(p => p.Status == "pending" && p.Method == PayMethod.Cash);   // cashier plan Y7: online ones are Xendit's
            var amenitiesTask = gw.ListAsync<Amenity>(a => a.IsActive == true);
            await Task.WhenAll(checkInsTask, pendingTask, amenitiesTask);
            return (checkInsTask.Result, pendingTask.Result, amenitiesTask.Result);
        });
        await Task.WhenAll(threadsTask, dataTask);

        var threads = threadsTask.Result;
        if (!threads.Ok) return AppResult<DeskSummary>.Fail(threads.Error, threads.Message);
        var data = dataTask.Result;
        if (!data.Ok) return AppResult<DeskSummary>.Fail(data.Error, data.Message);

        var (checkIns, pending, amenities) = data.Value;
        var inbox = threads.Value!.Where(t => t.IsSupport && t.WithId is not null).ToList();   // a member's own "Front desk" thread has no WithId
        return AppResult<DeskSummary>.Success(new DeskSummary(
            checkIns.Count, pending.Count,
            amenities.Count(a => a.Availability == "available"), amenities.Count(a => a.Availability == "in_use"), amenities.Count(a => a.Availability == "unavailable"),
            inbox.Sum(t => t.Unread), inbox.Count(t => t.Status == "open" && t.ClaimedBy is null)));
    }
}
