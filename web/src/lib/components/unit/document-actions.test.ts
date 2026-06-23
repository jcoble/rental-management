import assert from 'node:assert/strict';
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
});
