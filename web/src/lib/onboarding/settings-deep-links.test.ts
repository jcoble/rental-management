/**
 * Guard for the getting-started / wizard → Settings deep-link contract (C-1, batch-1 holistic review).
 *
 * The Settings page is tabbed and only opens the tab whose key matches the URL hash (or a known legacy
 * section-anchor mapped to its tab). Earlier, the checklist + wizard sent legacy anchor IDs that the
 * tabbed page didn't honor, so "Show me" teleported the user to the wrong tab with the target hidden
 * and no spotlight. These assertions fail the build if any settings deep-link hash ever stops
 * resolving to a real tab — so the seam can't silently regress.
 *
 * Imports are limited to dependency-free modules (wizard-steps is icon-free; getting-started-tasks
 * pulls in Svelte/lucide and can't load under the bare node test runner). The getting-started task
 * hashes are asserted explicitly here, mirrored from getting-started-tasks.ts.
 *
 *   node --test --experimental-strip-types src/lib/onboarding/settings-deep-links.test.ts
 */
import { test } from 'node:test';
import assert from 'node:assert/strict';

import { SETTINGS_SECTIONS, WIZARD_STEPS } from './wizard-steps.ts';
import { resolveSettingsTab, isLegacySettingsAnchor } from './settings-anchor-map.ts';

/** Valid tab keys the Settings page activates directly from a hash. */
const TAB_KEYS = SETTINGS_SECTIONS.map((s) => s.key) as string[];

/**
 * The hashes the getting-started checklist tasks deep-link to (mirror of the `hash` fields in
 * getting-started-tasks.ts for tasks whose route is /settings). All must be tab keys so the right tab
 * opens; the matching coach key then spotlights the section. If you change a settings task's hash,
 * change it here too.
 */
const GETTING_STARTED_SETTINGS_HASHES = ['portfolio', 'notifications', 'automations', 'messaging'];

test('every getting-started settings deep-link hash resolves to a real tab', () => {
	for (const hash of GETTING_STARTED_SETTINGS_HASHES) {
		assert.equal(
			resolveSettingsTab(hash, TAB_KEYS),
			hash,
			`checklist hash "#${hash}" should open the "${hash}" tab directly`
		);
	}
});

test("every wizard step's settingsAnchor (the wizard→settings return) resolves to a real tab", () => {
	const anchored = WIZARD_STEPS.filter((s) => s.settingsAnchor);
	assert.ok(anchored.length > 0, 'expected some wizard steps to declare a settings anchor');
	for (const step of anchored) {
		const tab = resolveSettingsTab(step.settingsAnchor!, TAB_KEYS);
		assert.ok(
			tab !== null,
			`wizard step "${step.key}" settingsAnchor "#${step.settingsAnchor}" does not resolve to a Settings tab`
		);
	}
});

test('legacy section anchors map to a tab and are flagged as needing scroll-into-view', () => {
	// The three anchors still emitted by the wizard-return path / old bookmarks.
	for (const anchor of [
		'settings-portfolio-basics',
		'settings-notification-email',
		'settings-notification-delivery',
	]) {
		assert.ok(resolveSettingsTab(anchor, TAB_KEYS) !== null, `${anchor} must resolve to a tab`);
		assert.ok(isLegacySettingsAnchor(anchor), `${anchor} must be flagged as a legacy anchor`);
	}
});

test('an unknown hash resolves to null (no tab change, no crash)', () => {
	assert.equal(resolveSettingsTab('not-a-real-thing', TAB_KEYS), null);
	assert.equal(resolveSettingsTab('', TAB_KEYS), null);
});
