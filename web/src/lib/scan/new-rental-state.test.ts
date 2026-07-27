import assert from 'node:assert/strict';
import { describe, it } from 'node:test';
import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { parseForm, propertySchema } from '../schemas/index.ts';
import {
	beginNewRentalDraftNavigation,
	createNewRentalPropertyForm,
	findNewRentalExistingUnitId,
	formatNewRentalStepLabel,
	formatNewRentalStepPosition,
	newRentalDraftUrl,
	parseNewRentalDraftId,
	seedNewRentalLateFeeAmount
} from './new-rental-state.ts';

describe('new rental draft URL state', () => {
	it('parses a valid resume draft id from the URL and rejects unsafe values', () => {
		assert.equal(parseNewRentalDraftId(new URLSearchParams('draftId=42')), 42);
		assert.equal(parseNewRentalDraftId(new URLSearchParams('draftId=0')), null);
		assert.equal(parseNewRentalDraftId(new URLSearchParams('draftId=-1')), null);
		assert.equal(parseNewRentalDraftId(new URLSearchParams('draftId=42x')), null);
		assert.equal(parseNewRentalDraftId(new URLSearchParams('')), null);
	});

	it('formats the stable guided-review URL used after upload', () => {
		assert.equal(newRentalDraftUrl(42), '/scan/new-rental?draftId=42');
	});

	it('keeps polling inactive until the URL adopts the uploaded draft id', () => {
		assert.deepEqual(beginNewRentalDraftNavigation(42), {
			draftId: null,
			phase: 'processing',
			url: '/scan/new-rental?draftId=42'
		});
	});
});

describe('createNewRentalPropertyForm', () => {
	it('does not infer rental structure from the extracted property type', () => {
		const form = createNewRentalPropertyForm();

		assert.equal(form.rentalStructure, '');
	});

	it('includes hidden defaults required by the shared property schema', () => {
		const form = createNewRentalPropertyForm();
		Object.assign(form, {
			name: 'Harbor View Apartments',
			type: 'MultiFamily',
			rentalStructure: 'MultiRental',
			addressLine1: '1807 Harbor View Apartments',
			city: 'Columbus',
			state: 'OH',
			postalCode: '43215'
		});

		const result = parseForm(propertySchema, form);

		assert.equal(result.errors, null);
		assert.equal(result.data?.status, 'Active');
	});
});

describe('findNewRentalExistingUnitId', () => {
	it('uses a linked proposal when that unit belongs to the selected property', () => {
		assert.equal(
			findNewRentalExistingUnitId('1A', { action: 'link', existingId: 14 }, [
				{ id: 14, unitNumber: '1A' }
			]),
			'14'
		);
	});

	it('falls back to an exact unit-number match for camera-derived leases', () => {
		assert.equal(
			findNewRentalExistingUnitId(' 1a ', null, [
				{ id: 14, unitNumber: '1A' },
				{ id: 15, unitNumber: '2B' }
			]),
			'14'
		);
	});

	it('does not guess when the proposal is outside the loaded units or the unit number is ambiguous', () => {
		assert.equal(findNewRentalExistingUnitId('1A', { action: 'link', existingId: 99 }, [
			{ id: 14, unitNumber: '1A' },
			{ id: 15, unitNumber: '1A' }
		]), '');
	});
});

describe('seedNewRentalLateFeeAmount', () => {
	it('keeps extracted late fees and defaults missing values to zero for schema validation', () => {
		assert.equal(seedNewRentalLateFeeAmount('35.00'), '35.00');
		assert.equal(seedNewRentalLateFeeAmount('  '), '0');
		assert.equal(seedNewRentalLateFeeAmount(null), '0');
	});
});

