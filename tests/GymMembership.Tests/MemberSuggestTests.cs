using GymMembership.Core.Demo;
using GymMembership.Core.Models;
using GymMembership.Core.Services;
using GymMembership.Core.ViewModels;
using Xunit;

public class MemberSuggestTests
{
    sealed class FakeAttendance(Func<string, CancellationToken, Task<AppResult<IReadOnlyList<MemberHit>>>> find) : IAttendanceService
    {
        public readonly List<string> Queries = new();
        public Task<AppResult<IReadOnlyList<MemberHit>>> FindMembersAsync(string query) { Queries.Add(query); return find(query, CancellationToken.None); }
        public Task<AppResult<bool>> RecordAsync(int sessionId, string status) => throw new NotImplementedException();
        public Task<AppResult<bool>> CheckInAsync(string memberId, string? method = null, string? pin = null, string? reason = null, string? proof = null, string? idType = null) => throw new NotImplementedException();
        public Task<AppResult<string>> VerifyPasswordAsync(string memberId, string password) => throw new NotImplementedException();
        public Task<AppResult<bool>> CanSkipPinAsync() => throw new NotImplementedException();
        public Task<AppResult<MemberPass>> MyPassAsync() => throw new NotImplementedException();
        public Task<AppResult<MemberScan>> ScanAsync(string code) => throw new NotImplementedException();
        public Task<AppResult<IReadOnlyList<CheckInRow>>> CheckInsOnAsync(DateOnly manilaDay) => throw new NotImplementedException();
    }

    static MemberHit Hit(string id, string name) => new(id, name, false, null, null, "active", null, null, $"{id}@demo.io");

    static CashierViewModel Cashier(IAttendanceService att) => new(new PaymentStaffService(new DemoGateway()), att) { SearchDelay = TimeSpan.FromMilliseconds(40) };
    static DeskBookingViewModel Booking(IAttendanceService att) => new(att, new ReservationService(new DemoGateway())) { SearchDelay = TimeSpan.FromMilliseconds(40) };

    [Fact]
    public async Task One_letter_does_not_search()
    {
        var att = new FakeAttendance((_, _) => Task.FromResult(AppResult<IReadOnlyList<MemberHit>>.Success(new[] { Hit("1", "Carlo") })));
        var vm = Cashier(att);
        vm.Search = "c";
        await Task.Delay(80);
        Assert.Empty(att.Queries);
        Assert.Empty(vm.Suggestions);
    }

    [Fact]
    public async Task Typing_fast_debounces_to_the_last_query()
    {
        var att = new FakeAttendance((q, _) => Task.FromResult(AppResult<IReadOnlyList<MemberHit>>.Success(new[] { Hit("1", "Lilian") })));
        var vm = Cashier(att);
        vm.Search = "li";
        await Task.Delay(10);
        vm.Search = "lil";
        await Task.Delay(100);
        Assert.Equal(new[] { "lil" }, att.Queries);
        Assert.Single(vm.Suggestions);
    }

    [Fact]   // Review Focus 4: slow answer for "li" arriving after "lilian" is dropped
    public async Task Slow_old_answer_never_replaces_newer_results()
    {
        var liGate = new TaskCompletionSource<AppResult<IReadOnlyList<MemberHit>>>();
        var att = new FakeAttendance((q, _) => q == "li"
            ? liGate.Task
            : Task.FromResult(AppResult<IReadOnlyList<MemberHit>>.Success(new[] { Hit("2", "Lilian Sabado") })));
        var vm = Booking(att);
        vm.SearchDelay = TimeSpan.FromMilliseconds(15);

        vm.Search = "li";
        await Task.Delay(40);   // "li" is now in flight
        vm.Search = "lilian";
        await Task.Delay(40);   // "lilian" completed
        Assert.Equal("Lilian Sabado", Assert.Single(vm.Suggestions).Username);

        liGate.SetResult(AppResult<IReadOnlyList<MemberHit>>.Success(new[] { Hit("1", "Liam Old") }));
        await Task.Delay(20);
        Assert.Equal("Lilian Sabado", Assert.Single(vm.Suggestions).Username);
    }

    [Fact]
    public async Task Choosing_a_suggestion_picks_member_fills_box_and_closes_list()
    {
        var att = new FakeAttendance((_, _) => Task.FromResult(AppResult<IReadOnlyList<MemberHit>>.Success(new[] { Hit("1", "Carlo Diaz"), Hit("2", "Carla Cruz") })));
        var vm = Booking(att);
        vm.Search = "car";
        await Task.Delay(80);
        Assert.Equal(2, vm.Suggestions.Count);

        var first = vm.Suggestions[0];
        vm.ChooseSuggestionCommand.Execute(first);
        await Task.Delay(80);

        Assert.Same(first, vm.Member);
        Assert.Equal("Carlo Diaz", vm.Search);
        Assert.Empty(vm.Suggestions);
        Assert.Single(att.Queries);   // filling the box did not trigger another search
    }

    [Fact]
    public async Task Enter_picks_the_top_suggestion()
    {
        var att = new FakeAttendance((_, _) => Task.FromResult(AppResult<IReadOnlyList<MemberHit>>.Success(new[] { Hit("1", "Carlo Diaz"), Hit("2", "Carla Cruz") })));
        var vm = Cashier(att);
        vm.Search = "car";
        await Task.Delay(80);

        await vm.PickTopSuggestionCommand.ExecuteAsync(null);
        Assert.Equal("Carlo Diaz", vm.Member?.Username);
        Assert.Equal("Carlo Diaz", vm.Search);
        Assert.Empty(vm.Suggestions);
    }

    [Fact]
    public async Task Suggestions_cap_at_eight_rows()
    {
        var many = Enumerable.Range(1, 12).Select(i => Hit($"{i}", $"Zed {i}")).ToList();
        var att = new FakeAttendance((_, _) => Task.FromResult(AppResult<IReadOnlyList<MemberHit>>.Success(many)));
        var vm = Cashier(att);
        vm.Search = "zed";
        await Task.Delay(80);
        Assert.Equal(8, vm.Suggestions.Count);
        Assert.Equal(12, vm.Hits.Count);
    }
}
