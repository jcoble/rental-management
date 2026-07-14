import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { describe, it } from 'node:test';

const endpoints = readFileSync(new URL('../api/endpoints/lease-managements.ts', import.meta.url), 'utf8');
const dialog = readFileSync(new URL('../components/leases/HouseholdManagementDialog.svelte', import.meta.url), 'utf8');
const page = readFileSync(new URL('../../routes/(protected)/leases/[id]/+page.svelte', import.meta.url), 'utf8');
const tenantEndpoints = readFileSync(new URL('../api/endpoints/tenants.ts', import.meta.url), 'utf8');

describe('relationship-scoped household management', () => {
	it('uses only canonical idempotent party commands', () => {
		for (const action of ['addParty', 'changePartyRole', 'endParty', 'grantPartyAccess', 'revokePartyAccess']) assert.match(endpoints, new RegExp(`${action}:`));
		assert.match(endpoints, /"Idempotency-Key": operationKey/);
		assert.doesNotMatch(tenantEndpoints, /portal-access|portal-invite/);
	});

	it('explains mutable membership, immutable agreements, dates, reasons, and legal basis', () => {
		assert.match(dialog, /Household membership is an effective-dated relationship/);
		assert.match(dialog, /Signed agreement PDFs stay immutable/);
		assert.match(dialog, /effectiveFrom/);
		assert.match(dialog, /effectiveThrough/);
		assert.match(dialog, /changeReason/);
		assert.match(dialog, /legalBasis/);
		assert.match(dialog, /Primary handoff/);
	});

	it('derives mutation visibility from active-experience capabilities', () => {
		assert.match(page, /page\.data\.access\?\.navigation/);
		assert.match(page, /rentals\.manage/);
		assert.match(page, /leasing\.onboarding\.manage/);
		assert.match(page, /leasing\.agreements\.prepare/);
		assert.match(page, /canManage=\{canManageHousehold\}/);
	});
});
