import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { describe, it } from 'node:test';

const endpointSource = readFileSync(
	new URL('../api/endpoints/eviction-cases.ts', import.meta.url),
	'utf8'
);
const sectionSource = readFileSync(
	new URL('../components/leases/LeaseEvictionCasesSection.svelte', import.meta.url),
	'utf8'
);
const detailSource = readFileSync(
	new URL('../components/leases/LeaseManagementDetail.svelte', import.meta.url),
	'utf8'
);

describe('lease eviction cases section contract', () => {
	it('lists only the selected canonical relationship through server paging', () => {
		assert.match(sectionSource, /evictionCases\.listPage\(\{[\s\S]*leaseManagementId,[\s\S]*skip,[\s\S]*take: PAGE_SIZE/);
		assert.match(sectionSource, /sort: '-updatedAt'/);
		assert.match(sectionSource, /<Pagination[\s\S]*bind:skip=\{skip\}/);
		assert.match(sectionSource, /as caseItem \(caseItem\.id\)/);
		assert.doesNotMatch(sectionSource, /evictionCases\.list\(/);
		assert.doesNotMatch(sectionSource, /take:\s*(100|200)/);
		assert.match(endpointSource, /leaseManagementId\?: number/);
		assert.match(endpointSource, /leaseManagementId: params\?\.leaseManagementId/);
	});

	it('creates a case from the relationship, optional agreement, and selected parties', () => {
		assert.match(endpointSource, /interface CreateEvictionCaseRequest[\s\S]*leaseManagementId: number/);
		assert.match(endpointSource, /leaseAgreementId\?: number \| null/);
		assert.match(endpointSource, /respondentLeaseManagementPartyIds: number\[\]/);
		assert.match(sectionSource, /return \{[\s\S]*leaseManagementId,[\s\S]*leaseAgreementId:[\s\S]*respondentLeaseManagementPartyIds:/);
		assert.match(sectionSource, /evictionCases\.create\(request\)/);
		assert.match(sectionSource, /data-testid="eviction-case-create"/);
		assert.match(sectionSource, /data-testid="eviction-case-respondent-/);
		assert.match(detailSource, /<LeaseEvictionCasesSection[\s\S]*\{leaseManagementId\}[\s\S]*leaseAgreementId=\{summary\.leaseAgreementId\}[\s\S]*respondents=\{detail\.parties\}/);
		assert.doesNotMatch(sectionSource, /\bleaseId\b/);
	});

	it('updates status and appends dated case updates through typed actions', () => {
		assert.match(endpointSource, /interface UpdateEvictionCaseRequest[\s\S]*status\?: EvictionCaseStatus/);
		assert.match(endpointSource, /update: \(id: number, data: UpdateEvictionCaseRequest\)/);
		assert.match(sectionSource, /evictionCases\.update\(id, \{ status \}\)/);
		assert.match(sectionSource, /evictionCases\.addEvent\(id, \{[\s\S]*eventType:[\s\S]*eventDate:[\s\S]*notes:/);
		assert.match(sectionSource, /testid="eviction-case-status-/);
		assert.match(sectionSource, /data-testid="eviction-case-add-event-/);
	});

	it('keeps the list readable while gating every mutation with rentals.manage', () => {
		assert.match(detailSource, /const canManageEvictions = \$derived\(activeCapabilities\.has\('rentals\.manage'\)\)/);
		assert.match(detailSource, /<PossessionActions[\s\S]*<LeaseEvictionCasesSection/);
		assert.match(detailSource, /canManage=\{canManageEvictions\}/);
		assert.ok((sectionSource.match(/\{#if canManage\}/g) ?? []).length >= 3);
		assert.match(sectionSource, /<Card data-testid="lease-eviction-cases"/);
	});
});
