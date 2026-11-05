using GymMembership.Core.Demo;
using GymMembership.Core.Services;
using GymMembership.Core.ViewModels;
using Xunit;
using NullNavigator = NamesAndEmailTests.NullNavigator;

/// <summary>The "email already in use" message on the register page: debounced, never stale, and never in the way.</summary>
public class RegisterEmailCheckTests
{
    sealed class StubAuth(Func<string, Task<AppResult<bool>>> lookup) : IAuthService
    {
        public List<string> Lookups { get; } = new();
        public Task<AppResult<bool>> EmailInUseAsync(string email) { Lookups.Add(email); return lookup(email); }

        public string? UserId => null;
        public Task<AppResult<AppRole>> SignInAsync(string email, string password) => throw new NotImplementedException();
        public Task<AppResult<AppRole?>> RestoreAsync() => throw new NotImplementedException();
        public Task SignOutAsync() => throw new NotImplementedException();
        public Task<AppResult<bool>> ChangePasswordAsync(string current, string next) => throw new NotImplementedException();
        public Task<AppResult<bool>> MustChangePasswordAsync() => throw new NotImplementedException();
        public Task<AppResult<bool>> RegisterAsync(string firstName, string lastName, string email, string password) => throw new NotImplementedException();
        public Task<AppResult<EmailStatus>> EmailStatusAsync() => throw new NotImplementedException();
        public Task<AppResult<bool>> SendEmailCodeAsync(string email) => throw new NotImplementedException();
        public Task<AppResult<bool>> ConfirmEmailAsync(string email, string code) => throw new NotImplementedException();
        public Task<AppResult<bool>> SendResetCodeAsync(string email) => throw new NotImplementedException();
        public Task<AppResult<bool>> ResetPasswordAsync(string email, string code, string newPassword) => throw new NotImplementedException();
    }

    static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(100);
    static Task<AppResult<bool>> Answer(bool inUse) => Task.FromResult(AppResult<bool>.Success(inUse));
    static RegisterViewModel Page(IAuthService auth) => new(auth, new NullNavigator()) { EmailCheckDelay = Delay };

    static async Task Until(Func<bool> done)
    {
        for (var i = 0; i < 200 && !done(); i++) await Task.Delay(10);
        Assert.True(done());
    }

    [Fact]
    public async Task Fast_typing_sends_one_lookup_for_the_final_address()
    {
        var auth = new StubAuth(_ => Answer(false));
        var vm = Page(auth);
        vm.Email = "ana@demo.i"; vm.Email = "ana@demo.io"; vm.Email = "ana@demo.io.ph";
        await Task.Delay(Delay * 3);
        Assert.Equal(["ana@demo.io.ph"], auth.Lookups);
    }

    [Fact]
    public async Task An_address_in_use_shows_the_message_and_editing_clears_it_at_once()
    {
        var vm = Page(new StubAuth(_ => Answer(true)));
        vm.Email = "ana@demo.io";
        await Until(() => vm.EmailTaken is not null);
        vm.Email = "ana@demo.i";
        Assert.Null(vm.EmailTaken);
    }

    [Fact]
    public async Task A_slow_old_answer_never_overwrites_a_newer_one()
    {
        var slow = new TaskCompletionSource<AppResult<bool>>();
        var auth = new StubAuth(email => email == "old@demo.io" ? slow.Task : Answer(false));
        var vm = Page(auth);
        vm.Email = "old@demo.io";
        await Until(() => auth.Lookups.Count == 1);
        vm.Email = "new@demo.io";
        await Until(() => auth.Lookups.Count == 2);

        slow.SetResult(AppResult<bool>.Success(true));
        await Task.Delay(Delay);
        Assert.Null(vm.EmailTaken);
    }

    [Theory]
    [InlineData("")]
    [InlineData("ana")]
    [InlineData("ana@demo")]
    [InlineData("a b@demo.io")]
    public async Task An_unfinished_address_sends_no_lookup(string typed)
    {
        var auth = new StubAuth(_ => Answer(true));
        var vm = Page(auth);
        vm.Email = typed;
        await Task.Delay(Delay * 2);
        Assert.Empty(auth.Lookups);
        Assert.Null(vm.EmailTaken);
    }

    [Fact]
    public async Task Surrounding_spaces_are_trimmed_before_the_lookup()
    {
        var auth = new StubAuth(_ => Answer(false));
        var vm = Page(auth);
        vm.Email = "  ana@demo.io ";
        await Until(() => auth.Lookups.Count == 1);
        Assert.Equal("ana@demo.io", auth.Lookups[0]);
    }

    [Fact]
    public async Task A_refused_or_failed_lookup_shows_nothing()
    {
        var auth = new StubAuth(_ => Task.FromResult(AppResult<bool>.Fail(AppErrorKind.Validation, "rate_limited")));
        var vm = Page(auth);
        vm.Email = "ana@demo.io";
        await Until(() => auth.Lookups.Count == 1);
        await Task.Delay(Delay);
        Assert.Null(vm.EmailTaken);
        Assert.Null(vm.ErrorMessage);   // a page-level error here would look like registering failed
    }

    [Fact]
    public async Task The_service_finds_accounts_in_any_case_and_skips_bad_shapes()
    {
        var gw = new DemoGateway();
        var auth = new AuthService(gw);
        Assert.True((await auth.EmailInUseAsync("MEMBER@demo.io")).Value);
        Assert.False((await auth.EmailInUseAsync("nobody@demo.io")).Value);

        gw.FailWith = new Exception("network");   // proves no call is made for a bad shape
        var bad = await auth.EmailInUseAsync("not an email");
        Assert.True(bad.Ok);
        Assert.False(bad.Value);
    }

    [Fact]
    public async Task The_message_can_still_be_ignored_because_the_real_guard_is_on_submit()
    {
        var gw = new DemoGateway();
        var vm = new RegisterViewModel(new AuthService(gw), new NullNavigator())
        { FirstName = "Ana", LastName = "Reyes", Email = "member@demo.io", Password = "secret12", ConfirmPassword = "secret12" };
        await vm.RegisterCommand.ExecuteAsync(null);
        Assert.Equal(ErrorText.For(AppErrorKind.Validation, "email_taken"), vm.ErrorMessage);
    }
}
