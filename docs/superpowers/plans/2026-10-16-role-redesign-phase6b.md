# Phase 6b: coach and amenity photos Implementation Plan

> **For agentic workers:** Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking. No subagents or agent-initiated live data changes. The owner approved a commit and push to the current branch after confirming the feature works; no merge to main.

**Goal:** Add coach portraits and reminders, visual coach cards, and admin-managed amenity photos; membership packages remain text-only.

**Architecture:** Reuse ProfileService and its avatars bucket for coaches, LinkCache for display links, and gym-assets plus amenities.image_path for amenities. Add a shared CoachPhotoViewModel and prompt control to the coach profile and Sessions landing screen. Change only image_path when an admin uploads so stale catalog fields cannot overwrite other edits. Keep existing permissions and all hiring, messaging, follow and status actions.

**Tech Stack:** C# / .NET 10 MAUI, CommunityToolkit.Mvvm, xUnit, DemoGateway.

**Spec:** docs/superpowers/specs/2026-10-16-phase6b-coach-amenity-photos-design.md and the baseline docs/superpowers/specs/2026-10-15-role-redesign-design.md, narrowed by the owner in chat: coaches and amenities only, membership packages text-only. Owner approved implementation. Default: amenity uploads remain admin-only.

## Execution record

Tasks 1–3 code and automated verification complete. 430 xUnit checks and Windows/Android Debug builds passed again before committing. 110 local embedded-Postgres checks passed during implementation; the pre-commit rerun timed out during temporary PostgreSQL initialization after 120 seconds, before running any checks, so no fresh database pass is claimed. git diff --check passes. The first feature-test run failed on the missing CoachPhotoViewModel; review regressions for stale catalog photo clearing and same-second avatar filenames failed before their fixes and now pass. Existing obsolete media-picker and chat-test blocking-task analyzer warnings remain. No live migration, upload or data write was performed. The owner subsequently confirmed it works on Windows and requested Phase 6b be marked done and committed/pushed to `role-redesign-progress-2026-10-16`. Detailed phone, light/dark, fallback and individual upload/replacement checks were not separately recorded and remain final-pass checks. No merge or independent review was requested/performed; author self-review used ponytail-review. Complexity review: lean already; existing picker, link cache, shared photo VM/control and no new dependencies.

Implementation decisions: image_path is omitted from full model insert/update and written only through a filtered PATCH, preserving a newer image across stale catalog toggles. Coach and amenity filenames use GUIDs. The demo stores avatar and gym-asset bytes for display. Existing extension/size validation and deferred orphan-file cleanup remain unchanged in scope.

## Global Constraints
- No new dependency or live database operation. Existing migration 0013 supplies image_path; 0007 supplies gym-assets storage policies.
- Coach photo is a persistent reminder, never a prerequisite for hires or sessions.
- Reuse existing Barlow typography and theme tokens, Windows and Android layouts, semantic labels, wrapping actions on narrow screens.
- Validate JPG/PNG/WebP, nonempty, maximum 5 MB as ProfileService does. Preserve old image on a failed upload/save; cancellation changes nothing.
- Use unique storage names, persist storage paths not signed URLs. Old objects remain (cleanup deferred as in existing avatar flow).
- Package screens, models and uploads stay unchanged and text-only.

## Review Focus
1. Missing or failed photo links must not prevent reading coach or amenity cards.
2. Members/employees/coaches cannot upload gym assets; inactive users cannot write.
3. A photo-only update must not overwrite amenity live status, catalog fields or active flag.
4. A photo change must invalidate card equality on periodic refresh.
5. The coach reminder must not show for members on their shared Sessions screen.

### Task 1: Photo service and demo behavior
**Files:** Models.cs, Services.cs, DemoGateway.cs; tests/GymMembership.Tests/Phase6bPhotoTests.cs.
**Interfaces:** Amenity.ImagePath; AmenityCard.PhotoUrl and CoachCard.PhotoUrl plus Letters; IAdminService.SetAmenityPhotoAsync(int amenityId, string fileName, byte[] data) returns AppResult<string>.
- [x] Write tests for photo mapping, successful admin upload, invalid images, forbidden roles, failure preservation and image-only updates; watch failure before implementation.
- [x] Add model mapping; resolve card links through bounded LinkCache, treating failed links as null; compare photo path/link in amenity equality.
- [x] Implement validated, unique gym-assets upload and filtered image_path-only update. Mirror only this UpdateWhere case and gym-assets policies in DemoGateway; allow the demo to display uploaded gym-assets/avatars without changing existing chat behavior.
- [x] Run targeted and full xUnit suites; expect zero failures.

### Task 2: Coach reminder and upload commands
**Files:** new Core/ViewModels/CoachPhotoViewModel.cs; StaffViewModels.cs; Phase6bPhotoTests.cs.
**Interfaces:** CoachPhotoViewModel(ICoachService, IProfileService, IProofPicker), IsCoach, NeedsPhoto, Letters, PhotoUrl, PhotoAction, ChangePhotoCommand. Optional photo VM on CoachProfileViewModel and SessionsViewModel preserves existing callers. Optional IProofPicker on CatalogAdminViewModel preserves existing callers; UploadAmenityPhotoCommand accepts Amenity.
- [x] Tests: reminder for a coach with no path, absent for a member; successful upload hides reminder, stored path with broken link still counts as set, picker cancellation/failure preserves state, both screen roots load photo VM, catalog command uploads selected row.
- [x] Run RED then implement shared model, composition, guarded picker/upload commands; run GREEN and full suite.

### Task 3: MAUI views, verification and notes
**Files:** new App/Controls/CoachPhotoPrompt.xaml and .xaml.cs; CoachProfilePage.xaml, SessionsPage.xaml, CoachesPage.xaml; AmenityCardView.xaml/.xaml.cs; CatalogAdminPage.xaml/.xaml.cs.
- [x] Add shared accessible prompt: portrait/initials, plain reminder, Add photo/Change photo, busy state and error/notice; show only missing coaches on Sessions, all coaches on profile.
- [x] Add coach portraits and wrapping card actions; rectangular amenity photo with monogram fallback; admin upload/replace button in wrapping row. Preserve existing actions; packages untouched.
- [x] Run dotnet test tests/GymMembership.Tests/GymMembership.Tests.csproj; expect zero failures. Run local database suite unchanged when available.
- [x] Build App for net10.0-windows10.0.19041.0 and net10.0-android, with commands checked against project/CI.
- [x] Self-review using ponytail-review (no subagents per session instructions), git diff --check; record actual evidence and any unavailable visual/live checks in docs/progress-2026-10-16.md. Record the owner's general Windows confirmation without inventing detailed device coverage.
