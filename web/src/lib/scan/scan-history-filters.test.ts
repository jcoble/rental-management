import assert from 'node:assert/strict';
import { describe, it } from 'node:test';
import { SCAN_HISTORY_FILTERS, formatScanHistoryEmptyMessage, resolveScanHistoryFilter } from './scan-history-filters.ts';

describe('scan history filters', () => {
	it('includes every user-facing scan lifecycle state that can appear in history', () => {
		assert.deepEqual(SCAN_HISTORY_FILTERS, [
			'All',
			'Pending',
			'Reviewing',
			'Confirmed',
			'Rejected',
			'Failed',
		]);
	});

	it('accepts supported URL filters and falls back safely for unknown values', () => {
		assert.equal(resolveScanHistoryFilter('Rejected'), 'Rejected');
		assert.equal(resolveScanHistoryFilter('Failed'), 'Failed');
		assert.equal(resolveScanHistoryFilter('Processing'), 'All');
		assert.equal(resolveScanHistoryFilter(null), 'All');
	});

	it('distinguishes filtered-empty copy from first-run empty copy', () => {
		assert.equal(formatScanHistoryEmptyMessage('All'), 'No scan drafts found.');
		assert.equal(formatScanHistoryEmptyMessage('Pending'), 'No processing scans.');
		assert.equal(formatScanHistoryEmptyMessage('Reviewing'), 'No scans ready to review.');
		assert.equal(formatScanHistoryEmptyMessage('Confirmed'), 'No confirmed scans.');
		assert.equal(formatScanHistoryEmptyMessage('Rejected'), 'No rejected scans.');
		assert.equal(formatScanHistoryEmptyMessage('Failed'), 'No failed scans.');
	});
});
