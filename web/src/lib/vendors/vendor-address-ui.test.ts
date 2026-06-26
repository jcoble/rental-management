import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { describe, it } from 'node:test';

const source = (path: string) => readFileSync(new URL(path, import.meta.url), 'utf8');

describe('vendor address UI contract', () => {
	it('threads structured address fields through the vendor create/edit dialog', () => {
		const vendorsPage = source('../../routes/(protected)/vendors/+page.svelte');
		const schemaSource = source('../schemas/index.ts');
		const typesSource = source('../types/index.ts');

		assert.match(schemaSource, /export const vendorSchema = z\.object\(\{[\s\S]*addressLine1: optionalText/);
		assert.match(schemaSource, /export const vendorSchema = z\.object\(\{[\s\S]*city: optionalText/);
		assert.match(schemaSource, /export const vendorSchema = z\.object\(\{[\s\S]*state: optionalText/);
		assert.match(schemaSource, /export const vendorSchema = z\.object\(\{[\s\S]*postalCode: optionalText/);

		assert.match(typesSource, /export interface Vendor \{[\s\S]*addressLine1\?: string \| null;/);
		assert.match(typesSource, /export interface Vendor \{[\s\S]*city\?: string \| null;/);
		assert.match(typesSource, /export interface Vendor \{[\s\S]*state\?: string \| null;/);
		assert.match(typesSource, /export interface Vendor \{[\s\S]*postalCode\?: string \| null;/);

		assert.match(vendorsPage, /import AddressAutocomplete from '\$lib\/components\/shared\/AddressAutocomplete\.svelte';/);
		assert.match(vendorsPage, /import StateSelect from '\$lib\/components\/shared\/StateSelect\.svelte';/);
		assert.match(vendorsPage, /const emptyVendor = \{[\s\S]*addressLine1: ''[\s\S]*city: ''[\s\S]*state: ''[\s\S]*postalCode: ''/);
		assert.match(vendorsPage, /addressLine1: v\.addressLine1 \?\? ''/);
		assert.match(vendorsPage, /city: v\.city \?\? ''/);
		assert.match(vendorsPage, /state: v\.state \?\? ''/);
		assert.match(vendorsPage, /postalCode: v\.postalCode \?\? ''/);
		assert.match(vendorsPage, /<AddressAutocomplete[\s\S]*testid="vendor-address-input"[\s\S]*bind:value=\{vendorForm\.addressLine1\}/);
		assert.match(vendorsPage, /onresolved=\{\(a\) => \{[\s\S]*vendorForm\.city = a\.city[\s\S]*vendorForm\.state = a\.state[\s\S]*vendorForm\.postalCode = a\.zip[\s\S]*\}\}/);
		assert.match(vendorsPage, /data-testid="vendor-city-input"[\s\S]*bind:value=\{vendorForm\.city\}/);
		assert.match(vendorsPage, /<StateSelect[\s\S]*testid="vendor-state-input"[\s\S]*bind:value=\{vendorForm\.state\}/);
		assert.match(vendorsPage, /data-testid="vendor-zip-input"[\s\S]*bind:value=\{vendorForm\.postalCode\}/);
	});

	it('shows vendor address fields on the detail page', () => {
		const detailPage = source('../../routes/(protected)/vendors/[id]/+page.svelte');

		assert.match(detailPage, /function formatVendorAddress\(v: Vendor\)/);
		assert.match(detailPage, /data-testid="vendor-detail-address"/);
		assert.match(detailPage, /formatVendorAddress\(vendor\)/);
	});
});
