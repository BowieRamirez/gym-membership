# CI/CD Pipelines: Windows (MSIX and MSI) and Android (sideload)

| | |
| --- | --- |
| **Status** | Phases 1 to 3b are implemented in the repository (workflows, installer definition, project switches). Phase 4 (secrets and the first `v0.1.0a` release) needs the owner. |
| **Date** | 2026-10-14 |
| **Scope** | Build, test, sign and publish the .NET MAUI app for **Windows** and **Android** using GitHub Actions hosted runners. Testers and users install from the repository's **Releases** page. |
| **Out of scope** | iOS / Mac Catalyst, Google Play and Microsoft Store submission, auto-update inside the app, deploying Supabase migrations and Edge Functions. |

## 1. Goals

1. Every pull request and every push to `main` proves the code builds for both platforms and the tests pass.
2. A version tag (the first one is `v0.1.0a`) produces a signed **Android APK** and signed **Windows installers (MSIX and MSI)**, attached to a GitHub Release that anyone with the link can download.
3. The Android and Windows pipelines are **independent**: separate workflow files, separate runners, separate workloads, separate secrets. A failure, a slow runner or a flaky workload install on one platform never blocks, cancels or corrupts the other.
4. Builds are reproducible: pinned .NET SDK, pinned MAUI workload set, pinned runner images, no secrets in logs.

## 2. What the codebase tells us (inputs to the design)

Read from the repository on 2026-10-14.

| Finding | Where | Consequence for CI |
| --- | --- | --- |
| Solution `gym-membership.slnx` holds `GymMembership.App` (MAUI), `GymMembership.Core` (`net10.0`) and `GymMembership.Tests` (`net10.0`, xUnit, 143 tests). | `gym-membership.slnx` | Tests and Core need **no MAUI workload**. They get their own fast Linux job. |
| App targets are chosen **by the host OS**: `net10.0-android` always, `net10.0-ios` on macOS, `net10.0-windows10.0.19041.0` on Windows. | `GymMembership.App.csproj` lines 4-6 | A Windows runner would build **Android too**, and `dotnet restore` fails (NETSDK1147) unless both workloads are installed. This is the main way the two platforms could "mess up" each other. See section 4.1. |
| MAUI `10.0.90`, .NET SDK `10.0.401` locally, `CommunityToolkit.Maui 15.0.1`, `Supabase 8.2.0`. | csproj, `dotnet --version` | Pin the SDK and the workload set. The GitHub Windows image carries SDK 10.0.401. |
| No `global.json`, no `.github/`, no `.gitattributes`, no `Directory.Build.props`, no `nuget.config`. | repo root | All of those are new files in this spec. |
| `WindowsPackageType` is `None` (unpackaged). | csproj line 38 | Day-to-day F5 stays unpackaged. CI overrides it to `MSIX` on the command line. |
| `Package.appxmanifest` still has the template identity: `Publisher="CN=User Name"`. | `Platforms/Windows/Package.appxmanifest` | The signing certificate's subject **must equal** the manifest `Publisher`. We set a real value. |
| `ApplicationId = com.companyname.gymmembership.app`, `ApplicationDisplayVersion = 1.0`, `ApplicationVersion = 1`. | csproj | `com.companyname` is the template placeholder. The Android package name and the MSIX identity become permanent once people install the app. **Decided 2026-10-14:** the company is **Slim & Shape**, so the id becomes `com.slimshape.gymmembership` (section 12). Both version numbers are overridden per build. |
| Backend settings are read from `Resources/Raw/supabase.json`, which is **git-ignored**. If the file is missing or still a placeholder the app runs in **demo mode**. | `SupabaseConfig.Load`, `.gitignore` | A plain CI build produces a demo-mode app. Release builds write `supabase.json` from GitHub secrets. |
| Android manifest is minimal (INTERNET, ACCESS_NETWORK_STATE), `SupportedOSPlatformVersion` 21. No signing config in the csproj. | `AndroidManifest.xml` | Signing is passed on the command line from secrets. |
| `supabase/tests/local` runs 57 database checks against an embedded Postgres (`npm test`, no Docker). | `supabase/tests/local/package.json` | Cheap to run on Linux. Included in the quality workflow. |
| Repository is **public**, default branch `main`. | GitHub API | Standard hosted runners are free. Artifacts and releases are world-readable. Secrets are not exposed to pull requests from forks. |
| Local `dotnet workload list`: `android`, `ios`, `maccatalyst`, `maui-windows` at manifest band 10.0.100. | developer machine | The CI Windows job needs only `maui-windows`, the Android job only `maui-android`. |

## 3. Pipeline overview

```
 push / pull_request / tag v*
        |
        +--> tests.yml      (ubuntu-24.04)   unit tests + database tests         -> required check
        |
        +--> android.yml    (ubuntu-24.04)   maui-android workload only
        |                                    PlatformOnly=android
        |                                    signed APK  ---------------+
        |                                                               |  tag v* only
        +--> windows.yml    (windows-2025)   maui-windows workload only  +--> GitHub Release (assets)
                                             PlatformOnly=windows
                                             signed MSIX + .cer  -------+
```

Three workflow files, no `needs:` between them, no reusable workflow shared between the two platforms. The only thing they share is the Release object, and each one creates it idempotently if it is missing (section 7).

