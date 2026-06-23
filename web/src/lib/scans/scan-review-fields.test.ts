import assert from 'node:assert/strict';
import { describe, it } from 'node:test';

import {
	buildScanReviewFieldGroups,
	fieldDisplayLabel,
	shouldShowScanReviewField,
	type ScanReviewField
} from './scan-review-fields.ts';

function field(name: string, value = 'x'): ScanReviewField {
	return { name, value, confidence: 0.95 };
}

describe('scan review field grouping', () => {
	it('shows a landlord-friendly work order review without editable internal linkage fields', () => {
		const groups = buildScanReviewFieldGroups(
			[
				field('title', 'Front door lock sticks'),
				field('unit_id', '1'),
				field('lease_id', ''),
				field('tenant_id', '1'),
				field('vendor_id', ''),
				field('property_id', '1'),
				field('target_entity_type', 'WorkOrder'),
				field('priority', 'Normal'),
				field('description', 'Tenant reports the lock sticks.'),
				field('notes', 'Photo attached.'),
				field('transcript', 'Tenant said the door lock sticks.')
			],
			'WorkOrder'
		);

		assert.deepEqual(groups.map((group) => group.label), ['Work order', 'Notes']);
		assert.deepEqual(
			groups.flatMap((group) => group.fields.map((workOrderField) => workOrderField.name)),
			['title', 'priority', 'description', 'notes']
		);
	});

	it('keeps linkage ids out of generic payment and expense review groups too', () => {
		const groups = buildScanReviewFieldGroups(
			[
				field('vendor_name', 'Green Thumb Landscaping'),
				field('total', '63.75'),
				field('property_id', '1'),
				field('target_entity_type', 'Expense')
			],
			'Expense'
		);

		assert.deepEqual(
			groups.flatMap((group) => group.fields.map((reviewField) => reviewField.name)),
			['vendor_name', 'total']
		);
		assert.equal(shouldShowScanReviewField('leaseId', 'Payment'), false);
	});

	it('formats extracted field names into stable display labels', () => {
		assert.equal(fieldDisplayLabel('vendor_name'), 'Vendor name');
		assert.equal(fieldDisplayLabel('vendor_tax_id'), 'Vendor tax ID');
		assert.equal(fieldDisplayLabel('custom_reference'), 'Custom Reference');
	});
});
