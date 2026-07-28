import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';

const ownersPage = readFileSync(
	new URL('../../routes/(protected)/owners/+page.svelte', import.meta.url),
	'utf8'
);

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
