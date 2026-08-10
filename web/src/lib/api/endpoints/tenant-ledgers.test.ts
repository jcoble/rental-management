import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { describe, it } from 'node:test';

const source = readFileSync(new URL('./tenant-ledgers.ts', import.meta.url), 'utf8');

describe('tenant ledger API contract', () => {
	it('constructs ledger, month-summary, period-summary, and recurring-charge queries', () => {
		assert.match(source, /`\/tenant-accounts\/\$\{tenantAccountId\}\/ledger/);
		assert.match(source, /entryType: params\.entryType/);
		assert.match(source, /effectiveFrom: params\.effectiveFrom/);
		assert.match(source, /effectiveTo: params\.effectiveTo/);
		assert.match(source, /openOnly: params\.openOnly/);
		assert.match(source, /settledOnly: params\.settledOnly/);
		assert.match(source, /export type TenantLedgerSort =/);
		for (const sort of ['effectiveOn', '-effectiveOn', 'oldestDueOn', 'postedAtUtc', '-postedAtUtc']) {
			assert.match(source, new RegExp(`['"]${sort}['"]`));
		}
		assert.match(source, /`\/tenant-accounts\/\$\{tenantAccountId\}\/month-summary/);
		assert.match(source, /`\/tenant-accounts\/\$\{tenantAccountId\}\/ledger-summary/);
		assert.match(source, /months: TenantLedgerSummaryMonths = 12/);
		assert.match(source, /`\/tenant-accounts\/\$\{tenantAccountId\}\/credit-targets/);
		assert.match(source, /targetEntryId: params\.targetEntryId/);
		assert.match(source, /creditTargets: \(tenantAccountId: number, params\?: TenantCreditTargetParams\)/);
		assert.match(source, /`\/tenant-accounts\/\$\{tenantAccountId\}\/recurring-charges/);
	});

	it('binds the five parallel tenant-ledger relationship/category/service-period fields', () => {
		for (const field of [
			'relatedTenantLedgerEntryId',
			'relatedEntryDescription',
			'categoryName',
			'servicePeriodStartOn',
			'servicePeriodEndOn'
		]) {
			assert.match(source, new RegExp(`\\b${field}\\b`));
		}
		assert.match(
			source,
			/createRecurringCharge:[\s\S]*api\.post<RecurringTenantChargeRow>[\s\S]*body,[\s\S]*mutationOptions\(operationKey\)/
		);
		assert.match(
			source,
			/patchRecurringCharge:[\s\S]*api\.patch<RecurringTenantChargeRow>[\s\S]*body,[\s\S]*mutationOptions\(operationKey\)/
		);
		assert.match(
			source,
			/deactivateRecurringCharge:[\s\S]*api\.post<RecurringTenantChargeRow>[\s\S]*undefined,[\s\S]*mutationOptions\(operationKey\)/
		);
		assert.match(source, /'Idempotency-Key': operationKey/);
	});
});
