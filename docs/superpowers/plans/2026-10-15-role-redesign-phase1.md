# Role Redesign, Phase 1: Foundation and Shell Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking. UI tasks (5, 6, 7): invoke the `frontend-design:frontend-design` skill first. Finish with `ponytail:ponytail-review` (CLAUDE.md).

**Goal:** Every signed-in screen gets one persistent header (profile picture, page title, alerts bell with unread badge, always-visible Sign out); everyone can set a profile picture; front desk on a phone gets a bottom bar; the color tokens are guarded by an accessibility test.

**Architecture:** Additive migration `0013` (avatar column, `avatars` bucket, `set_my_avatar` function, image columns). A small Core `ProfileService` handles the picture; a `HeaderViewModel` feeds a new `HeaderBar` control that each of the three shells puts in `Shell.TitleView`. The bell opens the existing Alerts page for now (the dropdown panel is phase 2). The existing design tokens in `Colors.xaml` and `Styles.xaml` are kept and verified, not replaced.

**Tech Stack:** .NET 10 MAUI (Windows + Android), CommunityToolkit.Mvvm and CommunityToolkit.Maui 15.0.1, xUnit, Supabase Postgres, embedded-Postgres local tests (`supabase/tests/local`).

**Spec:** `docs/superpowers/specs/2026-10-15-role-redesign-design.md` (phase 1 of section 4, database items in section 3).

## Global Constraints

