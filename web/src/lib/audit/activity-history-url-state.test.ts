import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { describe, test } from 'node:test';

const source = readFileSync(
	new URL('../../routes/(protected)/audit/+page.svelte', import.meta.url),
	'utf8',
);

describe('Activity History URL state contract', () => {
	test('seeds every forensic filter and page from the URL', () => {
		assert.match(source, /const initialParams = page\.url\.searchParams/);
		assert.match(source, /readGridParam\(initialParams, 'q'\)/);
		assert.match(source, /readGridParam\(initialParams, 'operation'\)/);
		assert.match(source, /readGridParam\(initialParams, 'entity'\)/);
		assert.match(source, /readGridParam\(initialParams, 'from'\)/);
		assert.match(source, /readGridParam\(initialParams, 'to'\)/);
		assert.match(source, /readGridParam\(initialParams, 'page', 1\)/);
	});

	test('does not erase a restored page during the initial filter effect', () => {
		assert.match(source, /let filterResetPrimed = false/);
		assert.match(
			source,
			/if \(!filterResetPrimed\) \{\s*filterResetPrimed = true;\s*return;\s*\}/,
		);
	});

	test('mirrors the exact forensic position to the current history entry', () => {
		assert.match(source, /syncGridUrl\(/);
		for (const field of ['q', 'operation', 'entity', 'from', 'to', 'page']) {
			assert.match(source, new RegExp(`${field}:`));
		}
		assert.match(source, /\{ page: 1 \}/);
	});
});
