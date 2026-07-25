import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { describe, it } from 'node:test';

import {
	buildScanReviewTarget,
	contextualScanTitle,
	scanLauncherMode,
	shouldAskForScanDocumentType
} from './scan-launcher.ts';

const launcherSource = readFileSync(new URL('../components/scan/ScanLauncher.svelte', import.meta.url), 'utf8');
const unitPageSource = readFileSync(new URL('../../routes/(protected)/units/[id]/+page.svelte', import.meta.url), 'utf8');

describe('scan launcher helpers', () => {
	it('hides document-type selection when launched from a contextual page', () => {
		assert.equal(shouldAskForScanDocumentType({ type: 'Expense', unitId: 12 }), false);
		assert.equal(shouldAskForScanDocumentType({ type: 'LeaseAgreement' }), false);
		assert.equal(shouldAskForScanDocumentType({ unitId: 12 }), true);
		assert.equal(shouldAskForScanDocumentType({}), true);
	});

	it('builds review links that preserve scan context and return target', () => {
		assert.equal(
			buildScanReviewTarget(391, {
				type: 'LeaseAgreement',
				propertyId: 10,
				unitId: 20,
				leaseManagementId: 30,
				leaseAgreementId: 31,
				tenantAccountId: 40,
				tenantLedgerEntryId: 41,
				rentalListingId: 50,
				sourceLabel: 'Zillow signed lease import',
				returnTo: '/units/20?tab=tenant-lease&view=agreements'
			}),
			'/scan/391?type=LeaseAgreement&propertyId=10&unitId=20&leaseManagementId=30&leaseAgreementId=31&tenantAccountId=40&tenantLedgerEntryId=41&rentalListingId=50&sourceLabel=Zillow+signed+lease+import&returnTo=%2Funits%2F20%3Ftab%3Dtenant-lease%26view%3Dagreements'
		);
	});

	it('uses page-specific copy for contextual scan launchers', () => {
		assert.equal(contextualScanTitle({ type: 'Payment' }), 'Scan a payment');
		assert.equal(contextualScanTitle({ type: 'LeaseAgreement' }), 'Scan a lease agreement');
		assert.equal(contextualScanTitle({}), 'Scan a document');
		assert.equal(scanLauncherMode({ type: 'WorkOrder' }), 'contextual');
		assert.equal(scanLauncherMode({}), 'global');
	});

	it('shows the human rental context instead of only promising an invisible connection', () => {
		assert.match(launcherSource, /data-testid="scan-context-source"/);
		assert.match(launcherSource, /Connected to/);
		assert.match(launcherSource, /context\.sourceLabel/);
		assert.match(unitPageSource, /sourceLabel:\s*context\.sourceLabel\s*\?\?/);
		assert.match(unitPageSource, /dashboard\.propertyName/);
		assert.match(unitPageSource, /unit\.unitNumber/);
	});
});
