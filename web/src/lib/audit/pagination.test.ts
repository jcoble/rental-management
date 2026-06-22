import assert from 'node:assert/strict';
import { describe, it } from 'node:test';
import { overfetchPage } from './pagination.ts';

describe('overfetchPage', () => {
	it('does not report a next page when the final page exactly matches the visible page size', () => {
		const rows = Array.from({ length: 50 }, (_, id) => id);
		const page = overfetchPage(rows, 50);

		assert.equal(page.hasNext, false);
		assert.equal(page.items.length, 50);
	});

	it('reports a next page only when the server returned the extra overfetch row', () => {
		const rows = Array.from({ length: 51 }, (_, id) => id);
		const page = overfetchPage(rows, 50);

		assert.equal(page.hasNext, true);
		assert.equal(page.items.length, 50);
		assert.equal(page.items.at(-1), 49);
	});
});
