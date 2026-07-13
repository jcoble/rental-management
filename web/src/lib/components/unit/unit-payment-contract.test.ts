import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { describe, it } from 'node:test';

const typesSource = readFileSync(new URL('../../types/index.ts', import.meta.url), 'utf8');
const ledgerSource = readFileSync(new URL('./tabs/LedgerTab.svelte', import.meta.url), 'utf8');
const rentSource = readFileSync(new URL('./tabs/RentTab.svelte', import.meta.url), 'utf8');

describe('unit payment canonical identity contract', () => {
	it('models account, relationship, and legal provenance without an ambiguous lease id', () => {
		const summary = typesSource.match(/export interface UnitPaymentSummary \{[\s\S]*?\n\}/)?.[0] ?? '';

		assert.match(summary, /tenantAccountId: number/);
		assert.match(summary, /leaseManagementId: number/);
		assert.match(summary, /leaseAgreementId: number \| null/);
		assert.doesNotMatch(summary, /\bleaseId\b/);
	});

	it('scopes Unit Money reads, writes, and scans with account and relationship ids', () => {
		assert.match(rentSource, /tenantAccounts\.accountEntriesPage\(tenantAccountId as number/);
		assert.match(rentSource, /payments\.recordReceipt\(tenantAccountId, operationKey/);
		assert.match(ledgerSource, /leaseManagementId: dashboard\.currentLease\?\.leaseManagementId/);
		assert.match(ledgerSource, /tenantAccountId: dashboard\.currentLease\?\.tenantAccountId/);
		assert.doesNotMatch(`${rentSource}\n${ledgerSource}`, /\.leaseId\b/);
	});
});
