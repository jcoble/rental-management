import { readFileSync } from 'node:fs';
import { describe, it } from 'node:test';
import assert from 'node:assert/strict';

const endpointSource = readFileSync(
	new URL('../api/endpoints/lease-managements.ts', import.meta.url),
	'utf8'
);

const possessionActionsSource = readFileSync(
	new URL('../components/leases/PossessionActions.svelte', import.meta.url),
	'utf8'
);
const unitLeaseTabSource = readFileSync(
	new URL('../components/unit/tabs/LeaseTab.svelte', import.meta.url),
	'utf8'
);

describe('historical possession reconciliation web contract', () => {
	it('uses a dedicated endpoint and operation instead of generic give possession', () => {
		assert.match(
			endpointSource,
			/reconcileHistoricalPossession:[\s\S]*\/lease-managements\/\$\{leaseManagementId\}\/reconcile-historical-possession/
		);
		assert.match(endpointSource, /possessionGivenOn: string/);
	});

	it('shows the reconciliation action only for the governing-agreement-without-possession exception', () => {
		assert.match(
			possessionActionsSource,
			/summary\.hasGoverningAgreementWithoutPossession[\s\S]*!summary\.possessionGivenAtUtc/
		);
		assert.match(
			possessionActionsSource,
			/data-testid="lease-reconcile-historical-possession"/
		);
		assert.match(
			possessionActionsSource,
			/!summary\.hasGoverningAgreementWithoutPossession[\s\S]*!summary\.possessionGivenAtUtc/
		);
		assert.match(
			possessionActionsSource,
			/leaseManagements\.givePossession\(\s*summary\.leaseManagementId,\s*\{\s*unitId: summary\.unitId\s*\},\s*giveOperationKey\s*\)/
		);
	});

	it('mounts the shared possession action on the normal Unit Command Center lease view', () => {
		assert.match(
			unitLeaseTabSource,
			/import PossessionActions from '\$lib\/components\/leases\/PossessionActions\.svelte';/
		);
		assert.match(
			unitLeaseTabSource,
			/hasCapability\('rentals\.manage'\)\s*\|\|\s*hasCapability\('leasing\.onboarding\.manage'\)/
		);
		assert.match(
			unitLeaseTabSource,
			/data-testid="unit-possession-actions-\{relationship\.leaseManagementId\}"[\s\S]*<PossessionActions[\s\S]*summary=\{relationship\}[\s\S]*canManage=\{canManagePossession\}[\s\S]*onchanged=\{refresh\}/
		);
		assert.match(
			unitLeaseTabSource,
			/href=\{`\/units\/\$\{dashboard\.unit\.id\}\?tab=tenant-lease&view=agreements&leaseManagement=\$\{relationship\.leaseManagementId\}`\}/
		);
	});

	it('renders reconciled possession as a date-only business value', () => {
		assert.match(
			possessionActionsSource,
			/function formatBusinessDate\(value: string\)[\s\S]*timeZone: 'UTC'/
		);
		assert.doesNotMatch(
			possessionActionsSource,
			/new Date\(summary\.possessionGivenAtUtc\)\.toLocaleString\(\)/
		);
		assert.match(
			possessionActionsSource,
			/Given \{formatBusinessDate\(summary\.possessionGivenAtUtc\)\}/
		);
	});
});
