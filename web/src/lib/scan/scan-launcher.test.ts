import assert from 'node:assert/strict';
import { describe, it } from 'node:test';

import {
	buildScanReviewTarget,
	contextualScanTitle,
	scanLauncherMode,
	shouldAskForScanDocumentType
} from './scan-launcher.ts';

describe('scan launcher helpers', () => {
	it('hides document-type selection when launched from a contextual page', () => {
		assert.equal(shouldAskForScanDocumentType({ type: 'Expense', unitId: 12 }), false);
		assert.equal(shouldAskForScanDocumentType({ type: 'Lease' }), false);
		assert.equal(shouldAskForScanDocumentType({ unitId: 12 }), true);
		assert.equal(shouldAskForScanDocumentType({}), true);
	});

	it('builds review links that preserve scan context and return target', () => {
		assert.equal(
			buildScanReviewTarget(391, {
				type: 'Lease',
				propertyId: 10,
				unitId: 20,
				returnTo: '/units/20?tab=lease'
			}),
			'/scan/391?type=Lease&propertyId=10&unitId=20&returnTo=%2Funits%2F20%3Ftab%3Dlease'
		);
	});

	it('uses page-specific copy for contextual scan launchers', () => {
		assert.equal(contextualScanTitle({ type: 'Payment' }), 'Scan a payment');
		assert.equal(contextualScanTitle({ type: 'Lease' }), 'Scan a lease agreement');
		assert.equal(contextualScanTitle({}), 'Scan a document');
		assert.equal(scanLauncherMode({ type: 'WorkOrder' }), 'contextual');
		assert.equal(scanLauncherMode({}), 'global');
	});
});
