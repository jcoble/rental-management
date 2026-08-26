import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { describe, it } from "node:test";

const helpSource = readFileSync(
  new URL("./NotificationHelpAction.svelte", import.meta.url),
  "utf8"
);
const previewSource = readFileSync(
  new URL("./NoticePreview.svelte", import.meta.url),
  "utf8"
);
const pageSource = readFileSync(
  new URL("../../../routes/(protected)/settings/notifications/+page.svelte", import.meta.url),
  "utf8"
);
const accordionSource = readFileSync(
  new URL("./NotificationPolicyAccordion.svelte", import.meta.url),
  "utf8"
);
const endpointSource = readFileSync(
  new URL("../../api/endpoints/notifications.ts", import.meta.url),
  "utf8"
);
const myAlertsSectionSource = readFileSync(
  new URL("./MyAlertsSection.svelte", import.meta.url),
  "utf8"
);
const teamRoutingSectionSource = readFileSync(
  new URL("./TeamRoutingSection.svelte", import.meta.url),
  "utf8"
);
const tenantNoticesSectionSource = readFileSync(
  new URL("./TenantNoticesSection.svelte", import.meta.url),
  "utf8"
);

describe("notification settings contract", () => {
  it("opens accessible help", () => {
    assert.match(helpSource, /min-h-11/);
    assert.match(helpSource, /aria-haspopup="dialog"/);
    assert.match(helpSource, /<Dialog\.Title/);
    assert.match(helpSource, /<Dialog\.Description/);
    assert.match(helpSource, /onOpenChange=\{/);
    assert.match(helpSource, /showCloseButton=\{false\}/);
    assert.match(helpSource, /data-testid="notification-help-dialog"/);
    assert.match(helpSource, /\/docs\/settings-and-notifications/);
  });

  it("puts every notification section on one navigation-safe page", () => {
    for (const section of ["MyAlertsSection", "TenantNoticesSection", "TeamRoutingSection"]) {
      assert.match(pageSource, new RegExp(section));
    }
    assert.match(pageSource, /Appears when you add a team member\./);
    assert.match(myAlertsSectionSource, /id="my-alerts"/);
    assert.match(tenantNoticesSectionSource, /id="tenant-notices"/);
    assert.match(teamRoutingSectionSource, /id="team-routing"/);
    for (const section of [
      myAlertsSectionSource,
      teamRoutingSectionSource,
      tenantNoticesSectionSource
    ]) {
      assert.match(section, /beforeNavigate/);
      assert.match(section, /hasUnsavedChanges/);
      assert.match(section, /Saved summary/);
    }
  });

  it("keeps the notifications page padded and its section content spaced", () => {
    assert.match(pageSource, /mx-auto box-border h-full w-full max-w-5xl space-y-6 overflow-y-auto p-4 pb-20 sm:p-6/);
    assert.match(myAlertsSectionSource, /<div class="space-y-5" data-testid="my-alerts-page">/);
  });

  it("uses an accessible single-panel disclosure pattern", () => {
    assert.match(accordionSource, /aria-expanded/);
    assert.match(accordionSource, /aria-controls/);
    assert.match(accordionSource, /role="region"/);
    assert.match(accordionSource, /aria-labelledby/);
  });

  it("renders realistic preview", () => {
    assert.match(previewSource, /Preview unsaved message/);
    assert.match(previewSource, /previewNotice\(\{ systemKey, subject, body \}\)/);
    assert.match(previewSource, /whitespace-pre-wrap/);
    assert.match(previewSource, /field\.example/);
  });

  it("gates test send", () => {
    assert.match(previewSource, /Controlled non-tenant test email/);
    assert.match(previewSource, /Accepted/);
    assert.match(previewSource, /Suppressed/);
    assert.match(previewSource, /ProviderError/);
    assert.match(endpointSource, /Idempotency-Key/);
    assert.match(endpointSource, /\/test-send/);
  });
});
