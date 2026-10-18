using System.Reflection;
using GymMembership.Core.Services;
using Xunit;

public class LinkCacheTests
{
    // a gateway that only knows how to sign links, and counts how many are being asked for at once
    public class Probe : DispatchProxy
    {
        public int InFlight, MaxInFlight, Calls;
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method!.Name != nameof(ISupabaseGateway.SignedUrlAsync)) throw new NotSupportedException(method.Name);
            return Task.Run<string?>(async () =>
            {
                var now = Interlocked.Increment(ref InFlight);
                lock (this) MaxInFlight = Math.Max(MaxInFlight, now);
                Interlocked.Increment(ref Calls);
                await Task.Delay(30);
                Interlocked.Decrement(ref InFlight);
                return (string)args![1]! == "gone.png" ? null : $"https://files/{args[1]}";
            });
        }
    }

    static (LinkCache Cache, Probe Probe) Make()
    {
        var gw = DispatchProxy.Create<ISupabaseGateway, Probe>();
        return (new LinkCache(gw, "chat-attachments", rememberMisses: true), (Probe)(object)gw);
    }

    [Fact]
    public async Task A_page_full_of_photos_asks_for_only_a_few_links_at_a_time()
    {
        var (cache, probe) = Make();

        var urls = await Task.WhenAll(Enumerable.Range(0, 30).Select(i => cache.UrlAsync($"a/b/{i}.png")));

        Assert.All(urls, u => Assert.StartsWith("https://files/", u));
        Assert.Equal(30, probe.Calls);
        Assert.True(probe.MaxInFlight <= 6, $"{probe.MaxInFlight} links were asked for at once");
    }

    [Fact]
    public async Task A_link_is_asked_for_once_and_a_missing_file_is_not_asked_for_again_every_refresh()
    {
        var (cache, probe) = Make();

        Assert.NotNull(await cache.UrlAsync("a/b/here.png"));
        Assert.NotNull(await cache.UrlAsync("a/b/here.png"));
        Assert.Null(await cache.UrlAsync("gone.png"));
        Assert.Null(await cache.UrlAsync("gone.png"));
        Assert.Null(await cache.UrlAsync("gone.png"));

        Assert.Equal(2, probe.Calls);   // one for the photo that is there, one for the photo that is gone
    }
}
