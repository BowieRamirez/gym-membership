using GymMembership.Core.Demo;
using GymMembership.Core.Services;

static class TestDrawer
{
    /// <summary>0050: cash needs the signed-in cashier's open shift. Tests about something else open one first.</summary>
    public static Task OpenDrawerAsync(this DemoGateway gw) => new PaymentStaffService(gw).OpenShiftAsync(5000, null);
}
