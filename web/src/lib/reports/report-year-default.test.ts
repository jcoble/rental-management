import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { describe, it } from 'node:test';

const reportPageSource = readFileSync(
	new URL('../../routes/(protected)/reports/[report]/+page.svelte', import.meta.url),
	'utf8'
);

describe('report year default contract', () => {
	it('lets year-based reports use the API business-clock default until the user enters a year', () => {
		assert.match(reportPageSource, /let year = \$state\(''\)/);
		assert.match(reportPageSource, /year = ''/);
		assert.match(reportPageSource, /accepts\.has\('year'\) && year\.trim\(\)/);
		assert.match(reportPageSource, /Year \$\{String\(data\.year \?\? applied\.year \?\? ''\)\}/);
		assert.doesNotMatch(reportPageSource, /new Date\(\)\.getFullYear\(\)/);
	});
});
