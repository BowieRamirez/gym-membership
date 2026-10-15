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

public sealed record MyProfile(string Name, string? AvatarPath);

public interface IProfileService
{
    /// <summary>Raised after the profile picture changes, so an open header can redraw.</summary>
    event Action? AvatarChanged;
    Task<AppResult<MyProfile>> MeAsync();
    /// <summary>Checks the file, uploads it to your folder in the avatars bucket and makes it your picture. Returns the stored path.</summary>
    Task<AppResult<string>> SetAvatarAsync(string fileName, byte[] data);
    /// <summary>A short-lived link for a stored picture; null when there is none, so the initials are shown instead.</summary>
    Task<AppResult<string?>> AvatarUrlAsync(string? path);
}

public sealed class ProfileService(ISupabaseGateway gw) : IProfileService
{
    public const int MaxBytes = 5 * 1024 * 1024;
    static readonly string[] Extensions = [".jpg", ".jpeg", ".png", ".webp"];
    // signed links last an hour; reuse one for 50 minutes instead of asking the server on every redraw
    static readonly TimeSpan LinkLife = TimeSpan.FromMinutes(50);
    readonly ConcurrentDictionary<string, (string Url, DateTime Until)> _links = new();

    public event Action? AvatarChanged;

    public Task<AppResult<MyProfile>> MeAsync() => Safe.RunAsync(async () =>
    {
        var uid = gw.CurrentUserId;
        var p = (await gw.ListAsync<Profile>(x => x.Id == uid)).FirstOrDefault() ?? throw new Exception("not_found");
        return new MyProfile(string.IsNullOrWhiteSpace(p.FullName) ? p.Username : p.FullName, p.AvatarPath);
    });

    public async Task<AppResult<string>> SetAvatarAsync(string fileName, byte[] data)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        if (!Extensions.Contains(ext) || data.Length == 0 || data.Length > MaxBytes)
            return AppResult<string>.Fail(AppErrorKind.Validation, "bad_image");

        // ponytail: a new file name per upload so the link is never stale; the old file stays in the bucket. Add cleanup when storage use matters.
        var result = await Safe.RunAsync(async () =>
        {
            var path = await gw.UploadAsync("avatars", $"{gw.CurrentUserId}/avatar-{DateTime.UtcNow:yyyyMMddHHmmss}{ext}", data);
            await gw.RpcAsync("set_my_avatar", new() { ["p_path"] = path });
            return path;
        });
        if (result.Ok) AvatarChanged?.Invoke();
        return result;
    }

    public Task<AppResult<string?>> AvatarUrlAsync(string? path) => Safe.RunAsync<string?>(async () =>
    {
        if (string.IsNullOrEmpty(path)) return null;
        if (_links.TryGetValue(path, out var hit) && hit.Until > DateTime.UtcNow) return hit.Url;
        var url = await gw.SignedUrlAsync("avatars", path);
        if (url is not null) _links[path] = (url, DateTime.UtcNow + LinkLife);
        return url;
    });
}