describe('new rental lease prefill seeding', () => {
	it('does not turn missing extracted bed/bath values into real zeroes', () => {
		const source = readFileSync(
			resolve(dirname(fileURLToPath(import.meta.url)), '../components/scan/LeaseFirstImport.svelte'),
			'utf8'
		);

		assert.doesNotMatch(source, /unitForm\.bedrooms\s*=\s*values\.unitBedrooms\s*\|\|\s*['"]0['"]/);
		assert.doesNotMatch(source, /unitForm\.bathrooms\s*=\s*values\.unitBathrooms\s*\|\|\s*['"]0['"]/);
	});

	it('reviews and sends square feet when the guided import creates a unit', () => {
		const source = readFileSync(
			resolve(dirname(fileURLToPath(import.meta.url)), '../components/scan/LeaseFirstImport.svelte'),
			'utf8'
		);

		assert.match(source, /unitForm\.squareFeet = values\.unitSquareFeet/);
		assert.match(source, /<UnitFields[\s\S]*testidPrefix="new-rental-unit"/);
		assert.match(source, /o\.unitSquareFeet = Number\(unitForm\.squareFeet\)/);
	});

	it('sends the explicit rental structure when the guided import creates a property', () => {
		const source = readFileSync(
			resolve(dirname(fileURLToPath(import.meta.url)), '../components/scan/LeaseFirstImport.svelte'),
			'utf8'
		);

		assert.match(source, /if \(isCreatingProperty\) \{[\s\S]*?o\.propertyId = null;[\s\S]*?o\.rentalStructure = propertyForm\.rentalStructure;/);
		assert.doesNotMatch(source, /rentalStructure\s*=\s*propertyForm\.type/);
	});

	it('requires and sends an explicit rental structure from the general lease scan', () => {
		const source = readFileSync(
			resolve(dirname(fileURLToPath(import.meta.url)), '../../routes/(protected)/scan/[draftId]/+page.svelte'),
			'utf8'
		);

		assert.match(source, /data-testid="scan-new-property-single-rental"/);
		assert.match(source, /data-testid="scan-new-property-multi-rental"/);
		assert.match(source, /!newPropertyRentalStructure/);
		assert.match(source, /if \(isCreatingLeaseProperty\) \{[\s\S]*?overrides\['propertyId'\] = null;[\s\S]*?overrides\['rentalStructure'\] = newPropertyRentalStructure;/);
		assert.doesNotMatch(source, /newPropertyRentalStructure\s*=.*propertyType/);
	});
});

describe('new rental route shell', () => {
	it('renders the reusable lease-first import component with URL resume enabled', () => {
		const source = readFileSync(
			resolve(dirname(fileURLToPath(import.meta.url)), '../../routes/(protected)/scan/new-rental/+page.svelte'),
			'utf8'
		);

		assert.match(source, /import LeaseFirstImport/);
		assert.match(source, /<LeaseFirstImport[^>]*syncDraftToUrl/s);
		assert.match(source, /parseNewRentalDraftId\(page\.url\.searchParams\)/);
	});

	it('clears a failed standalone draft from the URL before returning to capture', () => {
		const source = readFileSync(
			resolve(dirname(fileURLToPath(import.meta.url)), '../components/scan/LeaseFirstImport.svelte'),
			'utf8'
		);

		assert.match(source, /d\.status === 'Failed' \|\| d\.status === 'Rejected'/);
		assert.match(source, /if \(syncDraftToUrl\)\s*{\s*void goto\('\/scan\/new-rental'/s);
	});
});

describe('new rental progress labels', () => {
	it('uses one consistent five-step sequence including review', () => {
		const labels = ['Property', 'Unit', 'Tenant', 'Lease'];

		assert.equal(formatNewRentalStepLabel(0, labels), 'Step 1 of 5 · Property');
		assert.equal(formatNewRentalStepLabel(3, labels), 'Step 4 of 5 · Lease');
		assert.equal(formatNewRentalStepLabel(4, labels), 'Step 5 of 5 · Review & confirm');
		assert.equal(formatNewRentalStepPosition(0, labels), '1/5');
		assert.equal(formatNewRentalStepPosition(4, labels), '5/5');
	});
});
