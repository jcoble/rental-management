import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { test } from 'node:test';

const source = readFileSync(new URL('./+page.svelte', import.meta.url), 'utf8');

test('dashboard hero keeps guided setup reachable after the checklist card is gone', () => {
	assert.match(source, /data-testid="dashboard-hero-guided-setup"/);
	assert.match(source, /href="\/onboarding\?from=dashboard"/);
	assert.match(source, />\s*Guided Setup\s*</);
});
