import assert from 'node:assert/strict';
import { describe, it } from 'node:test';

import { createEmptyPropertyDraft, getPropertiesEmptyStateCopy } from './property-list-state.ts';

describe('property list state', () => {
	it('creates an active property draft that still requires an explicit rental structure', () => {
		assert.deepEqual(createEmptyPropertyDraft(), {
			name: '',
			type: 'MultiFamily',
			rentalStructure: '',
			status: 'Active',
			addressLine1: '',
			addressLine2: '',
			city: '',
			state: '',
			postalCode: '',
			ownerEntityId: '',
			yearBuilt: '',
			managementFeePercent: '',
			notes: '',
			purchasePrice: '',
			landValue: '',
			inServiceDate: '',
			manualAnnualDepreciation: '',
		});
	});

	it('uses active property filters as create-form defaults', () => {
		assert.equal(
			createEmptyPropertyDraft({ typeFilter: 'Commercial', statusFilter: 'Inactive' }).type,
			'Commercial'
		);
		assert.equal(
			createEmptyPropertyDraft({ typeFilter: 'Commercial', statusFilter: 'Inactive' }).status,
			'Inactive'
		);
	});

	it('ignores invalid filter values when creating a new draft', () => {
		const draft = createEmptyPropertyDraft({ typeFilter: 'OfficeTower', statusFilter: 'Pending' });

		assert.equal(draft.type, 'MultiFamily');
		assert.equal(draft.status, 'Active');
	});

	it('keeps first-run empty copy when no filters are active', () => {
		assert.deepEqual(getPropertiesEmptyStateCopy({ hasActiveFilters: false }), {
			message: 'No rentals yet',
			description: 'A property is one building or address. Add your first to get started.',
			actionLabel: 'Add your first property',
		});
	});

	it('shows filter-aware empty copy when filters are active', () => {
		assert.deepEqual(getPropertiesEmptyStateCopy({ hasActiveFilters: true }), {
			message: 'No properties match your filters',
			description: 'Try adjusting search or filters, or add a property that matches this view.',
			actionLabel: 'Add property',
		});
	});
});
