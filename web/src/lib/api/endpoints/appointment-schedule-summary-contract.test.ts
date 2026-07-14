import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { describe, it } from 'node:test';

const endpointSource = readFileSync(new URL('./appointments.ts', import.meta.url), 'utf8');
const shellSource = readFileSync(new URL('../../components/AppShell.svelte', import.meta.url), 'utf8');

describe('appointment shell schedule summary contract', () => {
	it('uses the canonical scalar endpoint instead of loading and filtering a capped list', () => {
		assert.match(endpointSource, /scheduleSummary: \(\) =>[\s\S]*\/appointments\/schedule-summary/);
		assert.match(shellSource, /appointmentsApi\.scheduleSummary\(\)/);
		assert.match(shellSource, /data\?\.nextSevenDaysCount \?\? 0/);
		assert.doesNotMatch(shellSource, /appointmentsApi\.list\([^)]*take:\s*100/);
		assert.doesNotMatch(shellSource, /const horizon =|\.filter\(\(a\) =>/);
	});
});
