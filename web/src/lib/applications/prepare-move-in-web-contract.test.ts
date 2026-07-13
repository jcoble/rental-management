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

describe('canonical web Prepare move-in workflow', () => {
	it('posts the complete typed request with an explicit idempotency key', () => {
		assert.match(endpointSource, /export interface PrepareMoveInRequest/);
		assert.match(endpointSource, /applicationId: number/);
		assert.match(endpointSource, /parties: PrepareMoveInPartyRequest\[\]/);
		assert.match(endpointSource, /documentTemplateId: number/);
		assert.match(endpointSource, /termsPayload: Record<string, unknown>/);
		assert.match(endpointSource, /openingBalanceAmount: number \| null/);
		assert.match(
			endpointSource,
			/fetchApi<PrepareMoveInResponse>\(["']\/lease-managements\/prepare-move-in["']/
		);
		assert.match(endpointSource, /["']Idempotency-Key["']:\s*operationKey/);
	});

	it('consumes exact application, unit, and tenant context without legacy lease creation', () => {
		assert.match(applicationsPageSource, /readPrepareMoveInPrefill\(page\.url\.searchParams\)/);
		assert.match(applicationsPageSource, /<PrepareMoveInDialog/);
		assert.match(dialogSource, /seededApplicationId/);
		assert.match(dialogSource, /data-testid="prepare-move-in-locked-application"/);
		assert.match(dialogSource, /data-testid="prepare-move-in-locked-unit"/);
		assert.match(dialogSource, /application\.approvedTenantId !== requiredTenantId/);
		assert.doesNotMatch(dialogSource, /leases\.create/);
		assert.doesNotMatch(dialogSource, /leaseId/);
	});

	it('uses server-filtered paged selectors for unresolved approved applications, units, and templates', () => {
		assert.match(dialogSource, /applications\.listPage\(\{/);
		assert.match(dialogSource, /status: 'Approved'/);
		assert.match(dialogSource, /units\.listWithHealthPage\(\{/);
		assert.match(dialogSource, /propertyId: application\?\.propertyId \?\? undefined/);
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
