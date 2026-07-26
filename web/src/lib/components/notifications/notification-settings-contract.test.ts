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
const journeySource = readFileSync(
  new URL("./NotificationSetupJourney.svelte", import.meta.url),
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

describe("notification settings contract", () => {
  it("opens accessible help", () => {
    assert.match(helpSource, /min-h-11/);
    assert.match(helpSource, /aria-haspopup="dialog"/);
    assert.match(helpSource, /showModal/);
    assert.match(helpSource, /aria-labelledby/);
    assert.match(helpSource, /aria-describedby/);
    assert.match(helpSource, /onclose=\{returnFocus\}/);
    assert.match(helpSource, /\/docs\/settings-and-notifications/);
  });

  it("provides one route-backed, navigation-safe three-step journey", () => {
    for (const route of [
      "/settings/notifications/my-alerts",
      "/settings/notifications/team-routing",
      "/settings/notifications/tenant-notices"
    ]) {
      assert.match(journeySource, new RegExp(route));
    }
    assert.match(journeySource, /How should Rental Command reach you/);
    assert.match(journeySource, /Who should handle each kind of work/);
    assert.match(journeySource, /Which tenant messages should Rental Command prepare/);
    assert.match(journeySource, /beforeNavigate/);
    assert.match(journeySource, /hasUnsavedChanges/);
    assert.match(journeySource, /aria-current/);
    assert.match(journeySource, /Saved summary/);
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
