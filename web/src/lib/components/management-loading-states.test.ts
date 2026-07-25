import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { describe, test } from 'node:test';

const read = (path: string) => readFileSync(new URL(path, import.meta.url), 'utf8');

const paymentDetail = read('./records/PaymentDetail.svelte');
const expenseDetail = read('./records/ExpenseDetail.svelte');
const workOrderDetail = read('./records/WorkOrderDetail.svelte');
const aiProvider = read('../../routes/(protected)/settings/integrations/ai/+page.svelte');
const myAlerts = read('../../routes/(protected)/settings/notifications/my-alerts/+page.svelte');
const teamRouting = read('../../routes/(protected)/settings/notifications/team-routing/+page.svelte');
const tenantNotices = read('../../routes/(protected)/settings/notifications/tenant-notices/+page.svelte');
const tax = read('../../routes/(protected)/tax/+page.svelte');
const accounting = read('../../routes/(protected)/accounting/+page.svelte');
const pastDue = read('../../routes/(protected)/accounting/past-due/+page.svelte');
const banking = read('../../routes/(protected)/banking/+page.svelte');
const deposits = read('../../routes/(protected)/deposits/+page.svelte');
const ownerReports = read('../../routes/(protected)/owners-report/+page.svelte');

describe('management loading-state contract', () => {
	test('record details use the shared skeleton and expose a retryable query failure', () => {
		for (const source of [paymentDetail, expenseDetail, workOrderDetail]) {
			assert.match(source, /import LoadingState from '\$lib\/components\/shared\/LoadingState\.svelte'/);
			assert.match(source, /<LoadingState label="Loading [^"]+"/);
			assert.match(source, /\.isError/);
			assert.match(source, /\.refetch\(\)/);
		}

		assert.doesNotMatch(paymentDetail, />Loading receipt\.\.\.<\/div>/);
		assert.doesNotMatch(expenseDetail, />Loading expense\.\.\.<\/div>/);
		assert.doesNotMatch(workOrderDetail, />Loading…<\/p>/);
	});

	test('notification and AI settings replace primary plain-text loaders', () => {
		for (const source of [aiProvider, myAlerts, teamRouting, tenantNotices]) {
			assert.match(source, /import LoadingState from '\$lib\/components\/shared\/LoadingState\.svelte'/);
			assert.match(source, /<LoadingState label="Loading [^"]+"/);
		}

		assert.match(teamRouting, /Retry recipients/);
		assert.match(teamRouting, /Retry search/);
		assert.match(teamRouting, /team-routing-preview-loading/);
		assert.match(tenantNotices, /tenant-notice-deliveries-loading/);
		assert.doesNotMatch(aiProvider, />Loading provider status…<\/p>/);
		assert.doesNotMatch(myAlerts, />Loading your alert destinations…<\/Card\.Content>/);
	});

	test('money operations use stable loaders and useful retry actions', () => {
		for (const source of [tax, accounting, pastDue, banking, ownerReports]) {
			assert.match(source, /import LoadingState from '\$lib\/components\/shared\/LoadingState\.svelte'/);
			assert.match(source, /<LoadingState label="Loading [^"]+"/);
		}

		assert.match(tax, /Retry checklist/);
		assert.match(tax, /Retry tax summary/);
		assert.match(accounting, /Retry money summary/);
		assert.match(accounting, /accounting-reports-loading/);
		assert.match(pastDue, /Retry past-due accounts/);
		assert.match(banking, /Retry matches/);
		assert.match(banking, /Retry transactions/);
		assert.match(deposits, /loading=\{depositsQuery\.isLoading\}/);
		assert.doesNotMatch(deposits, /loading=\{depositsQuery\.isLoading \|\| depositsQuery\.isFetching\}/);
		assert.match(ownerReports, /Retry owner reports/);
		assert.match(ownerReports, /Retry statement/);
		assert.match(ownerReports, /Retry distributions/);
	});
});
