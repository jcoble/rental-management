import assert from 'node:assert/strict';
import { describe, it } from 'node:test';
import { readFileSync } from 'node:fs';
const source = readFileSync(new URL('./PortfolioLeaseLedgerPanel.svelte', import.meta.url), 'utf8');
describe('portfolio lease ledger contract', () => {
	it('sends property, unit, tenant, type, sort, and paging to the server', () => {
		for (const field of ['propertyId:', 'unitId:', 'search:', 'entryType:', 'skip:', 'take:', "sort: '-effectiveOn'"]) assert.ok(source.includes(field));
	});
	it('renders server rows as month groups with links to unit money', () => {
		assert.match(source, /portfolio-ledger-month-/); assert.match(source, /\/units\/\$\{row\.unitId\}\?tab=rent/);
	});
});
