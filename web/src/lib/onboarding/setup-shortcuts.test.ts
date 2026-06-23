import assert from 'node:assert/strict';
import { test } from 'node:test';
import { ONBOARDING_SETUP_SHORTCUTS } from './wizard-steps.ts';

test('onboarding setup shortcuts lead with scan-first setup', () => {
	assert.deepEqual(
		ONBOARDING_SETUP_SHORTCUTS.map((shortcut) => shortcut.key),
		['scan-new-rental', 'spreadsheet-import'],
	);

	const scanShortcut = ONBOARDING_SETUP_SHORTCUTS[0];
	assert.equal(scanShortcut.href, '/scan/new-rental');
	assert.equal(scanShortcut.primary, true);
	assert.match(scanShortcut.description, /lease/i);
});
