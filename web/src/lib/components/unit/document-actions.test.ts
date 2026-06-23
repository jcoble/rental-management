import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { describe, it } from 'node:test';
import { unitDocumentHref } from './document-actions.ts';

describe('unit document actions', () => {
	it('routes unit document rows through the stored-file proxy', () => {
		assert.equal(unitDocumentHref({ id: 11 }), '/document-file/11');
	});

	it('does not invent a direct browser URL without a stored file id', () => {
		assert.equal(unitDocumentHref({ id: 0 }), null);
		assert.equal(unitDocumentHref({ id: -1 }), null);
	});

	it('gives the Documents tab a direct unit-file uploader instead of sending generic uploads to expense scan', () => {
		const tabSource = readFileSync(
			new URL('./tabs/DocumentsTab.svelte', import.meta.url),
			'utf8'
		);
		const headerSource = readFileSync(
			new URL('./UnitHeader.svelte', import.meta.url),
			'utf8'
		);
		const pageSource = readFileSync(
			new URL('../../../routes/(protected)/units/[id]/+page.svelte', import.meta.url),
			'utf8'
		);

		assert.match(tabSource, /DocumentsPanel/);
		assert.match(tabSource, /entityType="Unit"/);
		assert.match(tabSource, /entityId=\{unitId\}/);
		assert.match(tabSource, /Scan a record/);
		assert.doesNotMatch(tabSource, /Scan \/ upload/);
		assert.match(headerSource, /Scan receipt/);
		assert.doesNotMatch(headerSource, /Scan \/ Upload/);
		assert.match(pageSource, /<DocumentsTab[^>]+unitId=\{dashboard\.unit\.id\}/s);
	});
});
