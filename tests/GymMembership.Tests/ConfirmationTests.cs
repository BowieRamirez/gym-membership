using System.Text.RegularExpressions;
using GymMembership.Core.Demo;
using GymMembership.Core.Models;
using GymMembership.Core.Services;
using GymMembership.Core.ViewModels;
using Xunit;

// Phase 12 (D14): consequential actions ask "are you sure?" first. "Go back" changes nothing; yes does the action.
public class ConfirmationTests
{
    // answers every question asked in this test and records "title|message|button"
    static List<string> Answer(bool yes)
    {
        var asked = new List<string>();
        Confirm.Answer = (title, message, action) => { asked.Add($"{title}|{message}|{action}"); return Task.FromResult(yes); };
        return asked;
    }

    static async Task<DemoGateway> As(string email, DemoGateway? gw = null)
    {
        gw ??= new DemoGateway();
        await gw.SignInAsync(email, DemoGateway.DemoPassword);
        return gw;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Signing_out_asks_first(bool yes)
    {
        var asked = Answer(yes);
        var gw = await As("member@demo.io");
        var vm = new HeaderViewModel(new ProfileService(gw), new NotificationService(gw), new AuthService(gw), new Nav());
        await vm.SignOutCommand.ExecuteAsync(null);
        Assert.StartsWith("Sign out?|", Assert.Single(asked));
        Assert.Equal(yes, gw.CurrentUserId is null);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Buying_a_membership_names_the_package_and_price(bool yes)
    {
        var asked = Answer(yes);
        var gw = await As("carlo@demo.io");
        var membership = new MembershipService(gw);
        var package = (await membership.ListPackagesAsync()).Value![0];
        var before = (await membership.MyPaymentsAsync()).Value!.Count;
        var vm = new PackagesViewModel(membership, new Nav(), new SubscriptionService(gw));
        await vm.AvailCommand.ExecuteAsync(package);
        var question = Assert.Single(asked);
        Assert.Contains(package.Name, question);
        Assert.Contains($"₱{package.Price:N2}", question);
        Assert.Equal(before + (yes ? 1 : 0), (await membership.MyPaymentsAsync()).Value!.Count);
    }

    [Fact]
    public async Task Going_back_on_an_amenity_pass_buys_nothing()
    {
        var asked = Answer(false);
        var gw = await As("member@demo.io");
        var vm = new AmenityBoardViewModel(new AmenityService(gw));
        await vm.LoadCommand.ExecuteAsync(null);
        var card = vm.Cards.First(c => c.BuyText == "Buy pass");
        var before = (await new MembershipService(gw).MyPaymentsAsync()).Value!.Count;
        await vm.BuyCommand.ExecuteAsync(card);
        Assert.Contains(card.Amenity.Name, Assert.Single(asked));
        Assert.Equal(before, (await new MembershipService(gw).MyPaymentsAsync()).Value!.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Approving_a_payment_asks_with_the_payer_and_amount(bool yes)
    {
        var gw = await As("carlo@demo.io");
        var membership = new MembershipService(gw);
        await membership.AvailAsync((await membership.ListPackagesAsync()).Value![0].Id);
        await gw.SignInAsync("employee@demo.io", DemoGateway.DemoPassword);
        var vm = new VerifyPaymentsViewModel(new PaymentStaffService(gw));
        await vm.LoadCommand.ExecuteAsync(null);
        var row = vm.Pending.First();
        var asked = Answer(yes);
        await vm.ApproveCommand.ExecuteAsync(row);
        var question = Assert.Single(asked);
        Assert.Contains(row.Payer, question);
        Assert.Contains($"₱{row.Payment.Amount:N2}", question);
        Assert.Equal(!yes, (await new PaymentStaffService(gw).PendingAsync()).Value!.Any(p => p.Payment.Id == row.Payment.Id));
    }

    [Fact]
    public async Task Going_back_leaves_an_account_active()
    {
        var gw = await As("admin@demo.io");
        var vm = new UsersViewModel(new AdminService(gw));
        await vm.LoadCommand.ExecuteAsync(null);
        var user = vm.Users.First(u => u.Roles.SequenceEqual(["member"]));
        var asked = Answer(false);
        await vm.ToggleActiveCommand.ExecuteAsync(user);
        Assert.Contains(user.Username, Assert.Single(asked));
        Assert.True((await gw.ListAsync<Profile>(p => p.Id == user.UserId)).Single().IsActive);
    }

    [Fact]
    public async Task Going_back_keeps_a_package_on_sale()
    {
        var gw = await As("admin@demo.io");
        var vm = new CatalogAdminViewModel(new AdminService(gw));
        await vm.LoadCommand.ExecuteAsync(null);
        var package = vm.Packages.First(p => p.IsActive);
        Answer(false);
        await vm.TogglePackageCommand.ExecuteAsync(package);
        Assert.True(package.IsActive);
        Assert.True((await gw.ListAsync<MembershipPackage>(p => p.Id == package.Id)).Single().IsActive);
    }

    // Every action on the approved D14 list asks before it does anything. Behaviour is covered above for a sample;
    // this keeps the rest from losing their question later.
    public static TheoryData<string, string, string> Approved => new()
    {
        { "MemberViewModels.cs", "HeaderViewModel", "SignOut" },
        { "MemberViewModels.cs", "SettingsViewModel", "SignOut" },
        { "MemberViewModels.cs", "SettingsViewModel", "ChangePassword" },
        { "MemberViewModels.cs", "PackagesViewModel", "Avail" },
        { "MemberViewModels.cs", "AmenityBoardViewModel", "Buy" },
        { "MemberViewModels.cs", "MyMembershipViewModel", "PayAgain" },
        { "MemberViewModels.cs", "MyMembershipViewModel", "SendProof" },
        { "MemberViewModels.cs", "CoachesViewModel", "Hire" },
        { "MemberViewModels.cs", "CoachesViewModel", "EndHire" },
        { "BookingViewModels.cs", "BookAmenityViewModel", "Book" },
        { "BookingViewModels.cs", "BookAmenityViewModel", "Cancel" },
        { "BookingViewModels.cs", "BookAmenityViewModel", "End" },
        { "StaffViewModels.cs", "RequestsViewModel", "Submit" },
        { "StaffViewModels.cs", "RequestsViewModel", "Cancel" },
        { "StaffViewModels.cs", "RequestsViewModel", "Accept" },
        { "StaffViewModels.cs", "RequestsViewModel", "Respond" },
        { "StaffViewModels.cs", "RequestsViewModel", "SubmitResponse" },
        { "StaffViewModels.cs", "SessionsViewModel", "Cancel" },
        { "StaffViewModels.cs", "SessionsViewModel", "Set" },
        { "StaffViewModels.cs", "SessionsViewModel", "Extend" },
        { "StaffViewModels.cs", "SessionsViewModel", "SendReschedule" },
        { "StaffViewModels.cs", "AttendanceViewModel", "CancelShift" },
        { "StaffViewModels.cs", "ShiftsViewModel", "Cancel" },
        { "StaffViewModels.cs", "ShiftsViewModel", "ToggleHiring" },
        { "StaffViewModels.cs", "TraineesViewModel", "Remove" },
        { "StaffViewModels.cs", "CoachProfileViewModel", "ToggleVisit" },
        { "StaffViewModels.cs", "CashierViewModel", "Record" },
        { "StaffViewModels.cs", "VerifyPaymentsViewModel", "Approve" },
        { "StaffViewModels.cs", "CheckInViewModel", "ToggleCoach" },
        { "StaffViewModels.cs", "UsersViewModel", "ToggleActive" },
        { "StaffViewModels.cs", "UsersViewModel", "ToggleRole" },
        { "StaffViewModels.cs", "UsersViewModel", "AddUser" },
        { "StaffViewModels.cs", "CatalogAdminViewModel", "TogglePackage" },
        { "StaffViewModels.cs", "CatalogAdminViewModel", "ToggleAmenity" },
        { "StaffViewModels.cs", "CatalogAdminViewModel", "SavePackage" },
        { "StaffViewModels.cs", "CatalogAdminViewModel", "AddAmenity" },
        { "StaffViewModels.cs", "DiscountsViewModel", "Toggle" },
        { "StaffViewModels.cs", "DiscountsViewModel", "SaveLimit" },
        { "StaffViewModels.cs", "GymSettingsViewModel", "Save" },
    };

    [Theory]
    [MemberData(nameof(Approved))]
    public void Each_approved_action_asks_first(string file, string type, string method)
    {
        var src = File.ReadAllText(Path.Combine(TestPaths.AppFolder(), "..", "GymMembership.Core", "ViewModels", file));
        var start = Regex.Match(src, $@"public (?:partial |sealed )*class {type}\b");
        Assert.True(start.Success, type);
        var next = Regex.Match(src[(start.Index + 1)..], @"\npublic (?:partial |sealed |abstract )*class ");
        var body = next.Success ? src.Substring(start.Index, next.Index + 1) : src[start.Index..];
        var head = Regex.Match(body, $@"Task {method}(?:Async)?\(");
        Assert.True(head.Success, $"{type}.{method}");
        var rest = body[head.Index..];
        var end = Regex.Match(rest, @"\n    }|\n\s*\[RelayCommand");
        Assert.Contains("Confirm.AskAsync(", end.Success && end.Index > 0 ? rest[..end.Index] : rest);
    }

    sealed class Nav : IAppNavigator
    {
        public void GoTo(AppRole role, bool changePassword = false) { }
        public void GoToLogin() { }
        public void GoToRegister() { }
        public void GoToMessages() { }
        public void GoToMembership() { }
        public void ApplyTheme(string theme) { }
    }
}
