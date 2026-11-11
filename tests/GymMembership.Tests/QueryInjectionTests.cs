using System.Web;
using GymMembership.Core.Models;
using Xunit;

/// <summary>
/// The app has no SQL strings: reads go through the Supabase library's filters. These pin what that library does with hostile
/// text in a filter value, so a library upgrade that stops encoding it fails here.
/// </summary>
public class QueryInjectionTests
{
    static readonly Supabase.Postgrest.Client Client = new("http://localhost/rest/v1");

    [Theory]
    [InlineData("x&or=(id.neq.0)")]
    [InlineData("x&select=*")]
    [InlineData("x'; drop table profiles; --")]
    [InlineData("x#fragment")]
    public void A_filter_value_stays_one_value(string hostile)
    {
        var url = new Uri(Client.Table<Profile>().Where(p => p.Username == hostile).GenerateUrl());
        var query = HttpUtility.ParseQueryString(url.Query);

        Assert.Equal("eq." + hostile, query["username"]);
        Assert.Null(query["or"]);
        Assert.Empty(url.Fragment);
    }

    /// <summary>
    /// A known weakness of the library, pinned rather than wished away: it does not quote values inside a grouped condition (a &amp;&amp; b),
    /// so a comma in one starts a new condition. The app is safe because it only puts ids and fixed words in filters; typed text goes
    /// through server functions (see <c>SupabaseGateway.ListAsync</c>). If this fails after an upgrade, the library now quotes: good news.
    /// </summary>
    [Fact]
    public void A_grouped_condition_is_not_quoted_so_it_must_never_hold_typed_text()
    {
        var typed = "x,id.neq.0";
        var url = new Uri(Client.Table<Profile>().Where(p => p.Username == typed && p.Id == "u-1").GenerateUrl());

        Assert.Equal("(username.eq.x,id.neq.0,id.eq.u-1)", HttpUtility.ParseQueryString(url.Query)["and"]);
    }
}
