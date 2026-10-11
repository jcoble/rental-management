import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { describe, it } from 'node:test';

const source = (path: string) => readFileSync(new URL(path, import.meta.url), 'utf8');

describe('staff create dialog validation clearing', () => {
	it('clears property and tenant form errors as corrected fields change', () => {
		const propertiesPage = source('../../routes/(protected)/properties/+page.svelte');
		const tenantFields = source('../components/forms/TenantFields.svelte');

		assert.match(propertiesPage, /function clearPropertyError\(field: string\)/);
		assert.match(propertiesPage, /clearPropertyError\('name'\)/);
		assert.match(propertiesPage, /clearPropertyError\('addressLine1'\)/);
		assert.match(propertiesPage, /clearPropertyError\('postalCode'\)/);
		assert.match(tenantFields, /function clearTenantError\(field: string\)/);
		assert.match(tenantFields, /clearTenantError\('firstName'\)/);
		assert.match(tenantFields, /clearTenantError\('lastName'\)/);
		assert.match(tenantFields, /clearTenantError\('email'\)/);
	});

	it('clears work-order, inspection, and recurring task errors as corrected fields change', () => {
		const maintenancePage = source('../../routes/(protected)/maintenance/+page.svelte');
		const recurringDialog = source('../components/maintenance/RecurringMaintenanceFormDialog.svelte');

		assert.match(maintenancePage, /function clearWoError\(field: string\)/);
		assert.match(maintenancePage, /clearWoError\('title'\)/);
		assert.match(maintenancePage, /clearInspectionError\('scheduledFor'\)/);
		assert.match(recurringDialog, /function clearRecurringError\(field: string\)/);
		assert.match(recurringDialog, /clearRecurringError\('nextDueDate'\)/);
	});

	it('keeps vendor and expense clearing while tenant-money sheets own canonical validation', () => {
		const vendorFields = source('../components/forms/VendorFields.svelte');
		const accountingPage = source('../../routes/(protected)/accounting/+page.svelte');
		const rentTab = source('../components/unit/tabs/RentTab.svelte');
		const tenantLedgerPanel = source('../components/accounting/TenantLedgerPanel.svelte');
		const paymentSheet = source('../components/accounting/RecordPaymentSheet.svelte');
		const chargeSheet = source('../components/accounting/OneTimeChargeSheet.svelte');
		const creditSheet = source('../components/accounting/TenantCreditSheet.svelte');
		const recurringSheet = source('../components/accounting/RecurringChargeSheet.svelte');

		assert.match(vendorFields, /function clearVendorError\(field: string\)/);
		assert.match(vendorFields, /clearVendorError\('name'\)/);
		assert.match(vendorFields, /clearVendorError\('serviceType'\)/);
		assert.match(accountingPage, /function clearExpenseError\(field: string\)/);
		assert.match(accountingPage, /clearExpenseError\('description'\)/);
		assert.match(accountingPage, /clearExpenseError\('amount'\)/);
		assert.match(rentTab, /TenantLedgerPanel/);
		assert.doesNotMatch(rentTab, /clearCreateError/);
		for (const sheet of ['RecordPaymentSheet', 'OneTimeChargeSheet', 'TenantCreditSheet', 'RecurringChargeSheet']) {
			assert.match(tenantLedgerPanel, new RegExp(`import ${sheet}`));
		}
		for (const [sheet, fields] of [
			[paymentSheet, ['amount', 'effectiveOn', 'method']],
			[chargeSheet, ['amount', 'effectiveOn', 'dueOn', 'description']],
			[creditSheet, ['amount', 'effectiveOn', 'reason']],
			[recurringSheet, ['displayName', 'amount', 'category', 'start', 'end', 'dueDay']]
		] as const) {
			assert.match(sheet, /function validate\(\): boolean/);
			assert.match(sheet, /errors = \{\}/);
			for (const field of fields) assert.match(sheet, new RegExp(`errors\\.${field}`));
		}
	});
});
