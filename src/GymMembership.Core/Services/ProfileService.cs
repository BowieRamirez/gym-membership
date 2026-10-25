using System.Collections.Concurrent;
using GymMembership.Core.Models;

namespace GymMembership.Core.Services;

public static class Initials
{
    /// <summary>First letter of the first and last word: "Mary-Ann O'Brien" is "MO", "alex" is "A", blank is "?".</summary>
    public static string From(string? name)
    {
        var words = (name ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length == 0) return "?";
        var first = char.ToUpperInvariant(words[0][0]).ToString();
        return words.Length == 1 ? first : first + char.ToUpperInvariant(words[^1][0]);
    }
}

public sealed record MyProfile(string Name, string? AvatarPath, string FirstName = "", string LastName = "");

public interface IProfileService
{
    /// <summary>Raised after the profile picture changes, so an open header can redraw.</summary>
    event Action? AvatarChanged;
    Task<AppResult<MyProfile>> MeAsync();
    /// <summary>Checks the file, uploads it to your folder in the avatars bucket and makes it your picture. Returns the stored path.</summary>
    Task<AppResult<string>> SetAvatarAsync(string fileName, byte[] data);
    /// <summary>A short-lived link for a stored picture; null when there is none, so the initials are shown instead.</summary>
    Task<AppResult<string?>> AvatarUrlAsync(string? path);
    /// <summary>Tells the server you are in the app (it counts at most once every 5 minutes). The result can be ignored.</summary>
    Task<AppResult<bool>> TouchSeenAsync();
    /// <summary>Renames you; the full name everyone sees becomes "first last".</summary>
    Task<AppResult<bool>> SetNameAsync(string firstName, string lastName);
}

public sealed class ProfileService(ISupabaseGateway gw) : IProfileService
{
    public const int MaxBytes = 5 * 1024 * 1024;
    static readonly string[] Extensions = [".jpg", ".jpeg", ".png", ".webp"];
    readonly LinkCache _avatars = new(gw, "avatars");

    /// <summary>A JPG, PNG or WebP of 1 byte to 5 MB: the rule for profile pictures and chat photos alike.</summary>
    public static bool IsAcceptableImage(string fileName, byte[] data) =>
        Extensions.Contains(Path.GetExtension(fileName).ToLowerInvariant()) && data.Length is > 0 and <= MaxBytes;

    public event Action? AvatarChanged;

    public Task<AppResult<bool>> TouchSeenAsync() => Safe.RunAsync(async () => { await gw.RpcAsync("touch_last_seen"); return true; });

    public Task<AppResult<MyProfile>> MeAsync() => Safe.RunAsync(async () =>
    {
        var uid = gw.CurrentUserId;
        var p = (await gw.ListAsync<Profile>(x => x.Id == uid)).FirstOrDefault() ?? throw new Exception("not_found");
        return new MyProfile(string.IsNullOrWhiteSpace(p.FullName) ? p.Username : p.FullName, p.AvatarPath, p.FirstName, p.LastName);
    });

    public async Task<AppResult<bool>> SetNameAsync(string firstName, string lastName)
    {
        firstName = firstName.Trim(); lastName = lastName.Trim();
        if (!AccountRules.IsName(firstName, lastName)) return AppResult<bool>.Fail(AppErrorKind.Validation, "bad_name");
        return await Safe.RunAsync(async () => { await gw.RpcAsync("set_my_name", new() { ["p_first"] = firstName, ["p_last"] = lastName }); return true; });
    }

    public async Task<AppResult<string>> SetAvatarAsync(string fileName, byte[] data)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        if (!IsAcceptableImage(fileName, data)) return AppResult<string>.Fail(AppErrorKind.Validation, "bad_image");

        // ponytail: a new file name per upload so the link is never stale; the old file stays in the bucket. Add cleanup when storage use matters.
        var result = await Safe.RunAsync(async () =>
        {
            var path = await gw.UploadAsync("avatars", $"{gw.CurrentUserId}/avatar-{Guid.NewGuid():N}{ext}", data);
            await gw.RpcAsync("set_my_avatar", new() { ["p_path"] = path });
            return path;
        });
        if (result.Ok) AvatarChanged?.Invoke();
        return result;
    }

    public Task<AppResult<string?>> AvatarUrlAsync(string? path) => Safe.RunAsync<string?>(() => _avatars.UrlAsync(path));
}

/// <summary>
/// Signed links last an hour; one is reused for 50 minutes instead of asking the server on every redraw. With rememberMisses, a file that is not
/// there is remembered for a minute, so a missing photo is not asked for again on every refresh. At most six links are asked for at a time,
/// so a page full of photos does not send fifty requests at once.
/// </summary>
public sealed class LinkCache(ISupabaseGateway gw, string bucket, bool rememberMisses = false)
{
    static readonly TimeSpan Life = TimeSpan.FromMinutes(50), MissLife = TimeSpan.FromMinutes(1);
    readonly ConcurrentDictionary<string, (string? Url, DateTime Until)> _links = new();
    readonly SemaphoreSlim _gate = new(6);

    public async Task<string?> UrlAsync(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        if (_links.TryGetValue(path, out var hit) && hit.Until > DateTime.UtcNow) return hit.Url;
        await _gate.WaitAsync();
        try
        {
            if (_links.TryGetValue(path, out hit) && hit.Until > DateTime.UtcNow) return hit.Url;   // another caller got it while this one waited
            var url = await gw.SignedUrlAsync(bucket, path);
            if (url is not null) _links[path] = (url, DateTime.UtcNow + Life);
            else if (rememberMisses) _links[path] = (null, DateTime.UtcNow + MissLife);
            return url;
        }
        finally { _gate.Release(); }
    }
}