- Migrations are additive only. Nothing is dropped. **`0012` is not yet on the real Supabase project and must be pushed before `0013`.**
- WCAG AA: text 4.5:1; status dots and other non-text indicators 3:1. Colors keep their meaning: color only ever signals status, and a status always also has a word.
- Palette: ink is the primary, Plate Blue is the one accent (focus), Plate Green, Yellow and Red are the semantic colors. No new colors in this phase.
- Light and dark mode both supported; every color is an `AppThemeBinding` over existing tokens.
- Pictures: JPG, PNG or WebP, 5 MB at most (same limits as the existing buckets).
- Writes go through database functions, never through a model update (the app's rule; `profiles` only allows `full_name` and `phone` column updates).
- Every new Core behavior has an xUnit test and runs against `DemoGateway`; every new database rule has a test in `supabase/tests/local/rules.mjs`.
- Commit messages end with `Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>`. Do not push or merge without the owner saying so. Never commit `docs/screenshotsforexample/`.

## Deviations from the spec (decided while planning)

| Spec says | This plan does | Why |
| --- | --- | --- |
| Extend the `profiles` column grant to `avatar_path` | A `set_my_avatar(p_path)` function instead | A grant would let a user point their picture at any path; the function checks the path starts with their own id, and the app only writes through functions anyway. |
| Shared controls: avatar, status chip, person card | Avatar only. The status chip already exists; new chip statuses (available, in use, unavailable) come in phase 3, person and service cards in phases 3 and 6 | Nothing uses them before then. |
| Front desk persistent sidebar or bottom bar | Bottom bar on phones only | Windows already has a locked (always visible) sidebar. |
| Bell with dropdown panel | Bell with badge opens the existing Alerts page | The panel is phase 2. |

## Review Focus

Failure modes the spec implies but the happy path will not show, most likely first. Each has a test in the task named in brackets.

1. A picked file that is not an image, is empty, or is over 5 MB: a plain-words message, nothing uploaded. [Task 3]
2. A name with one word, extra spaces, or nothing: initials never crash and fall back to `?`. [Task 3]
3. The picture link cannot be fetched (offline, expired, file missing): the header and Settings still show the initials, with no error banner. [Task 3, Task 4]
4. 0 unread hides the badge; more than 99 shows `99+`; the bell's spoken label includes the count. [Task 4]
5. Someone else's folder in `set_my_avatar`, a deactivated user, or writing `avatar_path` directly: all refused by the database. [Task 1]

---

### Task 1: Migration 0013 and its database tests

**Files:**
- Create: `supabase/migrations/0013_avatars.sql`
- Modify: `supabase/tests/local/rules.mjs` (append before the final `console.log` summary line)

**Interfaces:**
- Produces: column `profiles.avatar_path text`; columns `amenities.image_path text`, `membership_packages.image_path text`; storage bucket `avatars` (private, 5 MB, JPG/PNG/WebP; add to your own folder, any signed-in active user reads); function `public.set_my_avatar(p_path text) returns void` (null clears; path must start with `<own id>/`).

- [ ] **Step 1: Create the working branch**

```bash
git checkout role-redesign-spec && git checkout -b role-redesign-phase1
```

- [ ] **Step 2: Write the failing tests**

Append to `supabase/tests/local/rules.mjs`, just above the line `console.log(\`\n${pass} passed, ${failed} failed\`);`:

```js
console.log('\nprofile pictures and catalog images (0013)');
const avatarOf = async uid => (await run(uid, `select avatar_path from profiles where id = $1`, [uid])).rows[0].avatar_path;
await test('a member sets and clears their own picture', async () => {
  await run(alex, `select set_my_avatar($1)`, [`${alex}/avatar-1.jpg`]);
  assert.equal(await avatarOf(alex), `${alex}/avatar-1.jpg`);
  await run(alex, `select set_my_avatar(null)`);
  assert.equal(await avatarOf(alex), null);
});
await test("a path in someone else's folder is refused", async () => {
  assert.match(await code(run(alex, `select set_my_avatar($1)`, [`${bea}/avatar-1.jpg`])), /42501/);
});
await test('avatar_path cannot be written directly', async () => {
  assert.match(await code(run(alex, `update profiles set avatar_path = 'x' where id = $1`, [alex])), /42501/);
});
await test('a deactivated user cannot set a picture', async () => {
  await db.query(`update profiles set is_active = false where id = $1`, [bea]);
  assert.notEqual(await code(run(bea, `select set_my_avatar($1)`, [`${bea}/avatar-1.jpg`])), 'ok');
  await db.query(`update profiles set is_active = true where id = $1`, [bea]);
});
await test('the avatars bucket is private, images only, 5 MB', async () => {
  const b = (await db.query(`select public, file_size_limit, allowed_mime_types from storage.buckets where id = 'avatars'`)).rows[0];
  assert.equal(b.public, false);
  assert.equal(Number(b.file_size_limit), 5242880);
  assert.deepEqual([...b.allowed_mime_types].sort(), ['image/jpeg', 'image/png', 'image/webp']);
});
await test('storage: add only to your own folder; any signed-in user reads', async () => {
  await db.query(`grant select, insert on storage.objects to authenticated`);   // the real project grants this; the local stub does not
  const add = (uid, folder) => run(uid, `insert into storage.objects (bucket_id, name) values ('avatars', $1)`, [`${folder}/avatar-1.jpg`]);
  assert.equal(await code(add(alex, alex)), 'ok');
  assert.match(await code(add(alex, bea)), /42501/);
  assert.equal((await run(bea, `select name from storage.objects where bucket_id = 'avatars'`)).rowCount, 1);
});
await test('amenities and packages have an image_path, empty by default', async () => {
  assert.equal((await run(admin, `select image_path from amenities limit 1`)).rows[0].image_path, null);
  assert.equal((await run(admin, `select image_path from membership_packages limit 1`)).rows[0].image_path, null);
});
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `cd supabase/tests/local && npm test`
Expected: the new tests FAIL (`function set_my_avatar(unknown) does not exist`, `column "image_path" does not exist`); all earlier tests still pass.

- [ ] **Step 4: Write the migration**

Create `supabase/migrations/0013_avatars.sql`:

```sql
-- 0013: profile pictures and catalog images (role redesign, phase 1)
--
-- A picture lives in the avatars bucket under {user uuid}/..., and profiles.avatar_path remembers which file.
-- The app sets it through set_my_avatar (profiles only allows full_name and phone to be updated directly).
-- Package and amenity images are added here too; they are stored in the existing gym-assets bucket and used by later phases.

alter table public.profiles            add column avatar_path text;
alter table public.amenities           add column image_path  text;
alter table public.membership_packages add column image_path  text;

comment on column public.profiles.avatar_path is 'Path in the avatars bucket, {user uuid}/...; null shows initials';
comment on column public.amenities.image_path is 'Path in the gym-assets bucket';
comment on column public.membership_packages.image_path is 'Path in the gym-assets bucket';

insert into storage.buckets (id, name, public, file_size_limit, allowed_mime_types)
values ('avatars', 'avatars', false, 5242880, array['image/png', 'image/jpeg', 'image/webp'])
on conflict (id) do update
set public = excluded.public, file_size_limit = excluded.file_size_limit, allowed_mime_types = excluded.allowed_mime_types;

create policy "avatars: add to own folder" on storage.objects for insert to authenticated
	with check (bucket_id = 'avatars' and public.is_active_user() and (storage.foldername(name))[1] = auth.uid()::text);
create policy "avatars: signed-in read" on storage.objects for select to authenticated
	using (bucket_id = 'avatars' and public.is_active_user());

create function public.set_my_avatar(p_path text) returns void
language plpgsql security definer
set search_path = ''
as $$
begin
	perform public.require_active_user();
	if nullif(btrim(p_path), '') is not null and split_part(btrim(p_path), '/', 1) <> auth.uid()::text then
		perform public.fail('forbidden', '42501');
	end if;
	update public.profiles set avatar_path = nullif(btrim(p_path), '') where id = auth.uid();
end;
$$;

revoke all on function public.set_my_avatar(text) from public, anon, authenticated;
grant execute on function public.set_my_avatar(text) to authenticated;
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `cd supabase/tests/local && npm test`
Expected: `ok   0013_avatars.sql` during setup, then every test passes (57 previous + 7 new = 64 passed, 0 failed).

- [ ] **Step 6: Commit**

```bash
git add supabase/migrations/0013_avatars.sql supabase/tests/local/rules.mjs
git commit -m "feat(db): profile pictures, avatars bucket and catalog image columns (0013)

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 2: Accessibility guard for the color tokens

**Files:**
- Create: `tests/GymMembership.Tests/TestPaths.cs`, `tests/GymMembership.Tests/ContrastTests.cs`
- Modify: `src/GymMembership.App/Resources/Styles/Colors.xaml` (one value)

**Interfaces:**
- Produces: `TestPaths.AppFolder()` (path of `src/GymMembership.App`), used again in Task 5.

Measured today: every text pair already passes; `PlateYellowLight` (`#B58200`) is 2.96:1 on `ChalkLight`, below the 3:1 an indicator needs. This task adds the guard and fixes that one value.

- [ ] **Step 1: Write the failing test**

Create `tests/GymMembership.Tests/TestPaths.cs`:

```csharp
/// <summary>Finds the app project from the test's output folder (the solution file marks the root).</summary>
static class TestPaths
{
    public static string AppFolder()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "gym-membership.slnx"))) dir = dir.Parent;
        return Path.Combine(dir?.FullName ?? throw new InvalidOperationException("solution folder not found"), "src", "GymMembership.App");
    }
}
```

Create `tests/GymMembership.Tests/ContrastTests.cs`:

```csharp
using System.Xml.Linq;
using Xunit;

/// <summary>The color tokens must stay readable: WCAG AA is 4.5:1 for text and 3:1 for indicators such as status dots.</summary>
public class ContrastTests
{
    static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2009/xaml";
    static readonly Lazy<Dictionary<string, string>> Tokens = new(() =>
        XDocument.Load(Path.Combine(TestPaths.AppFolder(), "Resources", "Styles", "Colors.xaml")).Descendants()
            .Where(e => e.Name.LocalName == "Color" && e.Attribute(X + "Key") is not null)
            .ToDictionary(e => e.Attribute(X + "Key")!.Value, e => e.Value.Trim()));

    static double Luminance(string hex)
    {
        double Channel(int i) { var v = Convert.ToInt32(hex.Substring(1 + i * 2, 2), 16) / 255.0; return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4); }
        return 0.2126 * Channel(0) + 0.7152 * Channel(1) + 0.0722 * Channel(2);
    }

    static double Ratio(string a, string b)
    {
        var (hi, lo) = (Math.Max(Luminance(Tokens.Value[a]), Luminance(Tokens.Value[b])), Math.Min(Luminance(Tokens.Value[a]), Luminance(Tokens.Value[b])));
        return (hi + 0.05) / (lo + 0.05);
    }

    public static IEnumerable<object[]> TextPairs()
    {
        foreach (var (theme, ink, muted, inverse, inverseOn) in new[] { ("Light", "InkLight", "MutedLight", "White", "InkLight"), ("Dark", "InkDark", "MutedDark", "ChalkDark", "InkDark") })
        {
            foreach (var bg in new[] { "Chalk" + theme, "Surface" + theme }) { yield return new object[] { ink, bg }; yield return new object[] { muted, bg }; }
            yield return new object[] { inverse, inverseOn };   // button text, avatar letters
        }
        yield return new object[] { "White", "PlateRedLight" };       // unread badge, light
        yield return new object[] { "ChalkDark", "PlateRedDark" };    // unread badge, dark
    }

    public static IEnumerable<object[]> IndicatorPairs() =>
        from theme in new[] { "Light", "Dark" }
        from plate in new[] { "PlateBlue", "PlateYellow", "PlateGreen", "PlateRed" }
        from bg in new[] { "Chalk", "Surface" }
        select new object[] { plate + theme, bg + theme };

    [Theory, MemberData(nameof(TextPairs))]
    public void Text_pairs_reach_4_5_to_1(string fg, string bg) => Assert.True(Ratio(fg, bg) >= 4.5, $"{fg} on {bg} is {Ratio(fg, bg):0.00}:1");

    [Theory, MemberData(nameof(IndicatorPairs))]
    public void Status_colors_reach_3_to_1(string fg, string bg) => Assert.True(Ratio(fg, bg) >= 3.0, $"{fg} on {bg} is {Ratio(fg, bg):0.00}:1");
}
```

- [ ] **Step 2: Run to verify the one expected failure**

Run: `dotnet test tests/GymMembership.Tests --filter ContrastTests`
Expected: exactly one FAIL: `PlateYellowLight on ChalkLight is 2.96:1`. If anything else fails, stop and report it before changing any color.

- [ ] **Step 3: Fix the value**

In `src/GymMembership.App/Resources/Styles/Colors.xaml` change the one line:

```xml
    <Color x:Key="PlateYellowLight">#A47600</Color>  <!-- pending; was #B58200, 2.96:1 on ChalkLight, now 3.52:1 -->
```

- [ ] **Step 4: Run to verify all pass**

Run: `dotnet test tests/GymMembership.Tests --filter ContrastTests`
Expected: all PASS.

- [ ] **Step 5: Commit**

```bash
git add tests/GymMembership.Tests/TestPaths.cs tests/GymMembership.Tests/ContrastTests.cs src/GymMembership.App/Resources/Styles/Colors.xaml
git commit -m "test: guard the color tokens against WCAG AA; darken the light pending yellow

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 3: Profile service, initials and picture upload (Core)

**Files:**
- Create: `src/GymMembership.Core/Services/ProfileService.cs`, `tests/GymMembership.Tests/ProfileTests.cs`
- Modify: `src/GymMembership.Core/Models/Models.cs` (the `Profile` class), `src/GymMembership.Core/Services/Foundation.cs` (two error texts), `src/GymMembership.Core/Demo/DemoGateway.cs` (one `case`), `src/GymMembership.App/MauiProgram.cs` (one registration)

**Interfaces:**
- Produces:
  - `Initials.From(string? name) : string` (`"Mary-Ann O'Brien"` gives `"MO"`, `"alex"` gives `"A"`, blank gives `"?"`)
  - `record MyProfile(string Name, string? AvatarPath)`
  - `IProfileService` with `event Action? AvatarChanged`, `Task<AppResult<MyProfile>> MeAsync()`, `Task<AppResult<string>> SetAvatarAsync(string fileName, byte[] data)`, `Task<AppResult<string?>> AvatarUrlAsync(string? path)`
  - `ProfileService.MaxBytes = 5 * 1024 * 1024`

- [ ] **Step 1: Write the failing tests**

Create `tests/GymMembership.Tests/ProfileTests.cs`:

```csharp
using GymMembership.Core.Demo;
using GymMembership.Core.Services;
using Xunit;

public class ProfileTests
{
    static async Task<(DemoGateway Gw, ProfileService Svc)> As(string email)
    {
        var gw = new DemoGateway();
        await gw.SignInAsync(email, DemoGateway.DemoPassword);
        return (gw, new ProfileService(gw));
    }

    [Theory]
    [InlineData("Mary-Ann O'Brien", "MO")]
    [InlineData("Juan dela Cruz", "JC")]
    [InlineData("alex", "A")]
    [InlineData("  alex   tan ", "AT")]
    [InlineData("   ", "?")]
    [InlineData("", "?")]
    [InlineData(null, "?")]
    public void Initials_come_from_the_first_and_last_word(string? name, string expected) => Assert.Equal(expected, Initials.From(name));

    [Fact]
    public async Task Me_returns_the_name_and_no_picture_at_first()
    {
        var (_, svc) = await As("member@demo.io");
        var me = (await svc.MeAsync()).Value!;
        Assert.Equal("Alex Tan", me.Name);
        Assert.Null(me.AvatarPath);
    }

    [Fact]
    public async Task A_new_picture_is_stored_in_your_own_folder_and_announced()
    {
        var (_, svc) = await As("member@demo.io");
        var announced = 0;
        svc.AvatarChanged += () => announced++;

        var path = (await svc.SetAvatarAsync("My Photo.JPG", [1, 2, 3])).Value!;

        Assert.StartsWith("u-alex/", path);
        Assert.EndsWith(".jpg", path);
        Assert.Equal(path, (await svc.MeAsync()).Value!.AvatarPath);
        Assert.Equal(1, announced);
    }

    [Fact]
    public async Task Files_that_are_not_pictures_are_empty_or_too_big_are_refused_and_nothing_changes()
    {
        var (_, svc) = await As("member@demo.io");
        var announced = 0;
        svc.AvatarChanged += () => announced++;

        foreach (var (name, data) in new (string, byte[])[] { ("notes.pdf", [1]), ("photo.jpg", []), ("photo.png", new byte[ProfileService.MaxBytes + 1]), ("noextension", [1]) })
        {
            var r = await svc.SetAvatarAsync(name, data);
            Assert.Equal(AppErrorKind.Validation, r.Error);
            Assert.Equal("bad_image", r.Message);
        }
        Assert.Null((await svc.MeAsync()).Value!.AvatarPath);
        Assert.Equal(0, announced);
        Assert.Contains("5 MB", ErrorText.For(AppErrorKind.Validation, "bad_image"));
        Assert.Equal(AppErrorKind.Validation, ErrorMapper.Map(new Exception("bad_image")));
    }

    [Fact]
    public async Task No_picture_or_no_link_means_no_url_so_the_initials_show()
    {
        var (gw, svc) = await As("member@demo.io");
        Assert.Null((await svc.AvatarUrlAsync(null)).Value);
        Assert.Null((await svc.AvatarUrlAsync("")).Value);
        Assert.Null((await svc.AvatarUrlAsync("u-alex/avatar.jpg")).Value);   // the demo keeps no files
        Assert.True((await svc.AvatarUrlAsync("u-alex/avatar.jpg")).Ok);

        gw.FailWith = new HttpRequestException("offline");
        var offline = await svc.AvatarUrlAsync("u-alex/avatar.jpg");
        Assert.Equal(AppErrorKind.Offline, offline.Error);   // the caller shows initials and no banner
    }

    [Fact]
    public async Task A_failed_upload_changes_nothing_and_is_not_announced()
    {
        var (gw, svc) = await As("member@demo.io");
        var announced = 0;
        svc.AvatarChanged += () => announced++;
        gw.FailWith = new HttpRequestException("offline");

        Assert.Equal(AppErrorKind.Offline, (await svc.SetAvatarAsync("a.jpg", [1])).Error);
        Assert.Equal(0, announced);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/GymMembership.Tests --filter ProfileTests`
Expected: build FAILS (`Initials`, `ProfileService` do not exist).

- [ ] **Step 3: Add the model column**

In `src/GymMembership.Core/Models/Models.cs`, replace the `Profile` class (lines 21-23) with:

```csharp
[Table("profiles")] public class Profile : BaseModel
{ [PrimaryKey("id")] public string Id { get; set; } = ""; [Column("username")] public string Username { get; set; } = ""; [Column("full_name")] public string FullName { get; set; } = "";
  [Column("phone")] public string? Phone { get; set; } [Column("is_active")] public bool IsActive { get; set; } = true; [Column("must_change_password")] public bool MustChangePassword { get; set; }
  // written only by the set_my_avatar function
  [Column("avatar_path", ignoreOnInsert: true, ignoreOnUpdate: true)] public string? AvatarPath { get; set; } }
```

- [ ] **Step 4: Write the service**

Create `src/GymMembership.Core/Services/ProfileService.cs`:

```csharp
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
```

- [ ] **Step 5: Error texts, demo function, registration**

In `src/GymMembership.Core/Services/Foundation.cs`:
- In `ErrorMapper.FromMessage`, in the Validation `Has(...)` list, append `"bad_image"` after `"bad_input"`.
- In `ErrorText.For(AppErrorKind kind, string? raw)`, add above the `"bad_discount"` line:

```csharp
        "bad_image" => "Choose a JPG, PNG or WebP picture under 5 MB.",
```

In `src/GymMembership.Core/Demo/DemoGateway.cs`, add this case just above `case "mark_password_changed":`:

```csharp
            case "set_my_avatar":
            {
                var path = Opt("p_path");
                if (path is not null && !path.StartsWith(uid + "/")) throw Err("forbidden");
                (All<Profile>().FirstOrDefault(x => x.Id == uid) ?? throw Err("not_found")).AvatarPath = path;
                return null;
            }
```

In `src/GymMembership.App/MauiProgram.cs`, add after the `ISettingsService` line:

```csharp
		s.AddSingleton<IProfileService, ProfileService>();
```

- [ ] **Step 6: Run to verify they pass**

Run: `dotnet test tests/GymMembership.Tests --filter ProfileTests`
Expected: all PASS. Then `dotnet test tests/GymMembership.Tests` shows nothing else broke.

- [ ] **Step 7: Commit**

```bash
git add src/GymMembership.Core src/GymMembership.App/MauiProgram.cs tests/GymMembership.Tests/ProfileTests.cs
git commit -m "feat: profile service with picture upload and initials

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 4: HeaderViewModel

**Files:**
- Modify: `src/GymMembership.Core/ViewModels/MemberViewModels.cs` (append at the end of the file)
- Create: `tests/GymMembership.Tests/HeaderTests.cs`

**Interfaces:**
- Consumes: `IProfileService` (Task 3), `INotificationService`, `IAuthService`, `IAppNavigator`.
- Produces: `HeaderViewModel : LoadableViewModel` with `Letters : string`, `AvatarUrl : string?`, `UnreadCount : int`, `HasUnread : bool`, `UnreadText : string` (`"99+"` above 99), `AlertsLabel : string`, `Task RefreshAsync()`, `SignOutCommand`. It refreshes itself when `IProfileService.AvatarChanged` fires. Registered automatically by the project's convention (every `LoadableViewModel` subclass).

- [ ] **Step 1: Write the failing tests**

Create `tests/GymMembership.Tests/HeaderTests.cs`:

```csharp
using GymMembership.Core.Demo;
using GymMembership.Core.Services;
using GymMembership.Core.ViewModels;
using Xunit;

public class HeaderTests
{
    sealed class Nav : IAppNavigator
    {
        public bool AtLogin;
        public void GoTo(AppRole role, bool changePassword = false) { }
        public void GoToLogin() => AtLogin = true;
        public void GoToRegister() { }
        public void GoToMessages() { }
        public void GoToMembership() { }
        public void ApplyTheme(string theme) { }
    }

    static async Task<(DemoGateway Gw, HeaderViewModel Vm, Nav Nav)> Build(string email)
    {
        var gw = new DemoGateway();
        await gw.SignInAsync(email, DemoGateway.DemoPassword);
        var nav = new Nav();
        return (gw, new HeaderViewModel(new ProfileService(gw), new NotificationService(gw), new AuthService(gw), nav), nav);
    }

    [Fact]
    public async Task Refresh_shows_the_initials_and_the_unread_count()
    {
        var (gw, vm, _) = await Build("member@demo.io");
        await vm.RefreshAsync();

        var unread = (await new NotificationService(gw).ListAsync()).Value!.Count(n => n.ReadAt is null);
        Assert.Equal("AT", vm.Letters);
        Assert.Null(vm.AvatarUrl);
        Assert.True(unread > 0);
        Assert.Equal(unread, vm.UnreadCount);
        Assert.True(vm.HasUnread);
    }

    [Fact]
    public async Task Reading_a_notification_lowers_the_badge()
    {
        var (gw, vm, _) = await Build("member@demo.io");
        var notes = new NotificationService(gw);
        await vm.RefreshAsync();
        var before = vm.UnreadCount;

        await notes.MarkReadAsync((await notes.ListAsync()).Value!.First(n => n.ReadAt is null).Id);
        await vm.RefreshAsync();

        Assert.Equal(before - 1, vm.UnreadCount);
    }

    [Theory]
    [InlineData(0, false, "0", "Alerts")]
    [InlineData(1, true, "1", "Alerts, 1 unread")]
    [InlineData(99, true, "99", "Alerts, 99 unread")]
    [InlineData(100, true, "99+", "Alerts, 100 unread")]
    public async Task The_badge_hides_at_zero_caps_at_99_and_the_bell_says_the_count(int count, bool visible, string text, string label)
    {
        var (_, vm, _) = await Build("member@demo.io");
        vm.UnreadCount = count;
        Assert.Equal(visible, vm.HasUnread);
        Assert.Equal(text, vm.UnreadText);
        Assert.Equal(label, vm.AlertsLabel);
    }

    [Fact]
    public async Task If_the_server_cannot_be_reached_the_bar_stays_quiet()
    {
        var (gw, vm, _) = await Build("member@demo.io");
        gw.FailWith = new HttpRequestException("offline");
        await vm.RefreshAsync();   // must not throw

        Assert.Null(vm.ErrorMessage);   // a banner under the title bar would be noise
        Assert.Equal("?", vm.Letters);
    }

    [Fact]
    public async Task A_new_picture_refreshes_the_bar()
    {
        var (gw, _, _) = await Build("member@demo.io");
        var profile = new ProfileService(gw);
        var vm2 = new HeaderViewModel(profile, new NotificationService(gw), new AuthService(gw), new Nav());
        Assert.Equal("?", vm2.Letters);

        await profile.SetAvatarAsync("a.jpg", [1]);   // raises AvatarChanged, which makes the bar reload
        await Task.Delay(100);

        Assert.Equal("AT", vm2.Letters);
    }

    [Fact]
    public async Task Sign_out_ends_the_session_and_goes_to_the_login_page()
    {
        var (gw, vm, nav) = await Build("member@demo.io");
        await vm.SignOutCommand.ExecuteAsync(null);

        Assert.Null(gw.CurrentUserId);
        Assert.True(nav.AtLogin);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/GymMembership.Tests --filter HeaderTests`
Expected: build FAILS (`HeaderViewModel` does not exist).

- [ ] **Step 3: Write the view model**

Append to `src/GymMembership.Core/ViewModels/MemberViewModels.cs`:

```csharp
/// <summary>Feeds the bar at the top of every signed-in screen: your picture, the unread badge and Sign out.</summary>
public partial class HeaderViewModel : LoadableViewModel
{
    readonly IProfileService profile;
    readonly INotificationService notes;
    readonly IAuthService auth;
    readonly IAppNavigator nav;

    public HeaderViewModel(IProfileService profile, INotificationService notes, IAuthService auth, IAppNavigator nav)
    {
        (this.profile, this.notes, this.auth, this.nav) = (profile, notes, auth, nav);
        // ponytail: the service lives for the whole run and this object for one sign-in, so the handler is never removed
        profile.AvatarChanged += () => _ = RefreshAsync();
    }

    [ObservableProperty] private string letters = "?";
    [ObservableProperty] private string? avatarUrl;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasUnread), nameof(UnreadText), nameof(AlertsLabel))] private int unreadCount;

    public bool HasUnread => UnreadCount > 0;
    public string UnreadText => UnreadCount > 99 ? "99+" : UnreadCount.ToString();
    public string AlertsLabel => HasUnread ? $"Alerts, {UnreadCount} unread" : "Alerts";

    public override Task OnAppearingAsync() => RefreshAsync();

    /// <summary>Reloads quietly: a bar that cannot reach the server keeps what it had and shows no error.</summary>
    [RelayCommand]
    public async Task RefreshAsync()
    {
        if ((await profile.MeAsync()).Value is { } me)
        {
            Letters = Initials.From(me.Name);
            AvatarUrl = (await profile.AvatarUrlAsync(me.AvatarPath)).Value;
        }
        if ((await notes.ListAsync()).Value is { } list) UnreadCount = list.Count(n => n.ReadAt is null);
    }

    [RelayCommand] private async Task SignOutAsync() { await auth.SignOutAsync(); nav.GoToLogin(); }
}
```

- [ ] **Step 4: Run to verify they pass**

Run: `dotnet test tests/GymMembership.Tests --filter HeaderTests`
Expected: all PASS. Then `dotnet test tests/GymMembership.Tests`: everything passes.

- [ ] **Step 5: Commit**

```bash
git add src/GymMembership.Core/ViewModels/MemberViewModels.cs tests/GymMembership.Tests/HeaderTests.cs
git commit -m "feat: header view model with unread badge, picture and sign-out

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 5: Avatar control, HeaderBar and the three shells

**Invoke the `frontend-design:frontend-design` skill first.** Keep to the existing look: flat panels, hairlines, the Barlow fonts, no shadows.

**Files:**
- Create: `src/GymMembership.App/Controls/Avatar.cs`, `src/GymMembership.App/Controls/HeaderBar.xaml`, `src/GymMembership.App/Controls/HeaderBar.xaml.cs`, `src/GymMembership.App/Resources/Images/bell.svg`
- Modify: `src/GymMembership.App/MemberShell.xaml`, `MemberShell.xaml.cs`, `StaffShell.xaml`, `StaffShell.xaml.cs`, `CoachShell.xaml`, `CoachShell.xaml.cs`, `src/GymMembership.App/Services/AppServices.cs` (two lines), `tests/GymMembership.Tests/XamlBindingTests.cs`

**Interfaces:**
- Consumes: `HeaderViewModel` (Task 4), `TestPaths.AppFolder()` (Task 2).
- Produces: `Avatar` control with bindable `Letters`, `Url`, `Diameter` (default 40); `HeaderBar.Attach(Shell shell, IServiceProvider sp)`; shell constructors `StaffShell(AppRole role, IServiceProvider sp)` and `CoachShell(IServiceProvider sp)`; the Alerts item in each shell gets `Route="alerts"`.

- [ ] **Step 1: Make the binding test cover the Controls folder (it will fail first for the new XAML)**

In `tests/GymMembership.Tests/XamlBindingTests.cs`, replace `ViewsFolder()` and the two members that use it:

```csharp
    // every page and every control: a path under src/GymMembership.App
    public static IEnumerable<object[]> Pages() =>
        new[] { "Views", "Controls" }
            .SelectMany(d => Directory.GetFiles(Path.Combine(TestPaths.AppFolder(), d), "*.xaml"))
            .Select(f => new object[] { Path.GetRelativePath(TestPaths.AppFolder(), f) });

    [Theory]
    [MemberData(nameof(Pages))]
    public void Every_binding_on_the_page_points_at_a_real_property(string file)
    {
        var doc = XDocument.Load(Path.Combine(TestPaths.AppFolder(), file));
```

(Delete the old `ViewsFolder()` method. The rest of the test body is unchanged.)

Run: `dotnet test tests/GymMembership.Tests --filter XamlBindingTests`
Expected: all existing pages still PASS (their names now read `Views\...xaml`).

- [ ] **Step 2: Add the bell icon**

Create `src/GymMembership.App/Resources/Images/bell.svg` (Feather "bell", MIT; the project already includes `Resources\Images\*`):

```xml
<svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="#000000" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">
  <path d="M18 8A6 6 0 0 0 6 8c0 7-3 9-3 9h18s-3-2-3-9"/>
  <path d="M13.73 21a2 2 0 0 1-3.46 0"/>
</svg>
```

- [ ] **Step 3: Add the Avatar control**

Create `src/GymMembership.App/Controls/Avatar.cs`:

```csharp
using Microsoft.Maui.Controls.Shapes;

namespace GymMembership.App.Controls;

/// <summary>A round profile picture. The initials sit underneath, so they show while the picture loads and whenever it cannot.</summary>
public class Avatar : ContentView
{
    public static readonly BindableProperty LettersProperty = BindableProperty.Create(nameof(Letters), typeof(string), typeof(Avatar), "?",
        propertyChanged: (b, _, n) => ((Avatar)b)._letters.Text = n as string);
    public static readonly BindableProperty UrlProperty = BindableProperty.Create(nameof(Url), typeof(string), typeof(Avatar),
        propertyChanged: (b, _, n) => ((Avatar)b)._photo.Source = Uri.TryCreate(n as string, UriKind.Absolute, out var uri) ? ImageSource.FromUri(uri) : null);
    public static readonly BindableProperty DiameterProperty = BindableProperty.Create(nameof(Diameter), typeof(double), typeof(Avatar), 40d,
        propertyChanged: (b, _, _) => ((Avatar)b).Resize());

    public string? Letters { get => (string?)GetValue(LettersProperty); set => SetValue(LettersProperty, value); }
    public string? Url { get => (string?)GetValue(UrlProperty); set => SetValue(UrlProperty, value); }
    public double Diameter { get => (double)GetValue(DiameterProperty); set => SetValue(DiameterProperty, value); }

    readonly Label _letters = new() { Text = "?", HorizontalOptions = LayoutOptions.Center, VerticalOptions = LayoutOptions.Center, FontFamily = "BarlowSemiBold" };
    readonly Image _photo = new() { Aspect = Aspect.AspectFill };
    readonly Border _frame = new() { StrokeThickness = 0, Padding = 0 };

    public Avatar()
    {
        _frame.Content = new Grid { Children = { _letters, _photo } };
        _frame.SetAppThemeColor(Border.BackgroundColorProperty, Res("InkLight", Colors.Black), Res("InkDark", Colors.White));
        _letters.SetAppThemeColor(Label.TextColorProperty, Res("White", Colors.White), Res("ChalkDark", Colors.Black));
        Content = _frame;
        HorizontalOptions = LayoutOptions.Start;
        SemanticProperties.SetDescription(this, "Profile picture");
        Resize();
    }

    void Resize()
    {
        _frame.WidthRequest = _frame.HeightRequest = Diameter;
        _frame.StrokeShape = new RoundRectangle { CornerRadius = Diameter / 2 };
        _letters.FontSize = Diameter * 0.4;
    }

    static Color Res(string key, Color fallback) => Application.Current!.Resources.TryGetValue(key, out var v) && v is Color c ? c : fallback;
}
```

- [ ] **Step 4: Add the HeaderBar**

Create `src/GymMembership.App/Controls/HeaderBar.xaml`:

```xml
<?xml version="1.0" encoding="utf-8" ?>
<ContentView xmlns="http://schemas.microsoft.com/dotnet/2021/maui"
             xmlns:x="http://schemas.microsoft.com/winfx/2009/xaml"
             xmlns:toolkit="http://schemas.microsoft.com/dotnet/2022/maui/toolkit"
             xmlns:c="clr-namespace:GymMembership.App.Controls"
             xmlns:vm="clr-namespace:GymMembership.Core.ViewModels;assembly=GymMembership.Core"
             x:Class="GymMembership.App.Controls.HeaderBar" x:DataType="vm:HeaderViewModel">

    <Grid ColumnDefinitions="Auto,*,Auto,Auto" ColumnSpacing="8" HeightRequest="56"
          Padding="{OnIdiom Phone='4,0,8,0', Default='8,0,16,0'}">

        <c:Avatar Letters="{Binding Letters}" Url="{Binding AvatarUrl}" Diameter="36" VerticalOptions="Center"
                  SemanticProperties.Description="Your profile picture. Opens Settings.">
            <c:Avatar.GestureRecognizers><TapGestureRecognizer Tapped="OnAvatarTapped" /></c:Avatar.GestureRecognizers>
        </c:Avatar>

        <Label x:Name="TitleLabel" Grid.Column="1" Style="{StaticResource Heading}" VerticalOptions="Center" LineBreakMode="TailTruncation" />

        <!-- the bell: 48 x 48 touch target, unread count as a small red badge -->
        <Grid Grid.Column="2" WidthRequest="48" HeightRequest="48" VerticalOptions="Center">
            <ImageButton Source="bell.svg" Padding="12" Aspect="AspectFit" BackgroundColor="Transparent" Clicked="OnBellClicked"
                         SemanticProperties.Description="{Binding AlertsLabel}">
                <ImageButton.Behaviors>
                    <toolkit:IconTintColorBehavior TintColor="{AppThemeBinding Light={StaticResource InkLight}, Dark={StaticResource InkDark}}" />
                </ImageButton.Behaviors>
            </ImageButton>
            <Border IsVisible="{Binding HasUnread}" InputTransparent="True" HorizontalOptions="End" VerticalOptions="Start" Margin="0,4,2,0"
                    MinimumWidthRequest="18" HeightRequest="18" Padding="4,0" StrokeThickness="0" StrokeShape="RoundRectangle 9"
                    BackgroundColor="{AppThemeBinding Light={StaticResource PlateRedLight}, Dark={StaticResource PlateRedDark}}">
                <Label Text="{Binding UnreadText}" FontSize="11" FontFamily="BarlowSemiBold" HorizontalOptions="Center" VerticalOptions="Center"
                       TextColor="{AppThemeBinding Light={StaticResource White}, Dark={StaticResource ChalkDark}}" />
            </Border>
        </Grid>

        <Button Grid.Column="3" Text="Sign out" Style="{StaticResource SmallOutlineButton}" Command="{Binding SignOutCommand}" VerticalOptions="Center" />
    </Grid>
</ContentView>
```

Create `src/GymMembership.App/Controls/HeaderBar.xaml.cs`:

```csharp
using GymMembership.Core.ViewModels;

namespace GymMembership.App.Controls;

/// <summary>The bar at the top of every signed-in screen: your picture, the page title, the alerts bell and Sign out.</summary>
public partial class HeaderBar : ContentView
{
	public HeaderBar() => InitializeComponent();

	/// <summary>Called once by each shell. Follows the shell's current page for the title and refreshes the badge after every navigation.</summary>
	public void Attach(Shell shell, IServiceProvider sp)
	{
		var vm = sp.GetRequiredService<HeaderViewModel>();
		BindingContext = vm;
		void Update() { TitleLabel.Text = shell.CurrentPage?.Title; _ = vm.RefreshAsync(); }
		shell.Navigated += (_, _) => Update();
		shell.Loaded += (_, _) => Update();
	}

	static void GoTo(string route) => MainThread.BeginInvokeOnMainThread(async () => await Shell.Current.GoToAsync(route));
	void OnAvatarTapped(object? sender, TappedEventArgs e) => GoTo("//settings");
	void OnBellClicked(object? sender, EventArgs e) => GoTo("//alerts");
}
```

- [ ] **Step 5: Put the bar in the three shells**

In each of `MemberShell.xaml`, `StaffShell.xaml`, `CoachShell.xaml`:
1. Add `<Shell.TitleView><c:HeaderBar /></Shell.TitleView>` directly after the opening `<Shell ...>` tag (the `c:` namespace is already declared in all three).
2. Change `<FlyoutItem Title="Alerts">` to `<FlyoutItem Title="Alerts" Route="alerts">`.

`MemberShell.xaml.cs`: add one line at the end of the constructor, after `InitializeComponent();`:

```csharp
		((Controls.HeaderBar)GetTitleView(this)).Attach(this, sp);
```

(`sp` is the constructor parameter; move the line after the existing `_sp = sp; _access = access;` line if the compiler complains about order.)

`StaffShell.xaml.cs`: change to

```csharp
	public StaffShell(AppRole role, IServiceProvider sp)
	{
		InitializeComponent();
		((Controls.HeaderBar)GetTitleView(this)).Attach(this, sp);
		RoleLabel.Text = role == AppRole.Admin ? "Admin" : "Front desk";
		// (the rest of the constructor stays as it is)
```

`CoachShell.xaml.cs`:

```csharp
public partial class CoachShell : Shell
{
	public CoachShell(IServiceProvider sp)
	{
		InitializeComponent();
		((Controls.HeaderBar)GetTitleView(this)).Attach(this, sp);
	}
}
```

`src/GymMembership.App/Services/AppServices.cs`: in `GoTo`, change `new CoachShell()` to `new CoachShell(sp)` and `new StaffShell(role)` to `new StaffShell(role, sp)`.

- [ ] **Step 6: Build and run every test**

Run:
```bash
dotnet build src/GymMembership.App -f net10.0-windows10.0.19041.0
dotnet build src/GymMembership.App -f net10.0-android
dotnet test tests/GymMembership.Tests
```
Expected: both builds succeed; all tests PASS, including `XamlBindingTests` for `Controls\HeaderBar.xaml`.

- [ ] **Step 7: Look at it (Windows and the Android emulator)**

Windows: `dotnet run --project src/GymMembership.App -f net10.0-windows10.0.19041.0`. Sign in as each demo role (`member@`, `employee@`, `coach@demo.io`, password `demo1234`) and check, in light and dark mode: the bar shows picture initials, the page title changes when you click menu items, the bell badge shows the unread count, the bell opens Alerts, Sign out returns to login, tapping the avatar opens Settings.

Android: emulator `pixel_7_-_api_36_0`. Uninstall the app first, or build with `-p:EmbedAssembliesIntoApk=true` (stale `.__override__` assemblies otherwise). Check the same list at phone width: the title truncates instead of pushing Sign out off screen.

If the title stays empty on the first page, the `Loaded` hook did not fire; keep the `Navigated` hook and add `Dispatcher.Dispatch(Update)` at the end of `Attach`.

- [ ] **Step 8: Commit**

```bash
git add src/GymMembership.App tests/GymMembership.Tests/XamlBindingTests.cs
git commit -m "feat: persistent header with profile picture, alerts bell and sign-out on every shell

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 6: Profile picture on the Settings page

**Invoke the `frontend-design:frontend-design` skill first.**

**Files:**
- Modify: `src/GymMembership.Core/ViewModels/MemberViewModels.cs` (`SettingsViewModel`), `src/GymMembership.App/Views/SettingsPage.xaml`
- Create: `tests/GymMembership.Tests/SettingsPictureTests.cs`

**Interfaces:**
- Consumes: `IProfileService`, `IProofPicker` (the existing photo picker, `Task<(string FileName, byte[] Data)?> PickAsync()`), `Avatar` (Task 5).
- Produces: `SettingsViewModel.Letters`, `SettingsViewModel.AvatarUrl`, `SettingsViewModel.ChangePhotoCommand`. The constructor gains two parameters: `SettingsViewModel(ISettingsService settings, IAuthService auth, IAppNavigator nav, IProfileService profile, IProofPicker picker)`.

- [ ] **Step 1: Write the failing tests**

Create `tests/GymMembership.Tests/SettingsPictureTests.cs`:

```csharp
using GymMembership.Core.Demo;
using GymMembership.Core.Services;
using GymMembership.Core.ViewModels;
using Xunit;

public class SettingsPictureTests
{
    sealed class Picker((string, byte[])? pick) : IProofPicker
    {
        public Task<(string FileName, byte[] Data)?> PickAsync() => Task.FromResult(pick is { } p ? ((string, byte[])?)p : null);
    }

    sealed class Nav : IAppNavigator
    {
        public void GoTo(AppRole role, bool changePassword = false) { }
        public void GoToLogin() { }
        public void GoToRegister() { }
        public void GoToMessages() { }
        public void GoToMembership() { }
        public void ApplyTheme(string theme) { }
    }

    static async Task<(SettingsViewModel Vm, ProfileService Profile)> Build((string, byte[])? pick)
    {
        var gw = new DemoGateway();
        await gw.SignInAsync("member@demo.io", DemoGateway.DemoPassword);
        var profile = new ProfileService(gw);
        return (new SettingsViewModel(new SettingsService(gw), new AuthService(gw), new Nav(), profile, new Picker(pick)), profile);
    }

    [Fact]
    public async Task Opening_settings_shows_the_initials()
    {
        var (vm, _) = await Build(null);
        await vm.OnAppearingAsync();
        Assert.Equal("AT", vm.Letters);
    }

    [Fact]
    public async Task Choosing_a_picture_saves_it_and_says_so()
    {
        var (vm, profile) = await Build(("me.png", [1, 2, 3]));
        await vm.ChangePhotoCommand.ExecuteAsync(null);

        Assert.NotNull((await profile.MeAsync()).Value!.AvatarPath);
        Assert.Equal("Picture updated.", vm.Notice);
        Assert.Null(vm.ErrorMessage);
    }

    [Fact]
    public async Task Cancelling_the_picker_changes_nothing()
    {
        var (vm, profile) = await Build(null);
        await vm.ChangePhotoCommand.ExecuteAsync(null);

        Assert.Null((await profile.MeAsync()).Value!.AvatarPath);
        Assert.Null(vm.Notice);
    }

    [Fact]
    public async Task A_file_that_is_not_a_picture_shows_plain_words()
    {
        var (vm, profile) = await Build(("cv.pdf", [1]));
        await vm.ChangePhotoCommand.ExecuteAsync(null);

        Assert.Equal("Choose a JPG, PNG or WebP picture under 5 MB.", vm.ErrorMessage);
        Assert.Null((await profile.MeAsync()).Value!.AvatarPath);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/GymMembership.Tests --filter SettingsPictureTests`
Expected: build FAILS (constructor mismatch, `Letters` missing).

- [ ] **Step 3: Extend the view model**

In `src/GymMembership.Core/ViewModels/MemberViewModels.cs`, change the `SettingsViewModel` declaration and `OnAppearingAsync`, and add the picture members:

```csharp
public partial class SettingsViewModel(ISettingsService settings, IAuthService auth, IAppNavigator nav, IProfileService profile, IProofPicker picker) : LoadableViewModel
{
    public string[] Themes { get; } = ["system", "light", "dark"];
    [ObservableProperty] private string theme = "system";
    [ObservableProperty] private bool mustChange;
    [ObservableProperty] private string letters = "?";
    [ObservableProperty] private string? avatarUrl;

    public override async Task OnAppearingAsync()
    {
        await LoadAsync();
        await RunAsync(auth.MustChangePasswordAsync, m => MustChange = m);
        await LoadPhotoAsync();
    }

    async Task LoadPhotoAsync()
    {
        if ((await profile.MeAsync()).Value is not { } me) return;
        Letters = Initials.From(me.Name);
        AvatarUrl = (await profile.AvatarUrlAsync(me.AvatarPath)).Value;   // no link: the initials stay
    }

    [RelayCommand]
    private async Task ChangePhotoAsync()
    {
        Notice = null;
        if (await picker.PickAsync() is not { } file) return;
        if (!await RunAsync(() => profile.SetAvatarAsync(file.FileName, file.Data), _ => { })) return;
        await LoadPhotoAsync();
        Notice = "Picture updated.";
    }
```

Leave `LoadAsync`, `SaveAsync`, the password members and `SignOutAsync` exactly as they are.

- [ ] **Step 4: Add the section to the page**

In `src/GymMembership.App/Views/SettingsPage.xaml`, directly after the two `<c:Banner .../>` lines and before the Theme block, insert:

```xml
            <HorizontalStackLayout Spacing="16">
                <c:Avatar Letters="{Binding Letters}" Url="{Binding AvatarUrl}" Diameter="72" />
                <VerticalStackLayout Spacing="6" VerticalOptions="Center">
                    <Label Text="Profile picture" Style="{StaticResource Heading}" />
                    <Button Text="Change picture" Style="{StaticResource SmallOutlineButton}" Command="{Binding ChangePhotoCommand}" HorizontalOptions="Start" />
                </VerticalStackLayout>
            </HorizontalStackLayout>

            <c:LaneView Margin="0,12"
                        DashColor="{AppThemeBinding Light={StaticResource LineLight}, Dark={StaticResource LineDark}}"
                        TrackColor="{AppThemeBinding Light={StaticResource LineLight}, Dark={StaticResource LineDark}}" />
```

- [ ] **Step 5: Run to verify they pass, then look at it**

Run: `dotnet test tests/GymMembership.Tests` (everything, including `XamlBindingTests` for `SettingsPage.xaml`), then `dotnet build src/GymMembership.App -f net10.0-android` and `-f net10.0-windows10.0.19041.0`.
In demo mode (the default) the picture cannot display because the demo keeps no files; the initials staying is correct, and "Picture updated." appearing proves the flow. Checking a real picture needs the real Supabase project with migrations `0012` and `0013` already pushed by the owner: choose a picture, confirm the header avatar updates without navigating, sign out and in, confirm it persists. If the owner has not pushed them yet, say so in the final report instead of running it.

- [ ] **Step 6: Commit**

```bash
git add src tests/GymMembership.Tests/SettingsPictureTests.cs
git commit -m "feat: choose a profile picture on the Settings page

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 7: Front desk bottom bar on phones

**Invoke the `frontend-design:frontend-design` skill first.**

**Files:**
- Modify: `src/GymMembership.App/StaffShell.xaml`, `src/GymMembership.App/StaffShell.xaml.cs`

On Windows the locked sidebar already stays on screen, so only phones change. The four daily screens move into a bottom `TabBar`; admin pages, Alerts and Settings stay in the menu. There is no unit test for this (it is shell layout); it is verified on the emulator in Step 3.

- [ ] **Step 1: Mark the four daily items**

In `StaffShell.xaml`, add `ClassId="desk"` to these four items:

```xml
    <FlyoutItem Title="Payments to verify" ClassId="desk"><ShellContent ContentTemplate="{DataTemplate views:VerifyPaymentsPage}" /></FlyoutItem>
    <FlyoutItem Title="Counter payment" ClassId="desk"><ShellContent ContentTemplate="{DataTemplate views:CounterPage}" /></FlyoutItem>
    <FlyoutItem Title="Check-in" ClassId="desk"><ShellContent ContentTemplate="{DataTemplate views:CheckInPage}" /></FlyoutItem>
    <FlyoutItem Title="Amenity visits" ClassId="desk"><ShellContent ContentTemplate="{DataTemplate views:VerifyAmenityUsagesPage}" /></FlyoutItem>
```

- [ ] **Step 2: Build the bar on phones**

In `StaffShell.xaml.cs`, at the end of the constructor (after the existing `if (role != AppRole.Admin) ...` block):

```csharp
		if (DeviceInfo.Idiom == DeviceIdiom.Phone) MoveDeskItemsToBottomBar();
	}

	// short names fit under a bottom-bar icon slot; the pages themselves keep their full titles
	static readonly Dictionary<string, string> ShortNames = new()
	{
		["Payments to verify"] = "Verify", ["Counter payment"] = "Counter", ["Check-in"] = "Check-in", ["Amenity visits"] = "Amenities",
	};

	void MoveDeskItemsToBottomBar()
	{
		var bar = new TabBar();
		foreach (var item in Items.Where(i => i.ClassId == "desk").ToList())
		{
			var page = (ShellContent)item.Items[0].Items[0];
			bar.Items.Add(new Tab { Title = ShortNames.GetValueOrDefault(item.Title, item.Title), Items = { new ShellContent { ContentTemplate = page.ContentTemplate } } });
			Items.Remove(item);
		}
		Items.Insert(0, bar);
	}
```

(Move the closing brace of the constructor accordingly; the file keeps one class.)

- [ ] **Step 3: Build and check on the emulator**

Run: `dotnet build src/GymMembership.App -f net10.0-android` and `-f net10.0-windows10.0.19041.0`.
Emulator (`pixel_7_-_api_36_0`, uninstall the app first): sign in as `employee@demo.io`. Expected: four tabs along the bottom (Verify, Counter, Check-in, Amenities) that stay visible while switching; the header bar stays on top; the menu (hamburger) still lists Alerts and Settings; signing in as `admin@demo.io` also shows the admin pages in the menu. On Windows: no bottom bar, the locked sidebar lists all items as before.
If a tab bar and flyout items conflict on Android (menu empty, or the first page not selected), report it instead of working around it: the fallback is to keep all items in the menu on phones and revisit in phase 5 with the Cashier redesign.

- [ ] **Step 4: Commit**

```bash
git add src/GymMembership.App/StaffShell.xaml src/GymMembership.App/StaffShell.xaml.cs
git commit -m "feat: front desk bottom bar on phones

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

---

### Task 8: Wrap-up

**Files:**
- Modify: `docs/superpowers/specs/2026-10-15-role-redesign-design.md`

- [ ] **Step 1: Record the deviations in the spec**

In the spec's section 3.2 table, change the `profiles` row to: `avatar_path`; optional `last_seen_at`. **The picture is set through the function `set_my_avatar`, not a widened column grant.** In section 3.3, add the row: `set_my_avatar(path)`: sets the caller's picture; refuses a path outside their own folder. In section 4, phase 1, add: `Status chip statuses (available, in use, unavailable) and person and service cards arrive with phases 3 and 6; the front desk bottom bar is phones only (Windows keeps the locked sidebar); the bell opens the existing Alerts page until phase 2.`

- [ ] **Step 2: Full verification**

Run all of:
```bash
dotnet test tests/GymMembership.Tests
(cd supabase/tests/local && npm test)
dotnet build src/GymMembership.App -f net10.0-android
dotnet build src/GymMembership.App -f net10.0-windows10.0.19041.0
```
Expected: every test passes (previous 143 plus the new ones), 64 database checks pass, both builds succeed. Report the exact counts.

- [ ] **Step 3: Review**

Run `ponytail:ponytail-review` on the branch diff (`git diff role-redesign-spec...HEAD`) and fix what it finds. Re-run Step 2.

- [ ] **Step 4: Commit and hand over**

```bash
git add docs/superpowers/specs/2026-10-15-role-redesign-design.md
git commit -m "docs: phase 1 deviations recorded in the redesign spec

Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>"
```

Stop here. Do not push or merge. Tell the owner: the branch `role-redesign-phase1` is ready, and that `0012` then `0013` must be run on the real Supabase project (`npx supabase db push`, after the pre-check query at the top of `0012`) before the picture upload can work against it.
