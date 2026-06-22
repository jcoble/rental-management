import assert from 'node:assert/strict';
import { describe, it } from 'node:test';
import { formatStatusLabel, formatAuditChangeValue } from './status-labels.ts';

describe('status display labels', () => {
	it('formats compound enum tokens as user-facing labels', () => {
		assert.equal(formatStatusLabel('NoticeGiven'), 'Notice given');
		assert.equal(formatStatusLabel('PendingSignature'), 'Pending signature');
		assert.equal(formatStatusLabel('InProgress'), 'In progress');
	});

	it('leaves simple status names readable', () => {
		assert.equal(formatStatusLabel('Active'), 'Active');
		assert.equal(formatStatusLabel('Paid'), 'Paid');
	});

	it('formats status audit diffs without leaking enum tokens', () => {
		assert.equal(formatAuditChangeValue('Status', 'NoticeGiven'), 'Notice given');
		assert.equal(formatAuditChangeValue('Lease Status', 'PendingSignature'), 'Pending signature');
	});

	it('does not rewrite non-status audit fields', () => {
		assert.equal(formatAuditChangeValue('Lease Number', 'QA-2026-002-2B'), 'QA-2026-002-2B');
	});
});
