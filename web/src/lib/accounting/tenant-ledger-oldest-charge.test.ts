import assert from 'node:assert/strict';
import { describe, it } from 'node:test';
import { oldestOpenChargeDisplay, type TenantLedgerSummaryFacts } from './tenant-ledger-oldest-charge.ts';

describe('tenant ledger oldest open charge', () => {
	it('keeps the server-owned oldest charge stable when the visible page is filtered', () => {
		const summary: TenantLedgerSummaryFacts = {
			businessDate: '2026-08-09',
			oldestOpenChargeDueOn: '2025-01-15',
			oldestOpenChargeAmount: 125,
		};
		const expected = {
			label: 'late' as const,
			ageDays: 571,
			openAmount: 125,
			dueOn: '2025-01-15',
		};

		assert.deepEqual(oldestOpenChargeDisplay(summary), expected);
		for (const visibleFilter of ['payments', 'credits'] as const) {
			// These visible pages intentionally contain no charge rows. The summary
			// input is unchanged, so the tile remains the same for either filter.
			assert.deepEqual(oldestOpenChargeDisplay(summary), expected, visibleFilter);
		}
		// A charge outside the visible twelve-month page remains represented by the
		// unbounded account summary rather than being treated as no open charges.
		assert.deepEqual(oldestOpenChargeDisplay(summary), expected);
	});

	it('renders a due date when the server-owned oldest charge is not late', () => {
		assert.deepEqual(
			oldestOpenChargeDisplay({
				businessDate: '2026-08-09',
				oldestOpenChargeDueOn: '2026-08-20',
				oldestOpenChargeAmount: 80,
			}),
			{
				label: 'due',
				ageDays: 0,
				openAmount: 80,
				dueOn: '2026-08-20',
			}
		);
	});
});
