import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { describe, it } from 'node:test';

const source = (path: string) => readFileSync(new URL(path, import.meta.url), 'utf8');

describe('staff create dialog validation clearing', () => {
	it('clears property and tenant form errors as corrected fields change', () => {
		const propertiesPage = source('../../routes/(protected)/properties/+page.svelte');
		const tenantsPage = source('../../routes/(protected)/tenants/+page.svelte');

		assert.match(propertiesPage, /function clearPropertyError\(field: string\)/);
		assert.match(propertiesPage, /clearPropertyError\('name'\)/);
		assert.match(propertiesPage, /clearPropertyError\('addressLine1'\)/);
		assert.match(propertiesPage, /clearPropertyError\('postalCode'\)/);
		assert.match(tenantsPage, /function clearTenantError\(field: string\)/);
		assert.match(tenantsPage, /clearTenantError\('firstName'\)/);
		assert.match(tenantsPage, /clearTenantError\('lastName'\)/);
		assert.match(tenantsPage, /clearTenantError\('email'\)/);
	});

	it('clears work-order, inspection, and recurring task errors as corrected fields change', () => {
		const maintenancePage = source('../../routes/(protected)/maintenance/+page.svelte');
		const recurringPage = source('../../routes/(protected)/maintenance/recurring/+page.svelte');

		assert.match(maintenancePage, /function clearWoError\(field: string\)/);
		assert.match(maintenancePage, /clearWoError\('title'\)/);
		assert.match(maintenancePage, /clearInspectionError\('scheduledFor'\)/);
		assert.match(recurringPage, /function clearRecurringError\(field: string\)/);
		assert.match(recurringPage, /clearRecurringError\('nextDueDate'\)/);
	});

	it('keeps vendor and expense clearing while tenant-money sheets own canonical validation', () => {
		const vendorsPage = source('../../routes/(protected)/vendors/+page.svelte');
		const accountingPage = source('../../routes/(protected)/accounting/+page.svelte');
		const rentTab = source('../components/unit/tabs/RentTab.svelte');
		const tenantLedgerPanel = source('../components/accounting/TenantLedgerPanel.svelte');
		const paymentSheet = source('../components/accounting/RecordPaymentSheet.svelte');
		const chargeSheet = source('../components/accounting/OneTimeChargeSheet.svelte');
		const creditSheet = source('../components/accounting/TenantCreditSheet.svelte');
		const recurringSheet = source('../components/accounting/RecurringChargeSheet.svelte');

		assert.match(vendorsPage, /function clearVendorError\(field: string\)/);
		assert.match(vendorsPage, /clearVendorError\('name'\)/);
		assert.match(vendorsPage, /clearVendorError\('serviceType'\)/);
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
