import { test } from 'node:test';
import assert from 'node:assert/strict';

import { userFromAccessToken } from './jwt-claims.ts';

/** Build a (signature-less but structurally valid) JWT from a claims object. */
function makeToken(claims: Record<string, unknown>): string {
	const header = Buffer.from(JSON.stringify({ alg: 'HS256', typ: 'JWT' })).toString('base64url');
	const payload = Buffer.from(JSON.stringify(claims)).toString('base64url');
	return `${header}.${payload}.signature`;
}

const NOW = 1_700_000_000_000; // fixed "now" in ms
const FUTURE_EXP = Math.floor(NOW / 1000) + 600; // +10 min, in seconds
const PAST_EXP = Math.floor(NOW / 1000) - 600; // -10 min, in seconds

test('decodes a valid, unexpired token into the User shape', () => {
	const token = makeToken({
		sub: '42',
		email: 'landlord@example.com',
		displayName: 'Land Lord',
		portfolioId: '7',
		tenantId: '3',
		role: ['Admin', 'Manager'],
		exp: FUTURE_EXP
	});

	const user = userFromAccessToken(token, NOW);
	assert.deepEqual(user, {
		id: 42,
		email: 'landlord@example.com',
		displayName: 'Land Lord',
		portfolioId: 7,
		ownerEntityId: null,
		tenantId: 3,
		roles: ['Admin', 'Manager'],
		emailVerified: true
	});
});

test('reads roles from the long ClaimTypes.Role URI (single string)', () => {
	const token = makeToken({
		sub: '1',
		exp: FUTURE_EXP,
		'http://schemas.microsoft.com/ws/2008/06/identity/claims/role': 'Tenant'
	});

	assert.deepEqual(userFromAccessToken(token, NOW)?.roles, ['Tenant']);
});

test('falls back to email for displayName and yields null optional ids', () => {
	const token = makeToken({ sub: '5', email: 'a@b.com', exp: FUTURE_EXP });
	const user = userFromAccessToken(token, NOW);
	assert.equal(user?.displayName, 'a@b.com');
	assert.equal(user?.portfolioId, null);
	assert.equal(user?.ownerEntityId, null);
	assert.equal(user?.tenantId, null);
	assert.deepEqual(user?.roles, []);
});

test('rejects an expired token', () => {
	const token = makeToken({ sub: '1', exp: PAST_EXP });
	assert.equal(userFromAccessToken(token, NOW), null);
});

test('rejects a token with no exp claim', () => {
	const token = makeToken({ sub: '1', email: 'a@b.com' });
	assert.equal(userFromAccessToken(token, NOW), null);
});

test('rejects a token with no usable subject', () => {
	const token = makeToken({ email: 'a@b.com', exp: FUTURE_EXP });
	assert.equal(userFromAccessToken(token, NOW), null);
});

test('rejects a structurally malformed token', () => {
	assert.equal(userFromAccessToken('not-a-jwt', NOW), null);
	assert.equal(userFromAccessToken('only.two', NOW), null);
});

test('rejects a token whose payload is not valid base64url JSON', () => {
	assert.equal(userFromAccessToken('header.@@@not-json@@@.sig', NOW), null);
});
