namespace GymMembership.Core.Services;

/// <summary>G25 (0069): the Terms and Policy shipped with the app (Resources/Raw/terms.md). Sign-up sends this version; the server keeps
/// it with the time and refuses a self sign-up without it. Change it whenever the text changes.</summary>
public static class TermsInfo
{
    public const string Version = "2026-11-15";
}
