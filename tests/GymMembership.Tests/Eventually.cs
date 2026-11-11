using System.Diagnostics;
using Xunit;

/// <summary>
/// Waits until something a timer or an event brings about is true, up to a few seconds, instead of sleeping a fixed time and hoping.
/// CI's shared machines run timers late: a fixed 250 ms wait for an 80 ms timer failed there on 2026-11-12. Checks that something
/// did NOT happen keep their fixed wait, since a slow machine can only make those pass.
/// </summary>
static class Eventually
{
    public static async Task Until(Func<bool> done, int milliseconds = 3000)
    {
        var clock = Stopwatch.StartNew();
        while (!done() && clock.ElapsedMilliseconds < milliseconds) await Task.Delay(10);
        Assert.True(done(), $"still not true after {milliseconds} ms");
    }
}
