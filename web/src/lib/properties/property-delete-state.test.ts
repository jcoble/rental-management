import assert from 'node:assert/strict';
import { describe, it } from 'node:test';

import { getPropertyDeleteState } from './property-delete-state.ts';

describe('property delete state', () => {
	it('uses irreversible destructive copy when a property has no units', () => {
		assert.deepEqual(getPropertyDeleteState({ name: 'Maple Court', unitCount: 0 }), {
			message: 'Delete "Maple Court"? This cannot be undone.',
			confirmDisabled: false,
		});
	});

	it('blocks delete confirmation while a property still has live units', () => {
		assert.deepEqual(getPropertyDeleteState({ name: 'Maple Court', unitCount: 2 }), {
			message: 'Maple Court still has 2 units. Remove the units before deleting this property.',
			confirmDisabled: true,
		});
	});

	it('uses singular wording for a single remaining unit', () => {
		assert.deepEqual(getPropertyDeleteState({ name: 'Maple Court', unitCount: 1 }), {
			message: 'Maple Court still has 1 unit. Remove the unit before deleting this property.',
			confirmDisabled: true,
		});
	});
});
