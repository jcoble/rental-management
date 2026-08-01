import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { describe, it } from 'node:test';

const bankingPage = readFileSync(
	new URL('../../routes/(protected)/banking/+page.svelte', import.meta.url),
	'utf8',
);

describe('banking import toast copy', () => {
	it('describes first imports and exact replays without implying duplicate rows were added', () => {
		assert.match(
			bankingPage,
			/Processed \$\{result\.importedCount\} statement transaction/,
		);
		assert.match(bankingPage, /existing provider IDs are never duplicated/);
		assert.doesNotMatch(bankingPage, /Imported \$\{result\.importedCount\} bank transaction/);
	});
});
