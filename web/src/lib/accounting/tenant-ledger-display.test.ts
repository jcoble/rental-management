import assert from 'node:assert/strict';
import { describe, it } from 'node:test';
import { normalizeTenantLedgerDescription } from './tenant-ledger-display.ts';

describe('tenant-ledger description display', () => {
	it('leaves the new human seed text unchanged', () => {
		assert.equal(normalizeTenantLedgerDescription('Rent for July 2026'), 'Rent for July 2026');
		assert.equal(normalizeTenantLedgerDescription('Rent payment for July 2026'), 'Rent payment for July 2026');
	});

	it('normalizes legacy rent and payment periods only at render time', () => {
		assert.equal(normalizeTenantLedgerDescription('Rent for 2026-07'), 'Rent for July 2026');
		assert.equal(normalizeTenantLedgerDescription('Rent payment for 2026-08'), 'Rent payment for August 2026');
	});

	it('does not rewrite invalid or non-trailing period tokens', () => {
		assert.equal(normalizeTenantLedgerDescription('Rent for 2026-13'), 'Rent for 2026-13');
		assert.equal(normalizeTenantLedgerDescription('Rent for 2026-07 extra'), 'Rent for 2026-07 extra');
		assert.equal(normalizeTenantLedgerDescription('2026-07 Rent for'), '2026-07 Rent for');
		assert.equal(normalizeTenantLedgerDescription('Manual 2026-07 note'), 'Manual 2026-07 note');
	});
});
