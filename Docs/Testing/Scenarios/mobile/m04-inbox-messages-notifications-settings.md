# Mobile scenario M04 — Inbox, messages, notifications, notices, settings, profile

## Purpose
The landlord reads what came in, replies to a tenant, adjusts what alerts they get, and checks their profile/team settings.

## Read the implementation first
- mobile/lib/features/messages/, notifications/, notices/, inbox (routes '/inbox', '/messages/:id', '/notifications'), settings/, team/, auth/ (sign out, biometrics)
- RentalCommand.Api/Controllers/ MessageController / NotificationController / Notice / Team / Auth

## Rough exploration areas
- Inbox tab: what's listed (messages, alerts, notices), unread markers, tap-through, mark read.
- Send a message "QA-20260827-m04 test reply" in a tenant conversation; confirm it appears and the conversation ordering updates.
- Notifications screen and bell badge; settings → My alerts, Team routing, Tenant notices — toggle one, back out, reopen, confirm it persisted (then toggle back).
- Settings: profile, portfolio/company info, team members, sign out and sign back in with "Fill dev login"; biometric prompt behaviour if offered (skip enrollment).
- Server switcher (debug builds) — just confirm it exists and does not break anything; do not change it.

## Edge cases worth trying
- Empty message, very long message, emoji; open a notification that points at a deleted/closed record; sign out while a form is open.

## What to verify visually
- Unread vs read clear; timestamps relative and sensible; settings labels in plain English.
