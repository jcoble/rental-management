import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { test } from 'node:test';

const dashboard = readFileSync(new URL('./+page.svelte', import.meta.url), 'utf8');
const shell = readFileSync(
	new URL('../../lib/components/AppShell.svelte', import.meta.url),
	'utf8'
);

test('setup stays reachable after the dashboard stopped showing the checklist to set-up accounts', () => {
	// The dashboard still shows the checklist while there is not a single rental on the books.
	assert.match(dashboard, /<GettingStartedCard \/>/);
	assert.match(dashboard, /data\.occupancy\.totalUnits === 0/);
	// And Guided Setup is a permanent pinned entry in the sidebar for everyone else.
	assert.match(shell, /href: '\/onboarding', label: 'Guided Setup'/);
});
