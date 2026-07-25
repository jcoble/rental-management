import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";

const source = readFileSync(
  new URL("./ai-integrations.ts", import.meta.url),
  "utf8"
);
const settingsSource = readFileSync(
  new URL("../../../routes/(protected)/settings/+page.svelte", import.meta.url),
  "utf8"
);

test("AI integrations endpoint contract exposes write-only actions", () => {
  for (const exportName of [
    "getAiIntegrationStatus",
    "testAiCredential",
    "activateAiCredential",
    "rotateAiCredential",
    "removeAiCredential",
  ]) {
    assert.match(source, new RegExp(`export const ${exportName}`));
  }
  assert.doesNotMatch(source, /apiKey\s*:\s*string\s*\|\s*null/);
  assert.match(source, /\/integrations\/ai\/test/);
  assert.match(source, /\/integrations\/ai\/rotate/);
});

test("Settings exposes AI provider only to integration managers", () => {
  assert.match(
    settingsSource,
    /const canManageAiProvider = \$derived\(hasCapability\('integrations\.manage'\)\)/
  );
  assert.match(
    settingsSource,
    /\{#if canManageAiProvider\}[\s\S]*AI provider[\s\S]*href="\/settings\/integrations\/ai"[\s\S]*\{\/if\}/
  );
});