| Workflow | File | Runner | Triggers | Produces |
| --- | --- | --- | --- | --- |
| Tests | `.github/workflows/tests.yml` | `ubuntu-24.04` | PR, push `main`, manual | Test results. Required status checks `unit` and `database`. |
| Android | `.github/workflows/android.yml` | `ubuntu-24.04` | PR, push `main`, tag `v*`, manual | Signed `*.apk` artifact; on a tag, a Release asset. |
| Windows | `.github/workflows/windows.yml` | `windows-2025` | PR, push `main`, tag `v*`, manual | Signed `*.msix` + `.cer` artifact; on a tag, Release assets. |

Runner labels are pinned (`ubuntu-24.04`, `windows-2025`), not `*-latest`. The `windows-latest` label is being moved to a Visual Studio 2026 image, which would change the toolchain under a build nobody touched.

## 4. Design decisions

### 4.1 One platform per build: the `PlatformOnly` switch

The App project picks its target frameworks from the host OS, so on a Windows runner `dotnet publish -f net10.0-windows...` still restores **all** targets, including Android, and fails unless the Android workload is also installed. Installing both workloads would couple the jobs and add a large download to the Windows build.

Passing `-p:TargetFrameworks=...` on the command line does **not** work: a global property flows into the `GymMembership.Core` project reference and turns Core into an Android project (NETSDK1005). This was tried locally; see section 10.

**Decision:** add two lines to the App csproj, after the existing `TargetFrameworks` lines:

```xml
<!-- CI builds one platform at a time: -p:PlatformOnly=android or -p:PlatformOnly=windows. A custom property is used
     because a global TargetFrameworks would also reach the Core project reference and break it. -->
<TargetFrameworks Condition="'$(PlatformOnly)' == 'android'">net10.0-android</TargetFrameworks>
<TargetFrameworks Condition="'$(PlatformOnly)' == 'windows'">net10.0-windows10.0.19041.0</TargetFrameworks>
```

Without the property nothing changes, so Visual Studio and local builds behave as before. Each CI job then needs exactly one workload: `maui-android` on Linux, `maui-windows` on Windows.

### 4.2 Why Android builds on Linux and Windows builds on Windows

- Android: the `maui-android` workload is supported on Linux. The `ubuntu-24.04` image already has JDK 17/21 (`JAVA_HOME_21_X64`), `ANDROID_HOME`, Android SDK platform 36 and build-tools 36.x. Linux runners start faster and the Android job never needs Windows-only components.
- Windows: WinUI/MAUI Windows builds only run on Windows. The `windows-2025` image has the .NET 10.0.401 SDK and a Windows SDK.
- The full `maui` workload is **not** installed on Linux (it pulls iOS/macOS packs that do not exist there). The MAUI team does the same: `maui-android` on Linux, `maui` elsewhere.

### 4.3 Reproducibility

