import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';

const ownersPage = readFileSync(
	new URL('../../routes/(protected)/owners/+page.svelte', import.meta.url),
	'utf8'
);
const ownersEndpoint = readFileSync(new URL('../api/endpoints/owners.ts', import.meta.url), 'utf8');

test('owner portal grid shows pending invitation before active access', () => {
	assert.match(
		ownersPage,
		/hasPendingOwnerPortalInvitation \? 'Pending invitation' : o\.hasActiveOwnerPortalAccess \? 'Active' : 'Not active'/
	);

	const pendingBranch = ownersPage.indexOf('{#if o.hasPendingOwnerPortalInvitation}');
	const activeBranch = ownersPage.indexOf('{:else if o.hasActiveOwnerPortalAccess}');
	assert.ok(pendingBranch >= 0, 'missing pending invitation branch');
	assert.ok(activeBranch >= 0, 'missing active portal branch');
	assert.ok(pendingBranch < activeBranch, 'pending invitation must render before active access');
});

test('pending and active portal access can be revoked only after confirmation', () => {
	assert.equal(
		ownersPage.match(/data-testid="owner-portal-revoke"/g)?.length,
		2,
		'pending and active branches must both expose revoke'
	);
	assert.match(ownersPage, /title="Revoke owner portal access"/);
	assert.match(ownersPage, /confirmLabel="Revoke access"/);
	assert.match(
		ownersPage,
		/onconfirm=\{\(\) => ownerPortalRevokeTarget && revokePortalMutation\.mutate\(ownerPortalRevokeTarget\.id\)\}/
	);
	assert.match(
		ownersPage,
		/disabled=\{\(o\.assignedPropertyCount \?\? 0\) > 0 \|\| o\.hasActiveOwnerPortalAccess \|\| o\.hasPendingOwnerPortalInvitation\}/
	);
	assert.match(ownersEndpoint, /idempotentMutation\(`owners:portal-access:revoke:\$\{id\}`/);
	assert.match(ownersEndpoint, /`\/owner-entities\/\$\{id\}\/portal-access\/revoke`/);
});
