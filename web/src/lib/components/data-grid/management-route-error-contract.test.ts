import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { describe, it } from 'node:test';

const managementRoutes = [
	['properties', 'src/routes/(protected)/properties/+page.svelte'],
	['owners', 'src/routes/(protected)/owners/+page.svelte'],
	['units', 'src/routes/(protected)/units/+page.svelte'],
	['tenants', 'src/routes/(protected)/tenants/+page.svelte'],
	['leases', 'src/routes/(protected)/leases/+page.svelte'],
	['applications', 'src/routes/(protected)/applications/+page.svelte'],
] as const;

const sources = Object.fromEntries(
	managementRoutes.map(([name, path]) => [name, readFileSync(path, 'utf8')])
);
const tenantLedgerPanel = readFileSync(
	'src/lib/components/accounting/TenantLedgerPanel.svelte',
	'utf8'
);
const paymentDetail = readFileSync(
	'src/lib/components/records/PaymentDetail.svelte',
	'utf8'
);

describe('management route error contract', () => {
	it('distinguishes retryable error', () => {
		for (const [name] of managementRoutes) {
			const source = sources[name];
			assert.match(source, new RegExp(`data-testid="${name}-list-error"`));
			assert.match(source, /isError/);
			assert.match(source, /refetch\(\)/);
			assert.ok(
				source.indexOf('isError') < source.indexOf('<DataGrid'),
				`${name} must branch on request failure before rendering the empty grid`
			);
		}

		assert.match(tenantLedgerPanel, /data-testid="tenant-ledger-error"/);
		assert.match(tenantLedgerPanel, /coreReadError/);
		assert.match(tenantLedgerPanel, /retryReads/);
		assert.ok(
			tenantLedgerPanel.indexOf('{:else if coreReadError}') < tenantLedgerPanel.indexOf('data-testid="tenant-ledger-months"'),
			'canonical tenant-ledger errors must render before month rows'
		);
		assert.match(paymentDetail, /data-testid="payment-detail-error"/);
		assert.match(paymentDetail, /paymentQuery\.isError/);
		assert.match(paymentDetail, /paymentQuery\.refetch\(\)/);
	});

	it('shows empty', () => {
		assert.match(
			sources.properties,
			/emptyMessage=\{emptyStateCopy\.message\}/
		);
		assert.match(sources.units, /emptyMessage=\{emptyStateCopy\.message\}/);
		assert.match(sources.tenants, /emptyMessage=\{emptyCopy\.message\}/);
		assert.match(sources.owners, /emptyMessage=\{ownerEmptyMessage\}/);
		assert.match(sources.leases, /emptyMessage=\{leaseEmptyMessage\}/);
		assert.match(sources.applications, /\{emptyMessage\}/);
		assert.match(tenantLedgerPanel, /data-testid="tenant-ledger-empty"/);
		assert.match(tenantLedgerPanel, /No charges or payments yet\. Record the first payment or charge above\./);
		assert.match(paymentDetail, /data-testid="payment-detail-not-found"/);
	});

	it('shows no results', () => {
		assert.match(sources.properties, /hasActiveFilters/);
		assert.match(sources.units, /hasActiveFilters/);
		assert.match(sources.tenants, /hasActiveFilters/);
		assert.match(sources.applications, /formatApplicationsEmptyMessage/);
		assert.match(sources.owners, /hasOwnerSearch/);
		assert.match(sources.leases, /hasLeaseFilters/);

		for (const [name] of managementRoutes) {
			assert.match(
				sources[name],
				/serverSide/,
				`${name} must keep no-result evaluation on its server-paged grid`
			);
		}
	});

	it('keeps the global Leases page on the protected list-page spacing contract', () => {
		assert.match(
			sources.leases,
			/<div class="box-border h-full overflow-y-auto p-6 pb-20" data-testid="leases-page">/
		);
		assert.match(sources.leases, /<PageHeader[\s\S]*class="mb-4"[\s\S]*data-testid="leases-header"/);
	});
});
