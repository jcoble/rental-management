import { cleanup, render } from '@testing-library/svelte';
import { afterEach, describe, expect, it } from 'vitest';
import UnitFields from '$lib/components/forms/UnitFields.svelte';

afterEach(() => cleanup());

function unitForm() {
	return {
		unitNumber: '101',
		bedrooms: '',
		bathrooms: '',
		marketRent: '1200',
		squareFeet: '',
		floorPlan: '',
		notes: ''
	};
}

describe('unit beds and baths form boundary', () => {
	it('shows residential beds and baths as required inputs and hides them for non-residential types', () => {
		for (const propertyType of ['SingleFamily', 'MultiFamily', 'Condo', 'Townhome']) {
			const residential = render(UnitFields, {
				props: {
					form: unitForm(),
					testidPrefix: propertyType.toLowerCase(),
					propertyType
				}
			});

			const bedrooms = residential.getByTestId(`${propertyType.toLowerCase()}-bedrooms-input`) as HTMLInputElement;
			const bathrooms = residential.getByTestId(`${propertyType.toLowerCase()}-bathrooms-input`) as HTMLInputElement;
			expect(bedrooms.required).toBe(true);
			expect(bathrooms.required).toBe(true);
			residential.unmount();
		}

		for (const propertyType of ['Storage', 'Parking', 'Commercial']) {
			const nonResidential = render(UnitFields, {
				props: {
					form: unitForm(),
					testidPrefix: propertyType.toLowerCase(),
					propertyType
				}
			});

			expect(nonResidential.queryByTestId(`${propertyType.toLowerCase()}-bedrooms-input`)).toBeNull();
			expect(nonResidential.queryByTestId(`${propertyType.toLowerCase()}-bathrooms-input`)).toBeNull();
			expect(nonResidential.getByTestId(`${propertyType.toLowerCase()}-rent-input`)).toBeTruthy();
			nonResidential.unmount();
		}
	});

	it('renders the required-field errors supplied by residential validation', () => {
		const view = render(UnitFields, {
			props: {
				form: unitForm(),
				propertyType: 'Townhome',
				errors: {
					bedrooms: 'Bedrooms are required.',
					bathrooms: 'Bathrooms are required.'
				}
			}
		});

		expect(view.getByText('Bedrooms are required.')).toBeTruthy();
		expect(view.getByText('Bathrooms are required.')).toBeTruthy();
	});
});
