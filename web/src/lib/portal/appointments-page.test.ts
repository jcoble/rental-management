import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { test } from 'node:test';

const source = readFileSync('src/routes/(portal)/portal/appointments/+page.svelte', 'utf8');

test('portal appointments page loads real tenant appointments', () => {
	assert.match(source, /portal\.appointments\(\)/);
	assert.match(source, /portal-appointment-row/);
	assert.doesNotMatch(source, /Upcoming visits, inspections, and maintenance appointments will appear here\./);
});
