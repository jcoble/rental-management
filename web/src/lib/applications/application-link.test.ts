import assert from 'node:assert/strict';
import { describe, test } from 'node:test';
import {
	buildApplicationLinkUrl,
	readRentalListingLinkContext,
	resolveApplyHomeSelection,
} from './application-link.ts';

describe('application link unit context', () => {
	test('reads the Unit Command Center list-unit action context from the URL', () => {
		const context = readRentalListingLinkContext(
			new URLSearchParams('action=list-unit&propertyId=12&unitId=34')
		);

		assert.deepEqual(context, { propertyId: 12, unitId: 34 });
	});

	test('ignores incomplete or unrelated application URL params', () => {
		assert.equal(readRentalListingLinkContext(new URLSearchParams('propertyId=12&unitId=34')), null);
		assert.equal(readRentalListingLinkContext(new URLSearchParams('action=list-unit&unitId=34')), null);
		assert.equal(readRentalListingLinkContext(new URLSearchParams('action=list-unit&propertyId=12')), null);
		assert.equal(readRentalListingLinkContext(new URLSearchParams('action=list-unit&propertyId=0&unitId=34')), null);
	});

	test('adds property and unit params to a generated public apply URL', () => {
		assert.equal(
			buildApplicationLinkUrl('https://localhost:6042', '/apply/token-123', {
				propertyId: 12,
				unitId: 34,
			}),
			'https://localhost:6042/apply/token-123?propertyId=12&unitId=34'
		);
	});

	test('preserves existing apply path query params when adding unit context', () => {
		assert.equal(
			buildApplicationLinkUrl('https://localhost:6042', '/apply/token-123?source=flyer', {
				propertyId: 12,
				unitId: 34,
			}),
			'https://localhost:6042/apply/token-123?source=flyer&propertyId=12&unitId=34'
		);
	});

	test('preselects a valid property and unit on the public application form', () => {
		const selection = resolveApplyHomeSelection(
			new URLSearchParams('propertyId=12&unitId=34'),
			[
				{ id: 12, units: [{ id: 34 }, { id: 35 }] },
				{ id: 99, units: [{ id: 100 }] },
			],
			'none'
		);

		assert.deepEqual(selection, { propertyValue: '12', unitValue: '34' });
	});

	test('falls back to no unit preference when the unit does not belong to the property', () => {
		const selection = resolveApplyHomeSelection(
			new URLSearchParams('propertyId=12&unitId=100'),
			[
				{ id: 12, units: [{ id: 34 }] },
				{ id: 99, units: [{ id: 100 }] },
			],
			'none'
		);

		assert.deepEqual(selection, { propertyValue: '12', unitValue: 'none' });
	});
});
