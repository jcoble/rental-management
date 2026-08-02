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
});
