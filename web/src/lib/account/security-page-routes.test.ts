import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { test } from 'node:test';

test('portal security route uses the shared account security page and portal back link', () => {
	const pageSource = readFileSync('src/routes/(portal)/portal/security/+page.svelte', 'utf8');
	const actionSource = readFileSync('src/routes/(portal)/portal/security/+page.server.ts', 'utf8');

	assert.match(pageSource, /AccountSecurityPage/);
	assert.match(pageSource, /backHref="\/portal"/);
	assert.match(pageSource, /backLabel="Back to portal"/);
	assert.match(actionSource, /\$lib\/server\/account-security/);
	assert.match(actionSource, /changePassword/);
});

test('staff security route still uses the shared account security page and settings back link', () => {
	const pageSource = readFileSync('src/routes/(protected)/settings/security/+page.svelte', 'utf8');
	const actionSource = readFileSync('src/routes/(protected)/settings/security/+page.server.ts', 'utf8');

	assert.match(pageSource, /AccountSecurityPage/);
	assert.match(pageSource, /backHref="\/settings"/);
	assert.match(pageSource, /backLabel="Back to settings"/);
	assert.match(actionSource, /\$lib\/server\/account-security/);
	assert.match(actionSource, /changePassword/);
});

test('shared account security action posts change-password directly with the current access token', () => {
	const actionSource = readFileSync('src/lib/server/account-security.ts', 'utf8');
	const endpointSource = readFileSync('src/lib/api/endpoints/auth.ts', 'utf8');

	assert.match(actionSource, /serverPost<\{ message: string \}>/);
	assert.match(actionSource, /'\/auth\/change-password'/);
	assert.match(actionSource, /locals\.accessToken/);
	assert.match(actionSource, /operationKey = randomUUID\(\)/);
	assert.match(actionSource, /'Idempotency-Key': operationKey/);
	assert.match(endpointSource, /crypto\.subtle\.digest/);
	assert.match(endpointSource, /idempotentMutation\(`auth:change-password:\$\{requestScope\}`/);
	assert.match(endpointSource, /'Idempotency-Key': operationKey/);
});
