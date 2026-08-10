import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { describe, it } from 'node:test';

const source = readFileSync(
	new URL('./tabs/RentTab.svelte', import.meta.url),
	'utf8'
);
const panelSource = readFileSync(
	new URL('../accounting/TenantLedgerPanel.svelte', import.meta.url),
	'utf8'
);
const paymentSheetSource = readFileSync(
	new URL('../accounting/RecordPaymentSheet.svelte', import.meta.url),
	'utf8'
);
const chargeSheetSource = readFileSync(
	new URL('../accounting/OneTimeChargeSheet.svelte', import.meta.url),
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

describe('unit rent canonical tenant ledger workflows', () => {
	it('delegates RentTab to the canonical ledger surface and keeps receipt detail navigation', () => {
		assert.match(source, /AccountingDetailMode/);
		assert.match(source, /TenantLedgerPanel \{dashboard\} \{onScan\} onopenpayment=\{openReceipt\}/);
		assert.match(source, /PaymentDetail/);
		assert.match(source, /goto\(unitUrl\(id\)/);
		assert.doesNotMatch(source, /tenantAccounts\.(accountEntriesPage|chargesPage|depositsPage)/);
		assert.doesNotMatch(source, /payments\.(recordReceipt|postCharge)/);
	});

	it('records payments through the canonical command and server-side open-charge query', () => {
		assert.match(paymentSheetSource, /openOnly: true/);
		assert.match(paymentSheetSource, /tenantMoney\.recordReceipt\(tenantAccountId, operationKey/);
		assert.match(paymentSheetSource, /operationKey \?\?= crypto\.randomUUID\(\)/);
		assert.match(paymentSheetSource, /targetChargeEntryId: form\.applyMode === 'specific'/);
		assert.match(paymentSheetSource, /allocateOldestCharges: form\.applyMode === 'oldest'/);
		assert.doesNotMatch(paymentSheetSource, /openCharges\.filter/);
		assert.match(
			portalServiceSource,
			/BuildTenantChargeQuery[\s\S]*balance\.OpenAmount > 0m[\s\S]*return ApplyTenantChargeFilters/
		);
	});

	it('creates category-aware one-time charges with server-owned correction boundaries', () => {
		assert.match(chargeSheetSource, /tenantMoney\.postCharge\(tenantAccountId, operationKey/);
		assert.match(chargeSheetSource, /incomeLedgerAccountId: resolvedAccount\?\.id \?\? null/);
		assert.match(chargeSheetSource, /servicePeriodStartOn: form\.servicePeriodStartOn \|\| null/);
		assert.match(chargeSheetSource, /servicePeriodEndOn: form\.servicePeriodEndOn \|\| null/);
		assert.match(panelSource, /tenantMoney\.reverseCharge/);
		assert.match(panelSource, /Posts a credit to reduce this charge/);
		assert.match(panelSource, /Posts a related charge for the amount that was missed/);
		assert.match(panelSource, /Removes this charge with a linked reversal entry/);
	});

	it('keeps correction actions in the canonical month ledger and out of RentTab', () => {
		assert.match(panelSource, /data-testid="fix-charge-dialog"/);
		assert.match(panelSource, /buildTenantLedgerRowActionFlow/);
		assert.match(panelSource, /flow\.kind === 'give-credit'/);
		assert.match(panelSource, /flow\.kind === 'reverse'/);
		assert.doesNotMatch(source, /reverseEntry/);
	});

	it('renders immutable receipt detail without edit or delete actions', () => {
		assert.match(paymentDetailSource, /This receipt stays in your records/);
		assert.match(paymentDetailSource, /records a matching correction instead of rewriting it/);
		assert.doesNotMatch(paymentDetailSource, /payments\.(update|delete|markPaid)/);
	});
});
