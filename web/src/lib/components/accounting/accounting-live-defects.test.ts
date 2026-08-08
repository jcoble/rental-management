import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { describe, it } from 'node:test';

const source = (path: string) => readFileSync(new URL(path, import.meta.url), 'utf8');

describe('live accounting defect regressions', () => {
	it('keeps General Ledger filters visible on desktop and collapsible on mobile', () => {
		const panel = source('./GeneralLedgerPanel.svelte');
		assert.match(panel, /let filtersOpen = \$state\(false\)/);
		assert.match(panel, /aria-expanded=\{filtersOpen\}/);
		assert.match(panel, /lg:grid lg:grid-cols-12/);
		assert.doesNotMatch(panel, /<details class="group">/);
	});

	it('sends a stable idempotency key for onboarding choice', () => {
		const portfolios = source('../../api/endpoints/portfolios.ts');
		assert.match(portfolios, /idempotentMutation\(`portfolio:onboarding-choice:\$\{mode\}`/);
		assert.match(portfolios, /headers: \{ 'Idempotency-Key': operationKey \}/);
	});

	it('settles the shared journal drawer when journal detail succeeds even if chart lookup is pending', () => {
		const drawer = source('./JournalDetailDrawer.svelte');
		assert.match(drawer, /const isLoading = \$derived\(journalQuery\.isLoading\)/);
		assert.doesNotMatch(drawer, /journalQuery\.isLoading \|\| chartQuery\.isLoading/);
	});

	it('renders an explicit unavailable state for unauthorized general-ledger reads', () => {
		const panel = source('./GeneralLedgerPanel.svelte');
		assert.match(panel, /general-ledger-unavailable/);
		assert.match(panel, /General ledger not available/);
	});

	it('keeps a visible accounting-impact explanation for pre-accounting records', () => {
		const impact = source('./AccountingImpactCard.svelte');
		assert.match(impact, /accounting-impact-empty/);
		assert.match(impact, /No accounting entry — recorded before accounting was enabled/);
	});
});
