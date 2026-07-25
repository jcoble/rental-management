import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { test } from 'node:test';

const source = readFileSync('src/routes/(portal)/portal/appointments/+page.svelte', 'utf8');

test('portal appointments page loads real tenant appointments', () => {
	assert.match(source, /portal\.appointments\(\)/);
	assert.match(source, /portal-appointment-row/);
	assert.doesNotMatch(source, /Upcoming visits, inspections, and maintenance appointments will appear here\./);
});

test('portal appointments uses a page skeleton and recoverable error state', () => {
	assert.match(source, /LoadingState/);
	assert.match(source, /variant="page"/);
	assert.match(source, /portal-appointments-loading/);
	assert.match(source, /appointmentsQuery\.refetch\(\)/);
});
