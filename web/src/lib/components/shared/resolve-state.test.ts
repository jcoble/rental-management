/**
 * Unit tests for resolveStateCode — the logic that commits a typed prefix when
 * the StateSelect closes (Tab/blur/outside-click), instead of blanking the value.
 *
 * Run with the built-in Node test runner (Node >=20 strips TS types):
 *   node --test --experimental-strip-types src/lib/components/shared/resolve-state.test.ts
 */
import { test } from 'node:test';
import assert from 'node:assert/strict';

import { resolveStateCode } from './resolve-state.ts';
import { US_STATES, US_TERRITORIES } from '../../data/us-states.ts';

const items = US_STATES.map((s) => ({ value: s.code, label: `${s.name} (${s.code})` }));
const itemsWithTerritories = [...US_STATES, ...US_TERRITORIES].map((s) => ({
	value: s.code,
	label: `${s.name} (${s.code})`
}));

test('prefix of a state name resolves to that state ("Oh" -> OH)', () => {
	assert.equal(resolveStateCode({ typed: 'Oh', items, currentValue: '' }), 'OH');
});

test('exact 2-letter code resolves ("OH" -> OH)', () => {
	assert.equal(resolveStateCode({ typed: 'OH', items, currentValue: '' }), 'OH');
});

test('lowercase code resolves case-insensitively ("ca" -> CA)', () => {
	assert.equal(resolveStateCode({ typed: 'ca', items, currentValue: '' }), 'CA');
});

test('full state name resolves ("California" -> CA)', () => {
	assert.equal(resolveStateCode({ typed: 'California', items, currentValue: '' }), 'CA');
});

test('the rendered label resolves ("Ohio (OH)" -> OH)', () => {
	assert.equal(resolveStateCode({ typed: 'Ohio (OH)', items, currentValue: '' }), 'OH');
});

test('surrounding whitespace is ignored ("  Oh  " -> OH)', () => {
	assert.equal(resolveStateCode({ typed: '  Oh  ', items, currentValue: '' }), 'OH');
});

test('gibberish with no prior value resolves to empty', () => {
	assert.equal(resolveStateCode({ typed: 'zzzzz', items, currentValue: '' }), '');
});

test('gibberish NEVER blanks an already-set value (reverts to prior)', () => {
	assert.equal(resolveStateCode({ typed: 'zzzzz', items, currentValue: 'CA' }), 'CA');
});

test('empty typed text with no prior value -> empty (nothing to keep)', () => {
	assert.equal(resolveStateCode({ typed: '', items, currentValue: '' }), '');
});

test('empty typed text with a prior value KEEPS the prior value (never blank)', () => {
	// The box is cleared on open, so focusing a populated field and tabbing through
	// must not wipe the existing selection.
	assert.equal(resolveStateCode({ typed: '', items, currentValue: 'CA' }), 'CA');
});

test('ambiguous prefix matching >1 state does not guess; reverts to prior value', () => {
	// "New" matches New Hampshire/Jersey/Mexico/York — ambiguous, so keep prior.
	assert.equal(resolveStateCode({ typed: 'New', items, currentValue: 'TX' }), 'TX');
});

test('ambiguous prefix with no prior value -> empty (no guess)', () => {
	assert.equal(resolveStateCode({ typed: 'New', items, currentValue: '' }), '');
});

test('an exact match wins even when the prefix is otherwise ambiguous', () => {
	// "New York" is a prefix of nothing else and is itself a full name -> NY.
	assert.equal(resolveStateCode({ typed: 'New York', items, currentValue: '' }), 'NY');
});

test('prefix that uniquely identifies one of several "New" states resolves', () => {
	assert.equal(resolveStateCode({ typed: 'New J', items, currentValue: '' }), 'NJ');
});

test('territories resolve when included ("Guam" -> GU)', () => {
	assert.equal(
		resolveStateCode({ typed: 'Guam', items: itemsWithTerritories, currentValue: '' }),
		'GU'
	);
});

test('typed text equal to the current label is a no-op (stays selected)', () => {
	assert.equal(resolveStateCode({ typed: 'Ohio (OH)', items, currentValue: 'OH' }), 'OH');
});
