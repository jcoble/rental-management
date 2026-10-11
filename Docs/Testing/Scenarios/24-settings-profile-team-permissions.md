# Scenario 24 — Settings, profile, portfolio, users, roles, permissions, integrations, and billing boundaries

## Purpose

Validate the administrative configuration surfaces and role/capability envelope: profile/security, portfolio/accounting settings, team invites and assignment scopes, notification/integration settings, and any billing or provider surface that is actually exposed.

## Preconditions and login

Use the seeded administrator for read-only inspection, plus additive QA invitations/roles in local dev or preview. Do not connect a bank, enable external messaging, purchase billing, or alter production-wide defaults in the shared simulation.

## Read the implementation first

Before opening a browser, read these named source files and the relevant controller/service code they call:

- web/src/routes/(protected)/settings/+page.svelte, web/src/routes/(protected)/settings/accounting/+page.svelte, web/src/routes/(protected)/settings/security/+page.svelte
- web/src/routes/(protected)/settings/integrations/ai/+page.svelte
- web/src/routes/(protected)/settings/notifications/my-alerts/+page.svelte, web/src/routes/(protected)/settings/notifications/team-routing/+page.svelte, web/src/routes/(protected)/settings/notifications/tenant-notices/+page.svelte
- web/src/routes/(protected)/profile/+page.svelte
- web/src/routes/(admin)/admin/users/+page.svelte and web/src/routes/(admin)/admin/audit/+page.svelte
- web/src/routes/(protected)/plaid/auth/+page.svelte and web/src/routes/(protected)/banking/+page.svelte
- web/src/lib/auth/experience-policy.ts, web/src/lib/auth/role-route-gates.test.ts, and web/src/lib/components/AppShell.svelte
- RentalCommand.Api/Controllers/PortfolioController.cs, RentalCommand.Api/Controllers/AuthController.cs, RentalCommand.Api/Controllers/TeamController.cs, RentalCommand.Api/Controllers/WorkspaceInvitationsController.cs, RentalCommand.Api/Controllers/AccountingIntegrationsController.cs, RentalCommand.Api/Controllers/AiIntegrationsController.cs, RentalCommand.Api/Controllers/AutomationSettingsController.cs, RentalCommand.Api/Controllers/DevicesController.cs, RentalCommand.Api/Controllers/StripeWebhookController.cs, and RentalCommand.Api/Controllers/BankingController.cs

## Rough exploration areas

Use these as exploration goals, not a step-by-step script:

- Edit and reopen personal profile, password/security, timezone, locale, and notification preferences; verify account-level versus portfolio-level settings are labeled correctly.
- Inspect portfolio name/address/currency/accounting configuration, categories/integrations, and provider health; confirm save/reload persistence and safe unavailable states.
- Invite a QA team member, choose role/profile and property scope, edit assignment, deactivate/reactivate where supported, and compare the resulting navigation/capabilities.
- Open admin team/users and audit surfaces; verify self-protection, role descriptions, permission-denied handling, and that stale client capabilities cannot grant a forbidden action.
- Check billing/subscription or payment-provider links if they are exposed by the current route tree; otherwise record that no landlord billing page is exposed rather than inventing a route.

## Specific edge cases worth trying

- Required fields blank, invalid/long email/phone, Unicode, password mismatch/weakness, duplicate invite, expired/reused invite, role with no properties, and self-deactivation.
- Change settings with unsaved values, double submit, conflict/stale revision, offline/API failure, provider credential absent, and retry after a successful save.
- Direct forbidden settings/admin URLs for owner, leasing, technician, and tenant identities; cross-portfolio property scope; hidden action made visible by URL.
- 390px forms, long role/permission descriptions, collapsed sections, keyboard focus, and error text not relying on color.

## What to verify visually

- Settings hierarchy, scope labels, role cards, selected properties, save/loading/error/success states, and security warnings are clear.
- Navigation changes after role assignment are intentional and not stale; users can always find profile/sign-out/help.
- Sensitive values are masked, no credential appears in the DOM/toasts, and mobile forms do not clip action buttons.

## Data safety and evidence

Use QA-YYYYMMDD names/emails for invitations and additive role assignments. Do not connect external providers or alter existing shared team memberships.

Record actual-versus-expected behavior, URLs, response/status evidence, persistence after reload, and console errors. Report confirmed bugs separately from potential issues and observations using the BUG-N format from the exploratory tester definition.
