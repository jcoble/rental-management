import assert from 'node:assert/strict';
import { describe, it } from 'node:test';
import { listingClipboardText } from './listing-copy.ts';

describe('listing clipboard text', () => {
	it('lays out headline, description, rent, and deposit as plain lines', () => {
		const text = listingClipboardText({
			headline: 'Sunny two-bedroom near the park',
			description: 'Freshly painted, in-unit laundry, quiet street.',
			rent: 2125,
			securityDeposit: 2125,
		});
		assert.equal(
			text,
			'Sunny two-bedroom near the park\n\n' +
				'Freshly painted, in-unit laundry, quiet street.\n\n' +
				'Rent: $2,125 per month\n' +
				'Deposit: $2,125'
		);
	});

	it('accepts the string values the listing form holds', () => {
		const text = listingClipboardText({
			headline: '  Bright studio  ',
			description: ' Close to the bus line. ',
			rent: '1450.5',
			securityDeposit: '',
		});
		assert.equal(text, 'Bright studio\n\nClose to the bus line.\n\nRent: $1,450.50 per month');
	});

	it('leaves out anything that is empty or not a number', () => {
		assert.equal(listingClipboardText({ headline: '', description: '', rent: null, securityDeposit: null }), '');
		assert.equal(
			listingClipboardText({ headline: 'Only a headline', description: '', rent: 'abc', securityDeposit: undefined }),
			'Only a headline'
		);
	});
});
