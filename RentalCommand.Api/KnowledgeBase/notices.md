---
title: Tenant Notices (Reminders, Renewals, and Lease End)
category: Tenants & Leases
slug: notices
order: 4
summary: Understand independent tenant-notice automations, recipients, templates, review, and delivery evidence.
keywords: notices, renewal offer, rent reminder, late rent, non-renewal, tenant notice, template, recipient, delivery evidence
---

Tenant notices are driven by independent saved policies. They do not use the staff alert matrix and there is no master tenant-delivery switch. Turning off a rent reminder does not turn off a renewal offer, and changing a staff member's email alerts never changes tenant delivery.

## The five supplied automations

Every new workspace starts with complete editable copy for:

- rent reminders;
- lease renewal offers;
- month-to-month offers;
- lease expiration or non-renewal; and
- past-due rent or late-fee notices.

Each automation chooses its own mode: **Off**, **Create draft for review**, or **Send automatically where permitted**. It also owns its timing, tenant portal/mobile/email/SMS channels, recipient roles, immutable template version, and failure behavior. Lease-ending automations run only after the LeaseManagement relationship has an explicit ending disposition.

## Confirm who will receive it

Before enabling or sending an automation, use its recipient preview for the relevant LeaseManagement relationship. The preview reads the effective relationship parties on the current business date and shows:

- the primary tenant, co-tenant, guarantor, or occupant role;
- whether that person is eligible;
- the exact channels with valid destinations; and
- a plain-language reason for every exclusion.

Primary tenants and co-tenants are eligible by default. A guarantor receives a legal notice only when specifically designated as legally eligible. An occupant never receives financial or legal material solely because the person resides in the unit. Rental Command does not invent an email address, phone number, portal login, or mobile device when one is missing.

## Review and edit

1. Open **Tenant notices** and choose the automation.
2. Confirm its mode, timing, recipients, channels, and template version.
3. Open a generated draft and adjust the subject or message when review is required.

## Templates and legal review

Editing or restoring a template creates a new workspace version; it never rewrites the content used by an older draft or sent notice. Merge-field help explains every supported token and shows realistic sample data. When a newer supplied template exists, Rental Command reports the update without overwriting customized copy.

Courtesy, operational, and legal notices are classified separately. A legal template retains jurisdiction and review facts. Automatic legal delivery remains unavailable until the applicable jurisdiction and template version have been explicitly reviewed. This safeguard is not legal advice; the workspace remains responsible for the content and timing required in its jurisdiction.

## Approve and send

1. Confirm the recipients and allowed channels from the saved policy.
2. Click **Approve & send**.

Approval freezes the rendered subject, body, content hash, workspace/system template provenance, jurisdiction, approver, and recipient destinations. Every channel receives its own idempotent outbox delivery and evidence row. Delivery status distinguishes **Queued**, **Accepted**, **Retrying**, **Sent**, and **Permanently failed**, including attempts, next retry, provider reference, destination, and last error. You can dismiss a draft you do not want to send without changing the automation policy.
