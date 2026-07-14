---
title: Settings and Notifications
category: Settings
slug: settings-and-notifications
order: 1
summary: Understand personal alerts, team responsibility routing, and tenant notice delivery.
keywords: settings, notifications, alerts, team routing, tenant notices, templates, email, sms, mobile push, in-app, recipients, automation
---

Rental Command separates notifications by **who receives them**. This prevents a personal alert preference from accidentally changing a tenant notice or a coworker's delivery.

## My alerts

**My alerts** applies only to the signed-in account. Each team member chooses their own channels:

- **In-app** appears in the Rental Command notification bell and inbox.
- **Mobile push** goes to mobile devices registered to that account. It does not mean browser web push.
- **Email** goes to the email shown on the account.
- **SMS** goes to the phone number shown on the account when workspace texting is available.

Turning off one of these channels does not disable the event itself, change another team member's preferences, or change tenant delivery.

## Team routing

**Team routing** answers: “Which team member is responsible for this kind of update?” It is managed separately from channel preferences.

Routing topics include rent and money, applications and leasing, work orders, owner statements and decisions, account and security events, and the **Morning Briefing**. A saved rule shows:

- its scope, such as all properties or a selected property;
- every named recipient;
- the reason each person receives that topic; and
- whether Workspace Administrators are the fallback when nobody is named.

Named recipients must be active members of the current workspace. Their actual delivery channels come from **their own My alerts** choices. Tenant channels are never inherited from Team routing.

The Morning Briefing is the scheduled landlord/team rundown of rent, lease, appointment, inspection, and urgent work that needs attention. It remains workspace-wide because every recipient sees only records allowed by that person's active capabilities and property scope. Notification managers can pause or resume it, choose its local send hour, decide whether all-clear days should send, and name its responsible recipient. The recipient's own **My alerts** choices determine whether it arrives by email, SMS, or mobile push.

Only users with notification-management access can change Team routing. Property assignment and role scope still apply when the underlying record is loaded or acted on.

## Tenant notices

**Tenant notices** controls delivery to people in a lease-management relationship. Each automation is independent; there is no master “send tenant notices” switch.

The supplied automations are:

- rent reminder;
- lease renewal offer;
- month-to-month offer;
- lease expiration or non-renewal; and
- past-due rent or late-fee notice.

Every automation has one mode:

- **Off** — creates no draft and sends nothing.
- **Draft for review** — prepares a draft for an authorized team member.
- **Send automatically** — uses the saved policy only when its operational and legal requirements are satisfied.

Each policy separately chooses timing, tenant channels, eligible relationship roles, and what happens after a delivery failure. A primary tenant, co-tenant, guarantor, and occupant are not interchangeable. Legal notices never include an occupant merely because the person lives in the unit, and a guarantor must be explicitly eligible for legal delivery.

## Supplied templates and versions

Rental Command supplies complete starting copy for every tenant-notice automation. You edit that copy instead of beginning with an empty message.

Every new workspace receives these copies during setup. If an automation or supplied template is missing, the settings screen reports a workspace-setup problem instead of asking the user to create blank content.

Saving an edit creates a new immutable workspace template version. Older versions remain available as history, and an already prepared notice stays tied to the exact template version used to render it. **Restore current supplied default** also creates a new version; it does not erase prior customization.

Legal templates require a reviewed jurisdiction before automatic delivery is available. This is a workflow safeguard, not legal advice; the workspace administrator remains responsible for confirming the notice and timing for the applicable jurisdiction.

## Who can change what?

- Any signed-in staff or owner account can change **My alerts** for itself.
- A user with notification-management access can change **Team routing** and **Tenant notices**.
- Tenant portal notification preferences are managed in the tenant experience, not the staff settings area.
- Channel selection never bypasses record access, property scope, or role capabilities.

If a channel has no valid destination—for example, SMS is selected but the recipient has no phone number—the system does not invent one. Tenant notice failure behavior determines whether delivery stops for review, retries and keeps a draft, or retries and records failure.

## Delivery status

The delivery list distinguishes submission to a provider from confirmed delivery:

- **Queued** — the durable delivery worker has not made the first attempt yet.
- **Accepted** — the provider accepted the message, but final delivery has not been confirmed.
- **Retrying** — a temporary provider failure occurred and another attempt is scheduled.
- **Delivered** — the provider confirmed delivery.
- **Failed** — retries ended. Review the recorded error and the recipient's destination before trying again.

The list also shows the recipient relationship role, channel, destination, attempt count, next retry when applicable, and the provider's last error. It is delivery evidence, not a second set of notification preferences.
