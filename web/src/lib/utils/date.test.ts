import { test } from 'node:test';
import assert from 'node:assert/strict';
import { formatDateOnly, localInputToOffsetIso } from './date.ts';

test('formatDateOnly preserves UTC-midnight calendar dates', () => {
	assert.equal(formatDateOnly('2026-09-22T00:00:00Z'), 'Sep 22, 2026');
	assert.equal(formatDateOnly('2026-09-22'), 'Sep 22, 2026');
	assert.equal(formatDateOnly('2027-01-10T00:00:00Z'), 'Jan 10, 2027');
});

// The work-order schedule FROZEN contract: a `datetime-local` wall-clock value must serialize to an
// offset-bearing ISO string that denotes the SAME instant as the local wall-clock time. Round-tripping
// through Date is timezone-independent, so this guards the offset-sign logic on any runner TZ.
test('localInputToOffsetIso preserves the local instant', () => {
	const input = '2026-06-15T14:00';
	const iso = localInputToOffsetIso(input);
	assert.ok(iso, 'expected an ISO string');
	// Parsing the offset string back must equal the local interpretation of the wall-clock value.
	assert.equal(new Date(iso!).getTime(), new Date(input).getTime());
	// And it must carry an explicit offset (±HH:MM or Z), never be a bare un-zoned string.
	assert.match(iso!, /([+-]\d{2}:\d{2}|Z)$/);
});

test('localInputToOffsetIso returns null for empty/invalid input', () => {
	assert.equal(localInputToOffsetIso(''), null);
	assert.equal(localInputToOffsetIso(null), null);
	assert.equal(localInputToOffsetIso('not-a-date'), null);
});
