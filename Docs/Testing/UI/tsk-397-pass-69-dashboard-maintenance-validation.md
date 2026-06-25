# TSK-397 Pass 69 Dashboard Maintenance Validation

## Scope

Verify the tenant dashboard quick maintenance form gives clear validation feedback and still submits a real request.

## Setup

- Local web: `https://localhost:6042`
- Tenant user: `blake.hayes.portal.pass55@example.local`
- Route: `/portal`

## Steps

1. Sign in as the tenant and open `/portal`.
2. Click `Submit Request` with title and description blank.
3. Confirm the title field shows `Issue title is required.` and the description field shows `Describe the issue before submitting.`
4. Fill title `TSK-397 Pass 69 dashboard quick maintenance validation proof`.
5. Fill description `Kitchen cabinet door hinge is loose. Submitted from the tenant dashboard quick form after validation proof.`
6. Click `Submit Request`.
7. Confirm the dashboard maintenance count increments and the new request appears first as `New · Normal`.

## Failure Rules

- Empty submit with no visible feedback fails this guide.
- A valid filled request that does not appear on the dashboard after submission fails this guide.
