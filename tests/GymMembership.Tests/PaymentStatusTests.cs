using GymMembership.Core.Models;
using GymMembership.Core.Services;
using Xunit;

/// <summary>Owner fixes 2026-11-19 Q5: payments read Pending, Paid, Failed or Refunded (Voided stays for old payments).</summary>
public class PaymentStatusTests
{
    [Theory]
    [InlineData(Status.Pending, false, "Pending")]
    [InlineData(Status.Verified, false, "Paid")]
    [InlineData(Status.Verified, true, "Refunded")]
    [InlineData(Status.Rejected, false, "Failed")]
    [InlineData(Status.Expired, false, "Failed")]
    [InlineData(Status.Cancelled, false, "Cancelled")]
    [InlineData(Status.Voided, false, "Voided")]
    public void Each_status_has_one_plain_word(string status, bool refunded, string word) =>
        Assert.Equal(word, PaymentStatus.Text(status, refunded));
}
