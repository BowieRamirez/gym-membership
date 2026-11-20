using GymMembership.Core.Models;
using GymMembership.Core.Services;
using Xunit;

/// <summary>Owner fixes 2026-11-19 Q5: payments read Pending, Paid or Failed (Voided stays for old payments; Transactions also filters Refunded).</summary>
public class PaymentStatusTests
{
    [Theory]
    [InlineData(Status.Pending, "Pending")]
    [InlineData(Status.Verified, "Paid")]
    [InlineData(Status.Rejected, "Failed")]
    [InlineData(Status.Expired, "Failed")]
    [InlineData(Status.Cancelled, "Cancelled")]
    [InlineData(Status.Voided, "Voided")]
    public void Each_status_has_one_plain_word(string status, string word) =>
        Assert.Equal(word, PaymentStatus.Text(status));
}
