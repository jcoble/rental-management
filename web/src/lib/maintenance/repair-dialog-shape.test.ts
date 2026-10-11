import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { describe, it } from 'node:test';

const source = readFileSync(
	new URL('../../routes/(protected)/maintenance/+page.svelte', import.meta.url),
	'utf8'
);

/**
 * "Report a repair" is one screen: the four things a landlord always knows go on the
 * face of the dialog, everything else waits under a collapsed "More details" disclosure.
 * There is no photo field here — the staff dialog never had an upload (the tenant portal
 * form owns photo capture), so the essentials are property, unit, title and description.
 */
const ESSENTIAL_FIELDS = [
	'work-order-property-input',
	'work-order-unit-input',
	'work-order-title-input',
	'work-order-description-input'
];

/** Every field that used to live on a wizard step other than the essentials. */
const MORE_DETAILS_FIELDS = [
	'work-order-priority-input',
	'work-order-category-input',
	'work-order-scheduled-input',
	'work-order-window-end-input',
	'work-order-technician-access-input',
	'work-order-tenant-input',
	'work-order-vendor-input',
	'work-order-estimated-cost-input'
];

function block(startTestid: string, endMarker: string) {
	const start = source.indexOf(`data-testid="${startTestid}"`);
	assert.notEqual(start, -1, `missing data-testid="${startTestid}"`);
	const end = source.indexOf(endMarker, start);
	assert.notEqual(end, -1, `missing ${endMarker} after ${startTestid}`);
	return source.slice(start, end);
}

const essentials = () => block('repair-essentials', 'data-testid="repair-more-details"');
const moreDetails = () => block('repair-more-details', '</Dialog.Footer>');

describe('report a repair dialog shape', () => {
	it('is one screen, not a wizard', () => {
		assert.doesNotMatch(source, /const woSteps/);
		assert.doesNotMatch(source, /testid="work-order-stepper"/);
		assert.doesNotMatch(source, /data-testid="work-order-step-next"/);
		assert.doesNotMatch(source, /data-testid="work-order-step-back"/);
		assert.match(source, /Report a repair/);
		assert.match(source, /data-testid="work-order-form-save"[\s\S]{0,400}Save repair/);
	});

	it('shows exactly the essential fields on the face of the dialog', () => {
		const visible = essentials();
		for (const field of ESSENTIAL_FIELDS) {
			assert.ok(visible.includes(`testid="${field}"`), `${field} should be an essential field`);
		}
		for (const field of MORE_DETAILS_FIELDS) {
			assert.ok(!visible.includes(`"${field}"`), `${field} should not be an essential field`);
		}
	});

	it('keeps every former wizard field under More details', () => {
		const hidden = moreDetails();
		for (const field of MORE_DETAILS_FIELDS) {
			assert.ok(hidden.includes(`testid="${field}"`), `${field} should be under More details`);
		}
		assert.match(essentials(), /<span>More details<\/span>/);
	});

	it('collapses More details until the landlord opens it', () => {
		assert.match(source, /let woMoreOpen = \$state\(false\)/);
		assert.match(source, /aria-expanded=\{woMoreOpen\}/);
		assert.match(source, /data-testid="repair-more-details-toggle"/);
	});

	it('offers a link to schedule or assign after the repair is saved', () => {
		assert.match(source, /data-testid="repair-schedule-link"/);
		assert.match(source, /Schedule or assign someone/);
	});
});
