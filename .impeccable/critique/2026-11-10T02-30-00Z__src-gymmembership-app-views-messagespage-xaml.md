---
target: "Phase 4: Messages (Announcements, Gym queues, request cards) and the staff Announcements page"
total_score: 31
max_score: 40
na_heuristics: 
p0_count: 0
p1_count: 0
target_identity: "file:C:\\Users\\bowie\\OneDrive\\Desktop\\projects\\gym-membership\\src\\GymMembership.App\\Views\\MessagesPage.xaml"
target_path: "C:\\Users\\bowie\\OneDrive\\Desktop\\projects\\gym-membership\\src\\GymMembership.App\\Views\\MessagesPage.xaml"
timestamp: 2026-11-10T02-30-00Z
slug: src-gymmembership-app-views-messagespage-xaml
---
Method: design review only (detector not applicable, native platform)

Scope: Plan 2026-11-09, Phase 4, built to the approved mockup docs/mockups/messaging.html. Member Messages on Windows at 1600 dark (coach chat with a request card, Announcements open, Gym tab, a new Admin conversation); staff inbox with "To Admin" and the Announcements page (Sub-admin, 1600 dark); member on the Pixel 7 in dark, light and font 1.3 (list, Announcements, Gym tab, Admin, coach chat); coach on the Pixel 7 in dark (Trainees and Gym tabs).

## Design Health Score

| # | Heuristic | Score | Key Issue |
|---|-----------|-------|-----------|
| 1 | Visibility of System Status | 3 | Unread counts on the pinned row and the tabs; the open queue's chip is yellow |
| 2 | Match System / Real World | 3 | "Write to Front desk / Admin / System admin", "Gym", "Trainees" in the people's own words |
| 3 | User Control and Freedom | 3 | A new Admin conversation only exists once something is sent; nothing to undo |
| 4 | Consistency and Standards | 3 | Request cards and the pinned row reuse the list's row and the panel's look; staff keep Open, Mine, Closed |
| 5 | Error Prevention | 3 | Announcements has no box to type in; posting asks first and says it can't be removed |
| 6 | Recognition Rather Than Recall | 3 | Each empty gym conversation says who reads it |
| 7 | Flexibility and Efficiency | 3 | Open Time requests from the card; Enter sends |
| 8 | Aesthetic and Minimalist Design | 3 | One pinned row and one chip row added; the Write to chips wrap on narrow lists |
| 9 | Error Recovery | 3 | Server refusals use the existing error line |
| 10 | Help and Documentation | 4 | The read-only line says where to write instead |
| **Total** | | **31/40** | **Good** |

## Design Specificity Verdict

In the incumbent yellow-and-black world, from the approved mockup. The product-specific moment is the request card: the step of a time request said by whoever took it, on their side of the chat, with the way to act on it.

## Priority Issues

- Fixed (P1, also on Cashier, Sessions, Attendance, My membership): a chip that had been chosen and then left turned into a blank black box on Android and lost its outline on Windows. The chips swapped their whole style in a trigger; they now set only the three colours, and the "on" style is gone.
- Fixed (P3): after a refresh the open Announcements row could lose its open look; the open item is now the pinned row itself.
- Open (P3): the Android hardware Back key does not return from a conversation to the list (the in-app arrow does). Not new in this phase.
- Open (P3): the three Write to chips wrap onto two lines in the 360 px list when the first is not chosen.

## Minor Observations

- The demo now carries the message for its seeded time request and one announcement, as the database would.
