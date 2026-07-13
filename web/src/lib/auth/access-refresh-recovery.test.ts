import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';

const client = readFileSync(new URL('../api/client.ts', import.meta.url), 'utf8');
const signalr = readFileSync(new URL('../realtime/signalr.ts', import.meta.url), 'utf8');
const protectedLayout = readFileSync(
	new URL('../../routes/(protected)/+layout.svelte', import.meta.url),
	'utf8'
);
const portalLayout = readFileSync(
	new URL('../../routes/(portal)/+layout.svelte', import.meta.url),
	'utf8'
);

test('token refresh applies changed authority and awaits one recovery before resolving', () => {
	const refreshOperation = client.slice(
		client.indexOf('async function performTokenRefresh'),
		client.indexOf('/**\n * Coalesce a BURST of refreshes')
	);
	const comparison = client.indexOf(
		'const authorityChanged = accessEnvelopeAuthorityChanged(previousAccess, data.access);'
	);
	const storeUpdate = client.indexOf(
		'updateToken(data.accessToken, new Date(data.accessTokenExpiration), data.access);'
	);
	const recovery = client.indexOf('await accessRecoveryCallback?.(', storeUpdate);
	assert.ok(comparison >= 0, 'refresh must compare authority coordinates');
	assert.ok(storeUpdate > comparison, 'the authoritative envelope must be installed after comparison');
	assert.ok(recovery > storeUpdate, 'changed authority recovery must be awaited before refresh resolves');
	assert.equal(
		refreshOperation.match(/await accessRecoveryCallback\?\./g)?.length,
		1,
		'a refresh execution must invoke recovery at most once'
	);
	assert.match(refreshOperation, /source === 'signalr' \? 'refresh-signalr' : 'refresh'/);

	const staleRecovery = client.slice(
		client.indexOf('async function recoverStaleAccess()'),
		client.indexOf('function isMutation')
	);
	assert.doesNotMatch(staleRecovery, /accessRecoveryCallback/, 'stale retry must not purge twice');
});

test('SignalR refresh marks its handshake source and avoids recursive reconnect', () => {
	assert.match(signalr, /await refreshToken\('signalr'\)/);
	assert.match(protectedLayout, /if \(reason === 'refresh-signalr'\)/);
	assert.match(portalLayout, /if \(reason === 'refresh-signalr'\)/);
	const handshakeRecovery = protectedLayout.slice(
		protectedLayout.indexOf("if (reason === 'refresh-signalr')"),
		protectedLayout.indexOf('await signalRService.disconnect()')
	);
	assert.doesNotMatch(handshakeRecovery, /signalRService\.(disconnect|connect)/);
});
