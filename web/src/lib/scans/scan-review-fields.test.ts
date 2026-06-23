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

	it('shows payment scans as payment details instead of empty expense/vendor fields', () => {
		const groups = buildScanReviewFieldGroups(
			[
				field('vendor_name', ''),
				field('receipt_number', ''),
				field('subtotal', ''),
				field('tax', ''),
				field('total', '1125.00'),
				field('payment_method', 'Check'),
				field('card_last4', ''),
				field('category', ''),
				field('document_kind', 'RentCheck'),
				field('notes', 'Rent for lease QA-2026-001-1A'),
				field('bank_name', 'First QA Bank'),
				field('payer_name', 'Avery Ellis'),
				field('check_number', '8001'),
				field('transaction_date', '2026-02-03')
			],
			'Payment'
		);

		assert.deepEqual(groups.map((group) => group.label), ['Payment', 'Details']);
		assert.deepEqual(
			groups.flatMap((group) => group.fields.map((reviewField) => reviewField.name)),
			['total', 'payment_method', 'payer_name', 'bank_name', 'check_number', 'transaction_date', 'document_kind', 'notes']
		);
	});

	it('shows expense scans without payment-only bank and check fields', () => {
		const groups = buildScanReviewFieldGroups(
			[
				field('vendor_name', 'Green Thumb Landscaping'),
				field('receipt_number', 'R-1001'),
				field('subtotal', '60.00'),
				field('tax', '3.75'),
				field('total', '63.75'),
				field('payment_method', 'Card'),
				field('category', 'RepairsAndMaintenance'),
				field('transaction_date', '2026-02-02'),
				field('bank_name', 'First QA Bank'),
				field('payer_name', 'Avery Ellis'),
				field('check_number', '8001')
			],
			'Expense'
		);

		assert.deepEqual(groups.map((group) => group.label), ['Vendor', 'Amounts', 'Details']);
		assert.deepEqual(
			groups.flatMap((group) => group.fields.map((reviewField) => reviewField.name)),
			[
				'vendor_name',
				'receipt_number',
				'subtotal',
				'tax',
				'total',
				'payment_method',
				'category',
				'transaction_date'
			]
		);
	});

	it('formats extracted field names into stable display labels', () => {
		assert.equal(fieldDisplayLabel('vendor_name'), 'Vendor name');
		assert.equal(fieldDisplayLabel('vendor_tax_id'), 'Vendor tax ID');
		assert.equal(fieldDisplayLabel('custom_reference'), 'Custom Reference');
	});
});
