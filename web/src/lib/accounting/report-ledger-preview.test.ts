import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { describe, it } from 'node:test';

const source = (path: string) => readFileSync(new URL(path, import.meta.url), 'utf8');

describe('accounting report ledger preview', () => {
	it('uses server-provided preview rows and total count', () => {
		const page = source('../../routes/(protected)/accounting/+page.svelte');
		const types = source('../types/index.ts');

		assert.match(types, /ledgerTotalCount: number;/);
		assert.match(types, /recentLedger: LedgerTransaction\[\];/);
		assert.match(page, /const recentLedger = \$derived\(reports\?\.recentLedger \?\? reports\?\.ledger \?\? \[\]\);/);
		assert.match(page, /const ledgerTotalCount = \$derived\(reports\?\.ledgerTotalCount \?\? reports\?\.ledger\.length \?\? 0\);/);
		assert.doesNotMatch(page, /\.ledger\s*\?\?\s*\[\]\)\.slice\(/);
		assert.doesNotMatch(page, /<p[^>]*>\{reports\?\.ledger\.length/);
	});
});
