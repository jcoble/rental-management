import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { describe, it } from 'node:test';

const catalogSource = readFileSync(
	new URL('../../routes/(protected)/reports/+page.svelte', import.meta.url),
	'utf8'
);
const viewerSource = readFileSync(
	new URL('../../routes/(protected)/reports/[report]/+page.svelte', import.meta.url),
	'utf8'
);

const accountingLinks = [
	['accounting-profit-and-loss', '/accounting/profit-and-loss'],
	['accounting-balance-sheet', '/accounting/balance-sheet'],
	['accounting-trial-balance', '/accounting/trial-balance'],
	['accounting-general-ledger', '/accounting?tab=general-ledger'],
	['accounting-cash-flow', '/accounting?tab=cash-flow'],
] as const;

describe('reports catalog accounting links', () => {
	it('maps each real accounting report card to its accounting surface', () => {
		for (const [key, href] of accountingLinks) {
			assert.ok(catalogSource.includes(`'${key}': '${href}'`), `${key} should link from the catalog`);
			assert.ok(viewerSource.includes(`'${key}': '${href}'`), `${key} should link from the report route`);
		}
	});

	it('keeps the renamed cash-and-operating-activity report server-rendered', () => {
		assert.ok(catalogSource.includes("'cash-and-operating-activity': ScrollText"));
		assert.ok(viewerSource.includes("reportKey === 'cash-and-operating-activity'"));
	});
});
