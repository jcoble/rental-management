import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { describe, it } from 'node:test';

const endpointSource = readFileSync(
	new URL('../api/endpoints/lease-managements.ts', import.meta.url),
	'utf8'
);
const dialogSource = readFileSync(
	new URL('../components/applications/PrepareMoveInDialog.svelte', import.meta.url),
	'utf8'
);
const applicationsPageSource = readFileSync(
	new URL('../../routes/(protected)/applications/+page.svelte', import.meta.url),
	'utf8'
);
const leasesPageSource = readFileSync(
	new URL('../../routes/(protected)/leases/+page.svelte', import.meta.url),
	'utf8'
);

describe('canonical web Prepare move-in workflow', () => {
	it('posts the complete typed request with an explicit idempotency key', () => {
		assert.match(endpointSource, /export interface PrepareMoveInRequest/);
		assert.match(endpointSource, /applicationId: number \| null/);
		assert.match(endpointSource, /tenantId: number \| null/);
		assert.match(endpointSource, /newTenant: \{/);
		assert.match(endpointSource, /parties: PrepareMoveInPartyRequest\[\]/);
		assert.match(endpointSource, /documentTemplateId: number \| null/);
		assert.match(endpointSource, /termsPayload: Record<string, unknown>/);
		assert.match(endpointSource, /openingBalanceAmount: number \| null/);
		assert.match(
			endpointSource,
			/fetchApi<PrepareMoveInResponse>\(["']\/lease-managements\/prepare-move-in["']/
		);
		assert.match(endpointSource, /["']Idempotency-Key["']:\s*operationKey/);
	});

	it('offers a direct manual lease path from Leases without creating a legacy lease', () => {
		assert.match(leasesPageSource, /data-testid="leases-create-lease"/);
		assert.match(leasesPageSource, /<FilePlus2[^>]*\/> Create lease/);
		assert.match(leasesPageSource, /<PrepareMoveInDialog[\s\S]*mode="manual"/);
		assert.match(leasesPageSource, /onprepared=\{finishManualLease\}/);
		assert.doesNotMatch(leasesPageSource, /leases\.create/);
		assert.doesNotMatch(leasesPageSource, /tenants\.create/);
	});

	it('consumes exact application, unit, and tenant context without legacy lease creation', () => {
		assert.match(applicationsPageSource, /readPrepareMoveInPrefill\(page\.url\.searchParams\)/);
		assert.match(applicationsPageSource, /<PrepareMoveInDialog/);
		assert.match(dialogSource, /seededApplicationId/);
		assert.match(dialogSource, /data-testid="prepare-move-in-locked-application"/);
		assert.match(dialogSource, /data-testid="prepare-move-in-locked-unit"/);
		assert.match(dialogSource, /buildPrepareMoveInRequest\(form,\s*\{\s*requireApplication: !manualMode\s*\}\)/);
		assert.match(dialogSource, /selectedApplicationId <= 0[\s\S]*Choose an approved application/);
		assert.match(dialogSource, /application\.approvedTenantId !== requiredTenantId/);
		assert.doesNotMatch(dialogSource, /leases\.create/);
		assert.doesNotMatch(dialogSource, /leaseId/);
	});

	it('uses server-filtered paged selectors for unresolved approved applications, units, and templates', () => {
		assert.match(dialogSource, /applications\.listPage\(\{/);
		assert.match(dialogSource, /status: 'Approved'/);
		assert.match(dialogSource, /loadManualUnitOptions/);
		assert.match(dialogSource, /status: 'Vacant'/);
		assert.match(dialogSource, /loadManualTenantOptions/);
		assert.match(dialogSource, /availableForLease: true/);
		assert.match(dialogSource, /units\.listWithHealthPage\(\{/);
		assert.match(dialogSource, /propertyId: selectedPropertyId \?\? undefined/);
		assert.match(dialogSource, /documentTemplates\.listPage\(\{/);
		assert.match(dialogSource, /documentTemplates\.get\(selectedTemplateId\)/);
		assert.match(dialogSource, /kind: 'Lease'/);
		assert.match(dialogSource, /status: 'Active'/);
		assert.doesNotMatch(dialogSource, /\.items\.find\(/);
		assert.doesNotMatch(dialogSource, /\.items\.filter\(/);
	});

	it('captures every required agreement and money field before the atomic submit', () => {
		for (const testId of [
			'prepare-move-in-party-effective-input',
			'prepare-move-in-template-input',
			'prepare-move-in-term-type-input',
			'prepare-move-in-term-start-input',
			'prepare-move-in-rent-input',
			'prepare-move-in-due-day-input',
			'prepare-move-in-deposit-input',
			'prepare-move-in-late-fee-input',
			'prepare-move-in-grace-input',
			'prepare-move-in-create-deposit-account',
			'prepare-move-in-opening-amount-input',
			'prepare-move-in-submit'
		]) {
			assert.match(dialogSource, new RegExp(`(?:data-testid|testid)="${testId}"`));
		}
		assert.doesNotMatch(dialogSource, /terms-payload-input/);
		assert.doesNotMatch(dialogSource, /schema version/i);
	});
});
