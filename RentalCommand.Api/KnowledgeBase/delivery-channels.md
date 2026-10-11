---
title: Notification Delivery Channels
category: Settings
slug: notification-delivery-channels
order: 2
summary: Understand in-app, mobile push, email, SMS, destinations, retries, and delivery evidence.
keywords: notification channel, in-app, portal, push, email, sms, destination, retry, delivery status
---

Rental Command treats a **recipient**, a **channel**, and a **destination** as different facts. A policy may allow email, but an email delivery exists only when the eligible person has an email address. The system never substitutes another person's address or silently changes channels.

## Internal team channels

- **In-app** appears in the signed-in team member's notification bell and inbox.
- **Mobile push** goes to a device currently registered to that Rental Command account.
- **Email** uses the email address on that account.
- **SMS** uses the phone number on that account when workspace texting is configured.

Team routing decides **who is responsible**. Each resolved recipient's **My alerts** preferences decide which of these channels to use.

## Tenant-notice channels

- **Tenant portal** requires effective tenant access for the eligible LeaseManagement party.
- **Mobile push** requires effective tenant access and a registered device.
- **Email** requires an email address on the tenant record.
- **SMS** requires a phone number on the tenant record and a configured SMS provider.

Tenant channels belong to each individual automation. They never inherit team preferences. The recipient preview shows only destinations that are valid for the effective relationship and saved policy.

## What the delivery states mean

- **Queued** — a durable outbox record exists and is waiting for its first attempt.
- **Accepted** — the provider accepted the request, but final delivery has not been confirmed.
- **Retrying** — a temporary failure is recorded and another attempt is scheduled.
- **Sent** — the provider confirmed delivery.
- **Permanently failed** — retries ended and the recorded failure needs review.

Every tenant notice retains the frozen rendered content and template provenance. Each destination also retains an idempotency key, channel, relationship role, attempt count, provider reference, timestamps, and last error. Retrying the same durable delivery cannot create a second logical delivery.

Portal and in-app records are application destinations, not proof that a person read the notice. Email and SMS provider acceptance is also not the same as confirmed delivery or legal service. Use the evidence shown and follow the requirements for your jurisdiction.