- **SDK:** add `global.json` with `"version": "10.0.401"`, `"rollForward": "latestPatch"`. CI uses `actions/setup-dotnet` with `global-json-file`.
- **Workloads:** .NET 10 uses workload sets by default. CI pins the set explicitly: `dotnet workload install maui-android --version 10.0.401` (Android job) and `dotnet workload install maui-windows --version 10.0.401` (Windows job). The version lives in one place, an `env` value at the top of each workflow. Do **not** put `workloadVersion` in `global.json`: it would force workload-set mode on every developer machine. A past MAUI manifest was briefly missing from NuGet and broke every unpinned CI run (dotnet/maui#33987); pinning is the recommended protection.
- **Runner images:** pinned labels (section 3).
- **NuGet:** `actions/cache` on `~/.nuget/packages` (Linux) / `%USERPROFILE%\.nuget\packages` (Windows), keyed on the csproj files and `global.json`.
- **Actions:** only first-party `actions/*` actions, pinned to the current major (`checkout@v7`, `setup-dotnet@v6`, `setup-java@v6`, `setup-node@v7`, `cache@v6`, `upload-artifact@v7`). Dependabot keeps them current (section 8).

### 4.4 Windows installers: MSIX and MSI, both shipped

**Decision (2026-10-14): ship both.** Large desktop products usually offer an MSI (or EXE) for managed or per-machine installs and an MSIX for modern, clean installs. The two formats solve different problems:

| | MSIX | MSI |
| --- | --- | --- |
| Built from | `dotnet publish` with `WindowsPackageType=MSIX` (native MAUI output) | WiX Toolset v5 over an unpackaged, self-contained publish folder |
| Install / uninstall | Per user, clean uninstall, no leftover files | Per machine under Program Files, standard Add/Remove Programs |
| Updates | Install a higher version over the old one | Major-upgrade rule in the installer |
| Admin rights | Only to trust the self-signed certificate (once) | Yes (per-machine install) |
| Self-signed signature | Windows **refuses** to install until the certificate is trusted on that PC | Installs after a SmartScreen "More info, Run anyway" click; no certificate to trust |
| Managed rollout | Intune / winget / Store ready | `msiexec /i ... /qn` for Group Policy, Intune, scripts |
| Build cost | Almost free (a few MSBuild properties) | A hand-maintained `.wxs`, a second build of the app |

Recommendation to testers: **MSIX first** (cleanest, updates itself in place); use the **MSI** on PCs where trusting a certificate is not allowed or where IT deploys with scripts. Install one or the other on a PC, not both. The release notes say so.

**MSIX details**

- **Self-signed certificate.** CI signs with a self-signed certificate (the normal approach for test builds). Windows only installs the package if the machine trusts that certificate. The release therefore ships a **sideload package** (a zip of the folder MSBuild already generates): the `.msix`, the **public** certificate, and Microsoft's own `Install.ps1` / `Add-AppDevPackage.ps1`, which trust the certificate and install the app in one step. No custom installer script is written or maintained. Testers do this once per PC; later versions signed with the same certificate install normally.
- **Subject must match.** The certificate subject (`CN=SlimShape`) must equal the manifest `Publisher`. Signing fails otherwise. The subject deliberately has no `&`: an ampersand needs XML escaping in the manifest and is a common cause of subject mismatches. The human-readable name "Slim & Shape" is used for `PublisherDisplayName` and the MSI manufacturer.
- **Self-contained.** `-p:WindowsAppSDKSelfContained=true` bundles the Windows App SDK runtime, so the PC needs nothing pre-installed.
- **Upgrade path.** If the project later buys a trusted certificate (or uses Azure Trusted Signing), only the secret and the manifest `Publisher` change; the workflow is unchanged and the "trust this certificate" step disappears.

**MSI details**

- A separate `msi` job in `windows.yml` (`continue-on-error: true`, no `needs`), so an MSI problem can never block the MSIX, the APK or the Release.
- Steps: `dotnet publish` unpackaged and self-contained (`WindowsPackageType=None`, `SelfContained=true`) to a folder; `dotnet tool install --global wix --version 5.*`; `wix build installer/windows/Product.wxs` (WiX v5's `Files` element harvests the folder, so no `heat` step); sign the `.msi` with `signtool` and the same `.pfx`. The job is in `.github/workflows/windows.yml`, the definition in `installer/windows/Product.wxs`.
- A stable **UpgradeCode** GUID (`0D54EA34-C1B2-43CB-BEB8-58A34D7FD682`) is created once and never changed. The MSI product version is `major.minor.<run number>`: Windows Installer compares only the first three parts, so the run number goes in the third and every build upgrades the one before.

### 4.5 Android: sideload APK

- Format: `-p:AndroidPackageFormats=apk` (the Release default is `aab;apk`; an AAB cannot be sideloaded and the `env:` password prefix is not supported for AAB).
- ABI: **one universal APK with `arm64-v8a` and `x86_64`**. Phones use arm64; testers on PC emulators (Android Studio) use x86_64; nobody has to choose a file. The ABI list is set in the csproj for Release builds only (`<RuntimeIdentifiers>android-arm64;android-x64</RuntimeIdentifiers>`); Debug builds stay single-ABI and fast. Passing the list on the command line does not work (`MSB1006` / `NETSDK1083`, see section 10). Measured size: **32.8 MB**. 32-bit ARM (`armeabi-v7a`) is not included; add `android-arm` if an old phone ever needs it.
- Signing: a release keystore stored as a base64 secret. **The same keystore must sign every release.** Android refuses to update an app signed with a different key; users would have to uninstall and lose local data. The keystore needs an offline backup (section 9).
- `ApplicationVersion` (Android `versionCode`) is the workflow run number, which only ever increases, so every build installs over the previous one. `ApplicationDisplayVersion` is the tag version.

### 4.6 Backend settings in builds

| Build | `supabase.json` | App behavior |
| --- | --- | --- |
| Pull request | not written | Demo mode (in-memory sample data). Safe for forks. |
| Push to `main` | written from secrets | Talks to the real Supabase project (testers). |
| Tag `v*` | written from secrets | Talks to the real Supabase project (users). |

The file is created with `jq` from `SUPABASE_URL` and `SUPABASE_ANON_KEY`. The anon key is designed to ship inside client apps, but anyone can extract it from a downloaded APK or MSIX, so **row-level security must hold**. The service-role key must **never** appear in CI.

### 4.7 Supabase readiness for testers

Release builds talk to the real Supabase project, so the project must be ready **and** every release build must prove it can reach it. Two things protect testers: a one-time readiness checklist for the owner, and an automatic smoke check inside each platform workflow.

**State of the connected project, probed on 2026-10-14** (read-only requests with the anon key from `supabase.json`; nothing was created or changed):

| Probe | Result | Meaning |
| --- | --- | --- |
| Auth service health | `200`, GoTrue v2.197.0, about 100 ms per call | Project is up and fast from here. |
| Anon key | Valid JWT, role `anon`, issued for this exact project, expires 2036 | The key in `supabase.json` matches the URL. |
| Tables (`profiles`, `payments`, `coaches`, `membership_packages`, `amenities`, `gym_settings`, `roles`) | `42501 permission denied` for the anon role | The tables exist and are closed to signed-out users, as the row-level-security design intends. A wrong key would say "Invalid API key" instead. |
| Edge Functions `create-user`, `reset-password` | `401 forbidden` for anon; a made-up function name returns `404` | Both are deployed and admin-only. |
| Sign-up settings | Sign-up enabled, email auto-confirm **on** | Testers can register and sign in at once. No confirmation email is needed, so the built-in email rate limit cannot block testing. |

Not checkable without an account: signed-in reads and writes, the seeded reference data, storage policies, and whether migrations `0011` and `0012` are applied (`0012` is not yet).

**Owner checklist before the first tester release (Phase 0 and 4)**

1. Migrations `0001` to `0012` applied (`npx supabase db push`). Run the pre-check query at the top of `0012` first.
2. Reference data present: membership packages, amenities, and gym settings (payment QR and account name, so the payment screen has something to show).
3. Edge Functions deployed from the current `supabase/functions` code (`npx supabase functions deploy create-user reset-password`).
4. Owner account created (`supabase/scripts/create-owner.mjs`) and **one dedicated smoke-test member account** created. Its credentials become the optional secrets `SUPABASE_SMOKE_EMAIL` and `SUPABASE_SMOKE_PASSWORD`.
5. Row-level-security tests green (`database` job in `tests.yml`).
6. Project plan: **Supabase free projects pause after about 7 days of no activity**, and the first tester request after that fails. Either use a paid plan or enable the optional keep-alive workflow (Phase 5).

**Automatic smoke check: `scripts/check-supabase.mjs`**

Run by both platform workflows (not on pull requests) right after `supabase.json` is written and before anything is built. It uses Node, which is on both runners, reads `SUPABASE_URL` and `SUPABASE_ANON_KEY` from the environment, and prints no secrets.

| Level | Check | Fails the job when |
| --- | --- | --- |
| 1 (always) | `GET /auth/v1/health` returns 200 | URL wrong or project paused |
| 1 | `GET /rest/v1/membership_packages?limit=1` returns `42501` | Key rejected (`Invalid API key`), tables missing, or anon unexpectedly allowed |
| 1 | `POST /functions/v1/create-user` returns `401` (not `404`) | Edge Function not deployed |
| 2 (only if `SUPABASE_SMOKE_EMAIL` is set) | Password sign-in, then `GET /rest/v1/membership_packages?limit=1` with the user token returns `200` | Auth, RLS or reference data broken |

A failed check stops the workflow, so an APK or MSIX with a mistyped URL or key can never reach the Releases page.

**On the devices.** Android declares `INTERNET`, and the project URL is HTTPS, so no cleartext exception is needed. A packaged Windows desktop app can make outbound HTTPS calls without an extra capability. Sign-in persistence (`SecureSessionStore`) must be checked in the **packaged** app (MSIX) and the MSI-installed app, because they store data in different places than a Visual Studio unpackaged run: sign in, close, reopen, still signed in.

## 5. Workflow specifications

### 5.1 `tests.yml`

| Job | Steps | Notes |
| --- | --- | --- |
| `unit` | checkout, setup-dotnet (`global.json`), cache NuGet, `dotnet test tests/GymMembership.Tests -c Release`, upload `.trx` | Scoped to the test project, so the MAUI app is never built and no workload is needed. About 143 tests, seconds to run. |
| `database` | checkout, setup-node 22 (npm cache), `npm ci`, `npm test` in `supabase/tests/local` | Embedded Postgres, no Docker. 57 checks. |

Permissions: `contents: read`. Timeout 15 minutes. Pull request runs cancel superseded runs.

### 5.2 `android.yml`

Runner `ubuntu-24.04`, timeout 30 minutes. Job permissions: `contents: write` (needed only to attach the APK to a Release on tag runs; forks get a read-only token regardless).

| # | Step | Detail |
| --- | --- | --- |
| 1 | Checkout | `actions/checkout@v7` |
| 2 | .NET SDK | `actions/setup-dotnet@v6`, `global-json-file: global.json` |
| 3 | JDK | `actions/setup-java@v6`, `distribution: microsoft`, `java-version: 21` (MAUI 10 builds with JDK 21; the runner default is 17) |
| 4 | NuGet cache | `actions/cache@v6` |
| 5 | Workload | `dotnet workload install maui-android --version $WORKLOAD_VERSION` |
| 6 | Version | The tag is the version: `v0.1.0a` gives `0.1.0a` (Android `versionName`). A tag that is not `vMAJOR.MINOR.PATCH` with an optional letter or `-suffix` fails the job. A trailing letter or `-suffix` marks a pre-release. Other runs use `0.0.0-ci.<run number>`. Written to `$GITHUB_ENV`. |
| 7 | Backend settings | Non-PR only, only if both secrets exist: write `supabase.json` with `jq`, then run `node scripts/check-supabase.mjs` (section 4.7). A failed check stops the job before anything is built. |
| 8 | Keystore | If `ANDROID_KEYSTORE_BASE64` exists, decode to `$RUNNER_TEMP`. |
| 9a | Build (PR) | No keystore: `dotnet build -c Release -p:PlatformOnly=android` to prove it compiles. |
| 9b | Publish (signed) | With keystore: `dotnet publish ... -p:AndroidKeyStore=true` and the signing properties (see `.github/workflows/android.yml`). Passwords go through `env:` so they never reach the log. |
| 10 | Name and hash | Rename to `GymMembership-<version>-android.apk`, write `.sha256`. |
| 11 | Artifact | `actions/upload-artifact@v7`, 14 days. |
| 12 | Release | Tag runs only: create the Release if missing, upload the APK and `.sha256` with `--clobber` (section 7). |
| 13 | Cleanup | `if: always()`: delete the decoded keystore. |

### 5.3 `windows.yml`

Runner `windows-2025`, shell `pwsh`, timeout 40 minutes. Same permissions model as Android.

| # | Step | Detail |
| --- | --- | --- |
| 1-4 | Checkout, .NET SDK, NuGet cache | as Android |
| 5 | Workload | `dotnet workload install maui-windows --version $env:WORKLOAD_VERSION` (no Android pack is downloaded, thanks to `PlatformOnly`) |
| 6 | Version | Same tag rules as Android. The MSIX manifest needs numbers, so `v0.1.0a` gives MSIX version `0.1.0.<run number>`; the letter stays in the release title and file names (`GymMembership-0.1.0a-win-x64.msix`). |
| 7 | Backend settings | as Android, including the smoke check |
| 8 | Certificate | If `WINDOWS_PFX_BASE64` exists: write the `.pfx`, `Import-PfxCertificate` into `Cert:\CurrentUser\My`, keep the thumbprint. (The build itself writes the public `.cer` next to the MSIX.) |
| 9a | Build (PR / no certificate) | `dotnet publish ... -p:WindowsPackageType=MSIX -p:AppxPackageSigningEnabled=false` to prove packaging works (unsigned, not uploaded as a release asset). |
| 9b | Publish (signed) | `dotnet publish -f net10.0-windows10.0.19041.0 -c Release -p:PlatformOnly=windows -p:RuntimeIdentifierOverride=win-x64 -p:WindowsPackageType=MSIX -p:WindowsAppSDKSelfContained=true -p:AppxPackageSigningEnabled=true -p:PackageCertificateThumbprint=<thumbprint>` |
| 10 | Collect | Take the folder `src/GymMembership.App/AppPackages/GymMembership.App_<version>_Test/` (`.msix`, `.cer`, `Install.ps1`, `Add-AppDevPackage.ps1`, resources). Zip it as `GymMembership-<version>-win-x64-sideload.zip`; also copy the `.msix` and `.cer` out under clean names; write `.sha256` files. |
| 11 | Artifact | upload, 14 days |
| 12 | Release | Tag runs only, same idempotent logic |
| 13 | Cleanup | `if: always()`: remove the `.pfx` and the imported certificate |

The solution is **never** published: `dotnet publish` on the whole solution tries to publish every project (a documented MAUI warning). Both platform workflows publish the App project only.

### 5.4 Failure isolation (the "does not mess up builds" rules)

1. Separate files, separate runners; no `needs:` and no `workflow_call` between Android and Windows.
2. Each workflow has its own `concurrency` group (`android-<ref>`, `windows-<ref>`); cancelling one run never touches the other.
3. Each job installs only its own workload and builds only its own target framework (`PlatformOnly`).
4. Each platform has its own secrets (`ANDROID_*`, `WINDOWS_*`); a missing Windows certificate cannot fail the Android job.
5. Tag runs never cancel in-progress runs (`cancel-in-progress` is false for tags).
6. The Release object is created by whichever platform gets there first; the other simply finds it. A failed platform leaves the Release with the other platform's assets, clearly missing the failed one, and the failed workflow can be re-run on its own.
7. The MSI job is `continue-on-error` and has no `needs`, so it can fail without touching the MSIX, the APK or the Release.

## 6. Versioning

**The git tag is the version.** The first release is **`0.1.0a`**, published by pushing the tag `v0.1.0a`. Accepted tag shapes: `v0.1.0a`, `v0.1.0b`, `v1.2.3`, `v1.2.3-rc.1` (anything else fails the build with a clear message, so a typo cannot produce a release).

| Value | Source | Android | Windows (MSIX / MSI) |
| --- | --- | --- | --- |
| Release name, file names | tag `v0.1.0a` | `GymMembership-0.1.0a-android.apk` | `GymMembership-0.1.0a-win-x64.msix`, `.msi`, `-sideload.zip` |
| Display version | tag | `ApplicationDisplayVersion=0.1.0a` (versionName, free text) | `ApplicationDisplayVersion=0.1.0`: numbers only, the letter is dropped (a `-rc.1` suffix is dropped too) |
| Build number | `github.run_number` | `ApplicationVersion` (versionCode), must only increase | `ApplicationVersion`, becomes the 4th part: `0.1.0.15` (verified, section 10) |
| Non-tag builds | `main` / manual | `0.0.0-ci.<n>` | `0.0.0.<n>` |
| Day-to-day builds | csproj | `ApplicationDisplayVersion` is `0.1.0` (numeric on purpose) | same |

A trailing letter (`0.1.0a`, `0.1.0b`) or a `-suffix` marks the release as a **pre-release** on the Releases page, which is right for an alpha. It is still downloadable by everyone; the Releases page just labels it "Pre-release". Untick that box on the release page, or drop the letter from the tag, when the app is ready for a plain release.

## 7. Releases page

The Releases page is where testers and users get the apps. A tag `v*` is the only thing that publishes.

**Assets per release**

| File | Purpose |
| --- | --- |
| `GymMembership-<version>-android.apk` | Android install (sideload); one universal APK (arm64 and x86_64) |
| `GymMembership-<version>-win-x64-sideload.zip` | Windows: the MSIX plus certificate plus Microsoft's `Install.ps1` (recommended) |
| `GymMembership-<version>-win-x64.msix` | Windows installer on its own (manual install) |
| `GymMembership-signing.cer` | Public certificate Windows must trust once (manual install) |
| `GymMembership-<version>-win-x64.msi` | Windows MSI installer (per-machine; for PCs that cannot trust the MSIX certificate, or scripted rollouts) |
| `<asset>.sha256` | Checksum for each APK, MSIX, MSI and zip |

**How a release is created**

1. Maintainer pushes a tag: `git tag v0.1.0a && git push origin v0.1.0a` (or runs the workflow manually on a tag).
2. Android and Windows workflows start independently.
3. Whichever finishes its build first runs, using the preinstalled GitHub CLI and the job's `GITHUB_TOKEN`:
   ```
   gh release view "$TAG" || gh release create "$TAG" --title "Gym Membership $VERSION" --generate-notes [--prerelease] \
     || gh release view "$TAG"        # lost a creation race to the other workflow: that is fine
   gh release upload "$TAG" <files> --clobber
   ```
4. The second workflow finds the release and just uploads its files. `--clobber` makes a re-run replace its own assets.

**Release notes:** `--generate-notes` uses `.github/release.yml` to group merged pull requests (Features, Fixes, Database, Other). The release body is topped with the install instructions below via `--notes-file` on the first create.

**Install instructions to publish on every release**

*Android:* download the `.apk` on the phone. Allow "Install unknown apps" for the browser or Files app when prompted. Open the file and tap Install. If Play Protect warns about an unknown developer, choose "Install anyway". To update, install the newer APK over the old one (same signing key).

*Windows:* download `GymMembership-<version>-win-x64-sideload.zip`, unzip it, right-click `Install.ps1`, choose "Run with PowerShell" and accept the administrator prompt. It trusts the certificate and installs the app. Manual alternative: open the `.cer`, Install Certificate, Local Machine, Trusted People; then double-click the `.msix`. Uninstall from Settings, Apps. *Windows without the certificate step:* run the `.msi` instead (SmartScreen: More info, Run anyway). Install the MSIX **or** the MSI, not both.

**Safety nets:** a Release can be edited or deleted at any time without touching the code; a bad tag is fixed by deleting the Release and the tag, then tagging again. Pre-releases do not become "Latest".

An optional rolling pre-release tagged `nightly` (rebuilt from every push to `main`) can give testers the newest build without a version tag. It is not in the first rollout (section 11).

## 8. Repository changes

| File | Change |
| --- | --- |
| `global.json` | New: SDK pin (4.3). |
| `src/GymMembership.App/GymMembership.App.csproj` | Add the `PlatformOnly` lines (4.1), the Release-only Android `RuntimeIdentifiers` (4.5), and change `ApplicationId` to `com.slimshape.gymmembership`. |
| `src/GymMembership.App/Platforms/Windows/Package.appxmanifest` | `Publisher="CN=SlimShape"`, `PublisherDisplayName="Slim &amp; Shape"`. Must match the certificate subject. |
| `.github/workflows/tests.yml`, `android.yml`, `windows.yml` | New. |
| `scripts/check-supabase.mjs` | New: the Supabase smoke check (section 4.7). Plain Node, no dependencies. |
| `.github/release.yml` | New: release-note categories. |
| `.github/dependabot.yml` | New: weekly updates for `github-actions` and `nuget`. |
| `.gitignore` | Add `AppPackages/` (MSIX output lands in the project folder), `*.pfx`, `*.keystore`, `*.jks`. |
| `.gitattributes` | New: `* text=auto` so Windows checkouts stop reporting line-ending churn. |
| `README.md` | New "Download and install" section linking to Releases; "Build in CI" pointer to this spec. |

## 9. Secrets, variables and security

Create these under Settings, Secrets and variables, Actions. Use an **Environment** named `release` (with required reviewer = the maintainer) for the signing secrets so a tag build waits for a click before it can use them.

| Secret | Used by | What it is |
| --- | --- | --- |
| `ANDROID_KEYSTORE_BASE64` | android | Release keystore, base64 |
| `ANDROID_KEYSTORE_PASSWORD` | android | Store and key password |
| `ANDROID_KEY_ALIAS` | android | Alias in the keystore |
| `WINDOWS_PFX_BASE64` | windows | Signing certificate with private key, base64 |
| `WINDOWS_PFX_PASSWORD` | windows | Password of the `.pfx` |
| `SUPABASE_URL` | both | Project URL |
| `SUPABASE_ANON_KEY` | both | Public anon key (never the service-role key) |
| `SUPABASE_SMOKE_EMAIL`, `SUPABASE_SMOKE_PASSWORD` | both (optional) | Dedicated test member for the level-2 smoke check |

One-time creation (run on the maintainer's PC, never in CI):

```powershell
# Android keystore (JDK keytool). Back it up offline: losing it means users cannot update.
keytool -genkeypair -v -keystore release.keystore -alias gym -keyalg RSA -keysize 2048 -validity 10000
[Convert]::ToBase64String([IO.File]::ReadAllBytes("release.keystore")) | Set-Clipboard

# Windows certificate: Subject MUST equal the manifest Publisher (CN=SlimShape)
$cert = New-SelfSignedCertificate -Type Custom -Subject "CN=SlimShape" -KeyUsage DigitalSignature `
  -FriendlyName "Gym Membership signing" -CertStoreLocation "Cert:\CurrentUser\My" -NotAfter (Get-Date).AddYears(5) `
  -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.3", "2.5.29.19={text}")
Export-PfxCertificate -Cert $cert -FilePath gym-signing.pfx -Password (Read-Host -AsSecureString "pfx password")
[Convert]::ToBase64String([IO.File]::ReadAllBytes("gym-signing.pfx")) | Set-Clipboard
```

**Rules**

- Workflows use `on: pull_request`, **never** `pull_request_target`, so code from a fork can never run with secrets.
- Top-level `permissions: contents: read`; only the upload job raises it to `contents: write`.
- Decoded keystore and `.pfx` live in `$RUNNER_TEMP` and are deleted in an `always()` step. Passwords are passed by environment variable (`env:` prefix for Android, step `env` for PowerShell), never as plain command-line text.
- Branch protection on `main`: require `unit`, `database`, Android `build` and Windows `build`; require a pull request; no force-push. **Do not use path filters** on these workflows, because a required check that is skipped by a path filter stays "pending" and blocks the merge.
- Certificate rotation: the self-signed certificate is valid 5 years. Before it expires, issue a new one **with the same Subject** and re-publish the `.cer`; same-Publisher packages still update.
- The public `.cer` is safe to publish. The `.pfx` and keystore are not, and are never committed (`.gitignore` additions are listed in section 8).

## 10. Verification performed while writing this spec

Commands were run on a Windows 11 developer machine with SDK 10.0.401 against a scratch clone (the real repository was not modified).

| Check | Result |
| --- | --- |
| `dotnet publish -p:TargetFrameworks=net10.0-android` | **Failed** (NETSDK1005): the global property reaches the Core project reference and Core stops being a `net10.0` library. This is why section 4.1 uses a custom property. |
| `dotnet publish -p:PlatformOnly=android` (Release, arm64, `AndroidPackageFormats=apk`, signed from a throwaway keystore, password through `env:`) | **Passed**, 90 seconds on a warm machine. `com.companyname.gymmembership.app-Signed.apk` is **20.1 MB**. An unsigned `.apk` (19.9 MB) is written next to it, so CI must pick the `*-Signed.apk` file. |
| `dotnet publish -p:PlatformOnly=windows -p:WindowsPackageType=MSIX -p:WindowsAppSDKSelfContained=true -p:AppxPackageSigningEnabled=true -p:PackageCertificateThumbprint=...` (self-signed certificate, subject equal to the manifest Publisher) | **Passed**, 149 seconds. Produced a **66.5 MB** `.msix`. |
| MSIX contents | Signed by the test certificate (`CN=GymMembership CI`; Windows reports the signature as untrusted until the certificate is trusted, as expected). `Identity Name` equals the csproj `ApplicationId`. **No framework package dependency**, so the self-contained flag works. |
| MSIX version | `Version="0.0.7.7"` from `ApplicationDisplayVersion=0.0.7` and `ApplicationVersion=7`: the display version fills the first three parts and the build number becomes the fourth. A tag `v1.2.3` on run 15 gives `1.2.3.15`. |
| Output location | **Not** under `bin\Release`. The package lands in `src/GymMembership.App/AppPackages/GymMembership.App_<version>_Test/`, which holds the `.msix`, the public `.cer`, and Microsoft's generated sideload helpers `Install.ps1`, `Add-AppDevPackage.ps1` and `Add-AppDevPackage.resources`. `AppPackages/` must be git-ignored. |
| Real repository | Untouched by these runs. The temporary certificate was created in the current user's personal store for the test and removed afterwards. |

**Phase 1 re-check** (2026-10-14, on the Phase 1 branch with the real changes applied):

| Check | Result |
| --- | --- |
| Default developer builds (`dotnet build -f net10.0-windows...` and `-f net10.0-android`, no `PlatformOnly`) | **Passed** (23 s and 42 s). Visual Studio and local builds behave as before. |
| `-p:RuntimeIdentifiers=android-arm64;android-x64` on the command line | **Failed**: `MSB1006` with a plain `;`, and `NETSDK1083` with `%3B`. The ABI list therefore lives in the csproj (Release only). |
| `dotnet publish -p:PlatformOnly=android` with the csproj ABI list | **Passed**, 176 s. `com.slimshape.gymmembership-Signed.apk` is **32.8 MB** and contains `arm64-v8a` and `x86_64`. |
| `dotnet publish -p:PlatformOnly=windows -p:WindowsPackageType=MSIX` signed with a throwaway `CN=SlimShape` certificate | **Passed**, 52 s, **66.5 MB**. `Identity Name="com.slimshape.gymmembership" Publisher="CN=SlimShape" Version="0.0.12.12"`, `PublisherDisplayName` "Slim & Shape", signer `CN=SlimShape`. |
| `scripts/check-supabase.mjs` against the real project | **Passed** (3 checks, the signed-in check is skipped without a smoke account). It also **fails correctly** (exit 1) with a corrupted key and with a non-existent project URL, and exits 2 when the variables are missing. |

**Phase 3 re-check** (2026-10-14):

| Check | Result |
| --- | --- |
| Unsigned MSIX build (`AppxPackageSigningEnabled=false`), the pull-request path | **Passed**; the package is unsigned (`NotSigned`) as intended. |
| Unpackaged self-contained publish (`WindowsPackageType=None`, `SelfContained=true`, `WindowsAppSDKSelfContained=true`) | **Passed**, 53 s: 930 files, 381 MB, `GymMembership.App.exe`. The app **starts and keeps running** (checked after 12 s) with no runtime installed separately. |
| WiX 5.0.2 `wix build installer/windows/Product.wxs` over that folder | **Passed**, 81 s; **`.msi` is 120 MB**. WiX's `Files` element harvests the folder without a `heat` step. |
| Administrative extract of the MSI (`msiexec /a`, installs nothing) | **Passed**: 931 files, `GymMembership.App.exe` present, install folder `Program Files\Gym Membership`. |

Not verified locally: a real per-machine install and upgrade of the MSI (Phase 4 acceptance on a clean PC), and the workflows themselves, which only run on GitHub.

Supabase probe results are in section 4.7. Not verified here: signed-in access to the database (needs an account), installing the MSIX on a clean PC, installing the APK on a phone, and anything that only exists on GitHub-hosted runners (workload install time, image contents). Those are the acceptance checks in Phases 2 to 4.

## 11. Rollout plan

| Phase | Work | Done when |
| --- | --- | --- |
| 0. Decisions and secrets | Decisions are made (section 12). Create keystore and certificate (`CN=SlimShape`), add the secrets, back both up offline. Work through the Supabase checklist (section 4.7). | Secrets exist; backups stored outside the repository. |
| 1. Repository prep (done on branch `ci-cd-phase1`) | `global.json`, `PlatformOnly` lines, manifest Publisher, `.gitattributes`, `.gitignore` additions, `release.yml`, `dependabot.yml`. | Local `dotnet build` of the App and `dotnet test` unchanged and green; both `-p:PlatformOnly=` publishes work locally. |
| 2. Tests + Android (workflow files in the repository) | `tests.yml` and `android.yml`. | A pull request shows `unit`, `database`, Android `build` green; the `main` push uploads a signed APK artifact that installs on a phone (needs the Phase 0 secrets). |
| 3a. Windows MSIX (workflow file in the repository) | `windows.yml` with the `build` job. | A pull request shows Windows `build` green; the `main` push uploads a signed MSIX that installs through the sideload zip's `Install.ps1` on a clean Windows PC, signs in, and still is signed in after closing and reopening the app. |
| 3b. Windows MSI (in the repository) | `installer/windows/Product.wxs` and the `msi` job. | The `.msi` installs and uninstalls on a clean PC, and a second, higher version upgrades it in place. The MSIX result is unaffected by the MSI job failing. |
| 4. Release dry run | Tag `v0.1.0a` (the first real alpha; delete the release and tag if the dry run finds a problem and tag again). | A pre-release appears with both installers, hashes, certificate and notes; testers install both, sign in and load the package list against the real Supabase project; then delete the dry-run release and tag. |
| 5. Optional | `nightly` pre-release; a weekly `keepalive.yml` that calls the Supabase API so a free project does not pause; Azure Trusted Signing or a purchased certificate; attestation of artifacts. | Only if asked for. |

Branch protection is switched on at the end of Phase 3a, once the check names exist.

## 12. Risks and open questions

**Decisions made on 2026-10-14**

| Question | Decision |
| --- | --- |
| Final `ApplicationId` and publisher | Company **Slim & Shape**. Application id `com.slimshape.gymmembership`; MSIX publisher `CN=SlimShape`, display name "Slim & Shape". |
| Signing | Self-signed certificate for Windows (standard for test distribution), release keystore for Android. |
| Windows installer | Ship **both MSIX and MSI** (section 4.4). MSIX first; MSI follows in Phase 3b. |
| First release version | **`0.1.0a`**, published by the tag `v0.1.0a`; a pre-release on the Releases page (section 6). |
| Android ABIs | One universal APK containing **arm64-v8a and x86_64** so phones and Android Studio emulators both work. |

**Still open**

1. **Nightly pre-release** from `main`: yes or no? (Not in the first rollout.)

**Risks**

| Risk | Mitigation |
| --- | --- |
| MAUI workload install flakes or a manifest is missing from NuGet. | Pin the workload set; add `--ignore-failed-sources`; re-run is safe. |
| Runner image drift breaks the Windows build. | Pinned label `windows-2025`; read the runner-image release notes when bumping. |
| Self-signed MSIX is blocked until the certificate is trusted. | Shipped `.cer` plus Microsoft's `Install.ps1`, written instructions on every release, and the MSI as an alternative that needs no certificate. A trusted certificate later removes the step. |
| A PC ends up with both the MSIX and the MSI installed. | Release notes say to install one only; the MSI uses a different install folder and the MSIX can be removed from Settings, Apps. |
| The WiX `.wxs` is the one hand-written piece of the Windows build. | It lives in its own job (`continue-on-error`) and is exercised by the Phase 3b upgrade test. |
| Android keystore or Windows `.pfx` is lost. | Offline backup before Phase 2. Without the keystore, Android users must uninstall to update. |
| A release is built with a mistyped Supabase URL or key, or against a paused project. | The smoke check in section 4.7 fails the job before anything is published. |
| Free-tier Supabase project pauses after inactivity. | Paid plan or the optional keep-alive workflow. |
| The public APK and MSIX expose the Supabase URL and anon key. | Normal for Supabase; keep row-level security tight (it has local tests); never put the service-role key in CI. |
| The first MSIX build fails on identity or version formatting. | Phase 3 acceptance covers it; the Identity name and 4-part version are checked against the verification notes in section 10. |
| APK/MSIX size. | The APK carries two ABIs (size measured in section 10); the Windows packages are self-contained by design, so they are larger. |

## Appendix A: where the pieces live

The workflow and installer files are the source of truth; they are not copied into this spec.

| Piece | File |
| --- | --- |
| Unit and database tests | `.github/workflows/tests.yml` |
| Android build and release upload | `.github/workflows/android.yml` |
| Windows MSIX build (`build` job) and MSI build (`msi` job) | `.github/workflows/windows.yml` |
| MSI definition (WiX v5, fixed UpgradeCode) | `installer/windows/Product.wxs` |
| Supabase smoke check | `scripts/check-supabase.mjs` |
| SDK pin | `global.json` |
| Release-note groups, dependency updates | `.github/release.yml`, `.github/dependabot.yml` |

### A.1 What the sideload zip contains

Nothing custom. MSBuild generates the folder as part of `dotnet publish` with `WindowsPackageType=MSIX` (verified, section 10):

```
GymMembership.App_<version>_Test/
  GymMembership.App_<version>_x64.msix     the installer
  GymMembership.App_<version>_x64.cer      public certificate
  Install.ps1                              runs Add-AppDevPackage.ps1
  Add-AppDevPackage.ps1                    trusts the certificate, installs the package
  Add-AppDevPackage.resources/             localized messages
```

## Sources

- Microsoft Learn: Publish an Android app using the command line (.NET MAUI 10); Publish a packaged app for Windows with the CLI; Publish an unpackaged app for Windows with the CLI; workload sets (`dotnet workload` docs); .NET 10 default workload configuration breaking change; .NET for Android dependencies (JDK 21).
- `actions/runner-images`: Ubuntu 24.04 and Windows Server 2025 image readmes; announcement that `windows-latest` moves to the Visual Studio 2026 image.
- `dotnet/maui` pull request 34046 (Linux uses `maui-android`) and issue 33987 (unpinned workload break).
- GitHub release metadata queried 2026-10-14 for the current major versions of the official actions.
