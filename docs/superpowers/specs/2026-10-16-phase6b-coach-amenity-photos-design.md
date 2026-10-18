# Phase 6b: coach and amenity photos

## Scope approved in chat

The owner asked to proceed with **coaches and amenities only**, keeping **membership packages text-only**. The preceding role-redesign spec remains the baseline. Amenity photo uploads stay admin-only, matching the existing catalog and gym-assets permissions; no permissions are widened.

## What is built

- Coach profile has the existing avatar (or initials), an Add photo / Change photo action, picker errors and upload feedback.
- Coaches with no stored avatar path see a persistent reminder on their Sessions landing page and their coach profile. The reminder never blocks hires or sessions and does not show for members on the shared Sessions page. A broken display link is not treated as a missing stored photo.
- Member-facing coach cards show portraits, names, specialty, bio, rate, shifts, and existing Hire / Message / End hire actions. Actions wrap on narrow layouts. Initials remain visible if a picture is absent or cannot load.
- Amenity cards show an image with initials underneath as fallback. Availability, reason, return time, follow and status editing are unchanged.
- Admin's Packages and amenities page has Add or replace photo on each saved amenity, a Photo set indicator, and upload feedback. No package image UI or package model changes were made.

## Implementation choices

Reuse ProfileService, IProofPicker, LinkCache, Barlow typography and existing theme tokens. CoachPhotoViewModel and CoachPhotoPrompt are shared by the two coach screens. Optional composition parameters preserve existing constructor callers; MAUI's existing view-model registration resolves the photo model in the app.

Coach portraits use avatars and the existing set_my_avatar function. Amenity images use gym-assets and amenities.image_path from migration 0013. Uploads validate JPG/PNG/WebP, nonempty, up to 5 MB using the existing validator. A unique GUID filename avoids replacement collisions. Only paths are persisted; signed links are cached, bounded to six requests at a time, and optional display failures do not fail the catalogue read.

An amenity upload updates only image_path through a filtered PATCH. Normal catalog writes omit image_path so an older toggle cannot clear a newer photo. Live amenity status fields remain read-only on catalog models. DemoGateway mirrors these column writes and the existing asset/own-avatar access rules and can display its uploaded images.

## Verification recorded in this session

- Added service, view-model, permission, fallback and HTTP request tests. Tests first failed on the missing feature; review regressions failed before their fixes.
- 430 xUnit checks passed, including the project's XAML binding checks.
- 110 existing embedded-Postgres checks passed during implementation; this suite runs locally, not against the live project. A pre-commit rerun timed out after 120 seconds during temporary PostgreSQL initialization, before any checks ran; it is not counted as another pass. The fresh pre-commit xUnit run and Windows/Android builds passed.
- Windows and Android Debug builds passed; the existing obsolete PickPhotoAsync warning remains. The existing MessengerPhotoTests blocking-task analyzer warning also remains.
- git diff --check passed. Author self-review and ponytail complexity review performed; no independent reviewer was used.
- No migration was needed or applied, and the agent did not upload files or change live data. The owner subsequently confirmed the feature works and requested a commit and push to `role-redesign-progress-2026-10-16`; no merge to main was requested.

## Completion and owner confirmation

**Phase 6b is done.** The owner confirmed it works in the Windows app and asked to mark the phase complete and push it to the current branch. Coaches and amenities are included; membership packages remain text-only. Login also worked after a rebuild; its earlier transient hang was not reproduced or diagnosed, so no login fix is claimed. Temporary sign-in diagnostic logging was removed before the phase commit.

The owner's confirmation was general, not a detailed test checklist. Narrow Android layouts, light/dark mode, missing-picture fallback and each upload/replacement path were not separately recorded as manually verified. These remain recommended final-pass checks rather than claims of completed device coverage. Phase 7 is next.

## Deliberate limits

No package photos, photo removal/cropping, camera capture, or new storage rules. As in the existing avatar flow, replaced files and an uploaded file whose subsequent database save fails remain in storage; cleanup is deferred. The existing validator checks file extension and size, not decoded image contents; storage MIME/size rules also apply. Admin can replace an image but the catalog indicator is not an inline preview; use the Amenities page to see it.
