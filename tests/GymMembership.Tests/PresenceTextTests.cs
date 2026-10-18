using GymMembership.Core.Models;
using GymMembership.Core.Services;
using Xunit;

public class PresenceTextTests
{
    static readonly DateTime Now = new(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(0, "Active just now")]
    [InlineData(90, "Active just now")]
    [InlineData(5 * 60, "Active 5 min ago")]
    [InlineData(59 * 60, "Active 59 min ago")]
    [InlineData(2 * 3600, "Active 2 h ago")]
    [InlineData(23 * 3600, "Active 23 h ago")]
    [InlineData(26 * 3600, "Active 1 d ago")]
    [InlineData(10 * 86400, "Active 10 d ago")]
    public void Active_ago_is_in_words(int secondsAgo, string expected) =>
        Assert.Equal(expected, ActiveAgo.Text(Now.AddSeconds(-secondsAgo), Now));

    [Fact]
    public void Never_seen_says_nothing() => Assert.Equal("", ActiveAgo.Text(null, Now));

    [Fact]
    public void A_time_in_the_future_reads_as_just_now() => Assert.Equal("Active just now", ActiveAgo.Text(Now.AddMinutes(3), Now));

    [Fact]
    public void A_time_of_unknown_kind_is_taken_as_utc() =>
        Assert.Equal("Active 2 h ago", ActiveAgo.Text(new DateTime(2026, 10, 16, 10, 0, 0, DateTimeKind.Unspecified), Now));

    [Fact]
    public void Presence_is_shown_for_a_coach_chat_you_can_still_write_in_and_for_a_member_seen_by_staff_only()
    {
        Assert.True(new ChatThread { Kind = "coach", WithId = "c", CanSend = true }.ShowsPresence);
        Assert.False(new ChatThread { Kind = "coach", WithId = "c", CanSend = false }.ShowsPresence);   // the hire ended
        Assert.True(new ChatThread { Kind = "support", WithId = "m" }.ShowsPresence);                    // staff looking at a member
        Assert.False(new ChatThread { Kind = "support", WithId = null, WithName = "Front desk" }.ShowsPresence);   // a member's own front desk thread
    }
}
