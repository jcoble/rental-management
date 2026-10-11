import assert from 'node:assert/strict';
import { describe, it } from 'node:test';

import {
	buildScanReviewFieldGroups,
	buildScanReviewInitialEditedFields,
	fieldDisplayLabel,
	resolveScanReviewPropertyId,
	scanCategoryLabel,
	scanCategoryValue,
	scanCategoryOptionsForTarget,
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
				field('category', 'Plumbing'),
				field('notes', 'Photo attached.'),
				field('transcript', 'Tenant said the door lock sticks.')
			],
			'WorkOrder'
		);

		assert.deepEqual(groups.map((group) => group.label), ['Work order', 'Notes']);
		assert.deepEqual(
			groups.flatMap((group) => group.fields.map((workOrderField) => workOrderField.name)),
			['title', 'priority', 'description', 'category', 'notes']
		);
	});

	it('uses maintenance categories for work-order scans instead of Schedule E expense categories', () => {
		const workOrderCategories = scanCategoryOptionsForTarget('WorkOrder').map((option) => option.value);
		const expenseCategories = scanCategoryOptionsForTarget('Expense').map((option) => option.value);

		assert.ok(workOrderCategories.includes('Plumbing'));
		assert.ok(workOrderCategories.includes('HVAC'));
		assert.equal(workOrderCategories.includes('MortgageInterest'), false);
		assert.ok(expenseCategories.includes('MortgageInterest'));
		assert.equal(scanCategoryLabel('Plumbing', 'WorkOrder'), 'Plumbing');
		assert.equal(scanCategoryLabel('MortgageInterest', 'Expense'), 'Mortgage interest');
	});

	it('normalizes extracted category labels to submitted enum values', () => {
		assert.equal(scanCategoryValue('Repairs & maintenance', 'Expense'), 'Repairs');
		assert.equal(scanCategoryValue('repairs and maintenance', 'Expense'), 'Repairs');
		assert.equal(scanCategoryValue('hvac', 'WorkOrder'), 'HVAC');
		assert.equal(scanCategoryValue('Unknown bucket', 'Expense'), 'Unknown bucket');
	});

	it('builds initial edited fields with normalized category values and no line-items scalar', () => {
		assert.deepEqual(
			buildScanReviewInitialEditedFields(
				[field('category', 'General'), field('title', 'Front door lock sticks'), field('line_items', '[]')],
				'WorkOrder'
			),
			{ category: 'General', title: 'Front door lock sticks' }
		);
		assert.deepEqual(
			buildScanReviewInitialEditedFields([field('category', 'Repairs & maintenance')], 'Expense'),
			{ category: 'Repairs' }
		);
	});

	it('resolves scan-review property selection from context, id, or one exact property-name match', () => {
		const properties = [
			{ id: 10, name: 'Cedar Point Flats' },
			{ id: 11, name: 'Summit Row' }
		];

		assert.equal(resolveScanReviewPropertyId({ contextPropertyId: 11, fields: [], properties }), '11');
		assert.equal(resolveScanReviewPropertyId({ fields: [field('property_id', '10')], properties }), '10');
		assert.equal(
			resolveScanReviewPropertyId({
				fields: [field('property_name', 'Cedar Point Flats')],
				properties
			}),
			'10'
		);
		assert.equal(
			resolveScanReviewPropertyId({
				fields: [field('notes', 'Property: Cedar Point Flats, Unit 1A. Lease reference QA-2026-001.')],
				properties
			}),
			'10'
		);
	});

	it('does not resolve ambiguous or partial property-name matches', () => {
		const properties = [
			{ id: 10, name: 'Cedar Point Flats' },
			{ id: 12, name: 'Cedar Point Flats - Garage' }
		];

		assert.equal(resolveScanReviewPropertyId({ fields: [field('property_name', 'Cedar Point')], properties }), null);
		assert.equal(resolveScanReviewPropertyId({ fields: [field('notes', 'Property: Cedar Point, Unit 1A')], properties }), null);
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
				'total',
				'subtotal',
				'tax',
				'payment_method',
				'category',
				'transaction_date'
			]
		);
	});

	it('puts the review total before component amounts for scanned expenses', () => {
		const groups = buildScanReviewFieldGroups(
			[
				field('subtotal', '256.00'),
				field('tax', '19.20'),
				field('tax_rate', '0.075'),
				field('tip', ''),
				field('discount', ''),
				field('shipping', ''),
				field('total', '275.20'),
				field('payment_method', 'Cash'),
				field('card_last4', ''),
				field('due_date', '')
			],
			'Expense'
		);

		const amountGroup = groups.find((group) => group.label === 'Amounts');
		assert.deepEqual(
			amountGroup?.fields.map((reviewField) => reviewField.name),
			['total', 'subtotal', 'tax', 'tax_rate', 'tip', 'discount', 'shipping', 'payment_method', 'due_date', 'card_last4']
		);
	});

	it('formats extracted field names into stable display labels', () => {
		assert.equal(fieldDisplayLabel('vendor_name'), 'Vendor name');
		assert.equal(fieldDisplayLabel('vendor_tax_id'), 'Vendor tax ID');
		assert.equal(fieldDisplayLabel('custom_reference'), 'Custom Reference');
	});
});
