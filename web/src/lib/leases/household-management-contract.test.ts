import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { describe, it } from 'node:test';

const endpoints = readFileSync(new URL('../api/endpoints/lease-managements.ts', import.meta.url), 'utf8');
const dialog = readFileSync(new URL('../components/leases/HouseholdManagementDialog.svelte', import.meta.url), 'utf8');
const page = readFileSync(new URL('../components/leases/LeaseManagementDetail.svelte', import.meta.url), 'utf8');
const tenantEndpoints = readFileSync(new URL('../api/endpoints/tenants.ts', import.meta.url), 'utf8');

describe('relationship-scoped household management', () => {
	it('uses only canonical idempotent party commands', () => {
		for (const action of ['addParty', 'changePartyRole', 'endParty', 'grantPartyAccess', 'revokePartyAccess']) assert.match(endpoints, new RegExp(`${action}:`));
		assert.match(endpoints, /"Idempotency-Key": operationKey/);
		assert.doesNotMatch(tenantEndpoints, /portal-access|portal-invite/);
	});

	it('explains mutable membership, immutable agreements, dates, reasons, and legal basis', () => {
		assert.match(dialog, /Update who lives here and who is responsible for the lease/);
		assert.match(dialog, /Signed lease files stay unchanged/);
		assert.match(dialog, /effectiveFrom/);
		assert.match(dialog, /effectiveThrough/);
		assert.match(dialog, /changeReason/);
		assert.match(dialog, /legalBasis/);
		assert.match(dialog, /same handoff/);
	});

	it('derives mutation visibility from active-experience capabilities', () => {
		assert.match(page, /page\.data\.access\?\.navigation/);
		assert.match(page, /rentals\.manage/);
		assert.match(page, /leasing\.onboarding\.manage/);
		assert.match(page, /leasing\.agreements\.prepare/);
		assert.match(page, /canManage=\{canManageHousehold\}/);
	});

	it('shows invitation activation state without accepting an administrator-entered tenant password', () => {
		assert.match(endpoints, /requiresAccountActivation\?: boolean/);
		assert.match(endpoints, /hasPendingActivationInvitation\?: boolean/);
		assert.match(endpoints, /isPortalLoginReady\?: boolean/);
		assert.match(page, /pending for/);
		assert.match(page, /active for/);
		assert.match(dialog, /passwordless resident account/);
		assert.doesNotMatch(dialog, /name="password"|type="password"|temporary password/i);
	});

	it('uses explicit household membership and resident-login action labels', () => {
		assert.match(page, />Add person</);
		assert.match(page, />End membership</);
		assert.match(page, />Create resident login</);
		assert.match(page, />Revoke resident login</);
		assert.doesNotMatch(page, />End<|>Create login<|>Revoke login</);
	});
});
