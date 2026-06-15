import { test } from 'node:test';
import assert from 'node:assert/strict';
import { generateOAuthState, isValidOAuthState } from './oauth-state.ts';

// H-3 regression (security): the callback must accept ONLY a state that matches the value stored
// before the redirect. A missing, empty, or forged state must be rejected so an attacker-minted
// authorization code paired with a bad state never reaches the code exchange (login-CSRF defense).

test('isValidOAuthState accepts an exact match', () => {
	const state = generateOAuthState();
	assert.equal(isValidOAuthState(state, state), true);
});

test('isValidOAuthState rejects a mismatch', () => {
	assert.equal(isValidOAuthState(generateOAuthState(), generateOAuthState()), false);
});

test('isValidOAuthState rejects a missing returned state', () => {
	const stored = generateOAuthState();
	assert.equal(isValidOAuthState(null, stored), false);
	assert.equal(isValidOAuthState(undefined, stored), false);
	assert.equal(isValidOAuthState('', stored), false);
});

test('isValidOAuthState rejects when no state was stored (no cookie)', () => {
	assert.equal(isValidOAuthState(generateOAuthState(), undefined), false);
	assert.equal(isValidOAuthState(generateOAuthState(), ''), false);
});

test('isValidOAuthState rejects two absent values (no silent both-empty pass)', () => {
	assert.equal(isValidOAuthState(undefined, undefined), false);
	assert.equal(isValidOAuthState('', ''), false);
});

test('generateOAuthState returns distinct, non-trivial values', () => {
	const a = generateOAuthState();
	const b = generateOAuthState();
	assert.notEqual(a, b);
	assert.ok(a.length >= 16);
});
