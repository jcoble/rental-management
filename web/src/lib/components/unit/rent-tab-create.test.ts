import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { describe, it } from 'node:test';

const source = readFileSync(
	new URL('./tabs/RentTab.svelte', import.meta.url),
	'utf8'
);

// TSK-457: RentTab lost its inline expand-to-edit payment UI; editing a payment's reference
// and notes now lives in the extracted PaymentDetail component, folded into the Rent tab on
// ?payment= select. The reference/notes-edit case below asserts against PaymentDetail.
const paymentDetailSource = readFileSync(
	new URL('../records/PaymentDetail.svelte', import.meta.url),
	'utf8'
);
const portalServiceSource = readFileSync(
	new URL('../../../../../RentalCommand.Api/Services/Domain/PortalService.cs', import.meta.url),
	'utf8'
);

describe('unit rent canonical receipt and charge commands', () => {
	it('captures receipt metadata and posts with an idempotency key', () => {
		assert.match(source, /method: ''/);
		assert.match(source, /reference: ''/);
		assert.match(source, /operationKey \?\?= crypto\.randomUUID\(\)/);
		assert.match(source, /payments\.recordReceipt\(tenantAccountId, operationKey/);
		assert.match(source, /targetChargeEntryId: receiptTargetChargeEntryId\(\)/);
		assert.match(source, /Leave unapplied\/advance receipt/);
		assert.doesNotMatch(source, /allocateOldestCharges/);
	});

	it('receives receipt targets from the server-side open-charge query without client filtering', () => {
		assert.match(source, /tenantAccounts\.chargesPage\(tenantAccountId as number/);
		assert.match(source, /\{#each chargesQuery\.data\?\.items \?\? \[\] as charge/);
		assert.doesNotMatch(source, /chargesQuery\.data\?\.items\.filter/);
		assert.match(
			portalServiceSource,
			/BuildTenantChargeQuery[\s\S]*balance\.OpenAmount > 0m[\s\S]*return ApplyTenantChargeFilters/
		);
	});

	it('offers a manual charge only from tenant-account context', () => {
		assert.match(source, /payments\.postCharge\(tenantAccountId, operationKey/);
		assert.match(source, /Use this only for a true one-off charge/);
	});

	it('offers an idempotent generic ledger reversal for non-payment activity rows', () => {
		assert.match(source, /tenantAccounts\.reverseEntry\(tenantAccountId, operationKey/);
		assert.match(source, /canReverseTenantLedgerEntry\(entry\)/);
		assert.match(source, /data-testid=\{`rent-reverse-ledger-entry-\$\{entry\.tenantLedgerEntryId\}`\}/);
		assert.match(source, /data-testid="rent-reversal-form"/);
		assert.doesNotMatch(source, /entry\.entryType === 'PaymentReceipt'[\s\S]*tenantAccounts\.reverseEntry/);
	});

	it('renders immutable receipt detail without edit or delete actions', () => {
		assert.match(paymentDetailSource, /This receipt stays in your records/);
		assert.match(paymentDetailSource, /records a matching correction instead of rewriting it/);
		assert.doesNotMatch(paymentDetailSource, /payments\.(update|delete|markPaid)/);
	});
});
