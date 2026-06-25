import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { describe, it } from 'node:test';
import {
	hasNoticeMoveOutDate,
	LEASE_DETAIL_TABS,
	resolveLeaseDetailTab,
	scannedLeaseDocumentLinkLabel,
	tabForLeaseEdit,
} from './lease-detail-state.ts';

describe('lease detail tab routing', () => {
	it('accepts every supported lease detail tab from the URL', () => {
		for (const tab of LEASE_DETAIL_TABS) {
			assert.equal(resolveLeaseDetailTab(tab), tab);
		}
	});

	it('falls back to overview for missing or invalid tab query values', () => {
		assert.equal(resolveLeaseDetailTab(null), 'overview');
		assert.equal(resolveLeaseDetailTab(''), 'overview');
		assert.equal(resolveLeaseDetailTab('unknown'), 'overview');
	});
});

describe('lease detail edit state', () => {
	it('keeps the overview tab when editing starts from overview', () => {
		assert.equal(tabForLeaseEdit('overview'), 'overview');
	});

	it('moves to overview when editing starts from a non-editable tab', () => {
		assert.equal(tabForLeaseEdit('history'), 'overview');
		assert.equal(tabForLeaseEdit('ledger'), 'overview');
		assert.equal(tabForLeaseEdit('agreement'), 'overview');
	});
});

describe('scannedLeaseDocumentLinkLabel', () => {
	it('does not assume every scanned lease source was uploaded as a PDF', () => {
		assert.equal(scannedLeaseDocumentLinkLabel(true), 'Open full size');
		assert.equal(scannedLeaseDocumentLinkLabel(false), 'View scanned document');
	});
});

describe('hasNoticeMoveOutDate', () => {
	it('requires a non-blank move-out date before giving notice', () => {
		assert.equal(hasNoticeMoveOutDate(''), false);
		assert.equal(hasNoticeMoveOutDate('   '), false);
		assert.equal(hasNoticeMoveOutDate(null), false);
		assert.equal(hasNoticeMoveOutDate('2026-07-15'), true);
	});
});

describe('lease detail hero CTA', () => {
	it('opens the ledger through the tab router so URL sync cannot reset it', () => {
		const pageSource = readFileSync(
			new URL('../../routes/(protected)/leases/[id]/+page.svelte', import.meta.url),
			'utf8'
		);

		assert.match(pageSource, /data-testid="lease-hero-cta"[\s\S]*onclick=\{\(\) => setTab\('ledger'\)\}/);
		assert.doesNotMatch(pageSource, /data-testid="lease-hero-cta"[\s\S]*onclick=\{\(\) => activeTab = 'ledger'\}/);
	});
});
