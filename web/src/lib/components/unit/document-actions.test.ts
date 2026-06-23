import assert from 'node:assert/strict';
import { describe, it } from 'node:test';
import { unitDocumentHref } from './document-actions.ts';

describe('unit document actions', () => {
	it('routes supported child entity documents through same-origin file proxies', () => {
		assert.equal(unitDocumentHref({ entityType: 'Lease', entityId: 11 }), '/lease-file/11');
		assert.equal(unitDocumentHref({ entityType: 'Payment', entityId: 12 }), '/payment-file/12');
		assert.equal(unitDocumentHref({ entityType: 'Expense', entityId: 13 }), '/expense-file/13');
		assert.equal(unitDocumentHref({ entityType: 'WorkOrder', entityId: 14 }), '/workorder-file/14');
		assert.equal(unitDocumentHref({ entityType: 'Application', entityId: 15 }), '/application-file/15');
		assert.equal(unitDocumentHref({ entityType: 'ScanDraft', entityId: 16 }), '/scan-file/16');
	});

	it('does not invent a direct browser URL for unit or unknown attachments', () => {
		assert.equal(unitDocumentHref({ entityType: 'Unit', entityId: 1 }), null);
		assert.equal(unitDocumentHref({ entityType: 'Other', entityId: 1 }), null);
		assert.equal(unitDocumentHref({ entityType: 'Lease' }), null);
		assert.equal(unitDocumentHref({ entityId: 1 }), null);
	});
});
