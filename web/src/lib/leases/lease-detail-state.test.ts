import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { describe, it } from 'node:test';
import {
	canSetLeaseActive,
	hasNoticeMoveOutDate,
	LEASE_DETAIL_TABS,
	leaseStatusOptionsForCurrentStatus,
	quickLeaseStatusActionsForStatus,
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

describe('lease detail lifecycle actions', () => {
	it('only offers Set Active for statuses that can transition to Active', () => {
		assert.equal(canSetLeaseActive('Draft'), true);
		assert.equal(canSetLeaseActive('PendingSignature'), true);
		assert.equal(canSetLeaseActive('NoticeGiven'), true);

		assert.equal(canSetLeaseActive('Active'), false);
		assert.equal(canSetLeaseActive('Expired'), false);
		assert.equal(canSetLeaseActive('Terminated'), false);
		assert.equal(canSetLeaseActive('Void'), false);
		assert.equal(canSetLeaseActive(null), false);
	});

	it('does not let terminal leases be edited back to Active', () => {
		const editableStatuses = ['Draft', 'Active', 'NoticeGiven', 'Expired', 'Terminated'];

		assert.deepEqual(leaseStatusOptionsForCurrentStatus('Expired', editableStatuses), ['Expired']);
		assert.deepEqual(leaseStatusOptionsForCurrentStatus('Terminated', editableStatuses), ['Terminated']);
		assert.deepEqual(leaseStatusOptionsForCurrentStatus('Void', editableStatuses), []);
	});

	it('keeps editable status choices aligned with the backend transition graph', () => {
		const editableStatuses = ['Draft', 'Active', 'NoticeGiven', 'Expired', 'Terminated'];

		assert.deepEqual(leaseStatusOptionsForCurrentStatus('Draft', editableStatuses), ['Draft', 'Active']);
		assert.deepEqual(leaseStatusOptionsForCurrentStatus('Active', editableStatuses), [
			'Active',
			'NoticeGiven',
			'Expired',
			'Terminated',
		]);
		assert.deepEqual(leaseStatusOptionsForCurrentStatus('NoticeGiven', editableStatuses), [
			'Active',
			'NoticeGiven',
			'Expired',
			'Terminated',
		]);
	});

	it('offers quick status actions only for supported Active, Expired, and Terminated transitions', () => {
		const targets = (status: string | null) =>
			quickLeaseStatusActionsForStatus(status).map((action) => action.targetStatus);

		assert.deepEqual(targets('Draft'), ['Active']);
		assert.deepEqual(targets('PendingSignature'), ['Active']);
		assert.deepEqual(targets('Active'), ['Expired', 'Terminated']);
		assert.deepEqual(targets('NoticeGiven'), ['Active', 'Expired', 'Terminated']);
		assert.deepEqual(targets('Expired'), []);
		assert.deepEqual(targets('Terminated'), []);
		assert.deepEqual(targets(null), []);
	});
});

describe('lease detail hero CTA', () => {
	it('opens the ledger through the tab router so URL sync cannot reset it', () => {
		// The lease detail body was folded into the reusable LeaseDetail component (TSK-457);
		// the hero-CTA wiring it guards now lives there, not in the thin [id] route wrapper.
		const pageSource = readFileSync(
			new URL('../components/records/LeaseDetail.svelte', import.meta.url),
			'utf8'
		);

		assert.match(pageSource, /data-testid="lease-hero-cta"[\s\S]*onclick=\{\(\) => setTab\('ledger'\)\}/);
		assert.doesNotMatch(pageSource, /data-testid="lease-hero-cta"[\s\S]*onclick=\{\(\) => activeTab = 'ledger'\}/);
	});
});

describe('lease detail status mutation invalidation', () => {
	it('refreshes lease, unit, dashboard, and lease-list caches after lifecycle status changes', () => {
		const pageSource = readFileSync(
			new URL('../components/records/LeaseDetail.svelte', import.meta.url),
			'utf8'
		);

		assert.match(pageSource, /queryKey:\s*\['lease',\s*leaseId\]/);
		assert.match(pageSource, /queryKey:\s*\['leases',\s*portfolioId\]/);
		assert.match(pageSource, /queryKey:\s*\['unit-dashboard',\s*unitId\]/);
		assert.match(pageSource, /queryKey:\s*\['unit-leases',\s*portfolioId,\s*unitId\]/);
		assert.match(pageSource, /queryKey:\s*\['dashboard',\s*portfolioId\]/);
	});
});

describe('lease agreement original source document', () => {
	it('shows the uploaded source lease in Agreement & Signing without using generated document actions', () => {
		const pageSource = readFileSync(
			new URL('../components/records/LeaseDetail.svelte', import.meta.url),
			'utf8'
		);
		const sourceDocumentCard = pageSource.match(
			/<DetailCard title="Original source lease agreement"[\s\S]*?<\/DetailCard>/
		)?.[0];

		assert.ok(sourceDocumentCard, 'expected an Agreement tab card for the original source lease');
		assert.match(sourceDocumentCard, /testid="lease-original-source-document-card"/);
		assert.match(sourceDocumentCard, /legal record/);
		assert.match(sourceDocumentCard, /href="\/lease-file\/\{lease\.id\}"/);
		assert.doesNotMatch(sourceDocumentCard, /generateDocMutation|downloadDocument|downloadSignedDocument/);
	});
});
