import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { describe, it } from 'node:test';

const source = (path: string) => readFileSync(new URL(path, import.meta.url), 'utf8');

describe('vendor address UI contract', () => {
	it('threads structured address fields through the vendor create/edit dialog', () => {
		const vendorsPage = source('../../routes/(protected)/vendors/+page.svelte');
		const vendorFields = source('../components/forms/VendorFields.svelte');
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

		assert.match(vendorFields, /import AddressAutocomplete from '\$lib\/components\/shared\/AddressAutocomplete\.svelte';/);
		assert.match(vendorFields, /import StateSelect from '\$lib\/components\/shared\/StateSelect\.svelte';/);
		assert.match(vendorsPage, /const emptyVendor = \{[\s\S]*addressLine1: ''[\s\S]*city: ''[\s\S]*state: ''[\s\S]*postalCode: ''/);
		assert.match(vendorsPage, /addressLine1: v\.addressLine1 \?\? ''/);
		assert.match(vendorsPage, /city: v\.city \?\? ''/);
		assert.match(vendorsPage, /state: v\.state \?\? ''/);
		assert.match(vendorsPage, /postalCode: v\.postalCode \?\? ''/);
		assert.match(vendorFields, /<AddressAutocomplete[\s\S]*testid=\{`\$\{testidPrefix\}-address-input`\}[\s\S]*bind:value=\{form\.addressLine1\}/);
		assert.match(vendorFields, /onresolved=\{\(address\) => \{[\s\S]*form\.city = address\.city[\s\S]*form\.state = address\.state[\s\S]*form\.postalCode = address\.zip[\s\S]*\}\}/);
		assert.match(vendorFields, /data-testid=\{`\$\{testidPrefix\}-city-input`\}[\s\S]*bind:value=\{form\.city\}/);
		assert.match(vendorFields, /<StateSelect[\s\S]*testid=\{`\$\{testidPrefix\}-state-input`\}[\s\S]*bind:value=\{form\.state\}/);
		assert.match(vendorFields, /data-testid=\{`\$\{testidPrefix\}-zip-input`\}[\s\S]*bind:value=\{form\.postalCode\}/);
	});

	it('shows vendor address fields on the detail page', () => {
		const detailPage = source('../../routes/(protected)/vendors/[id]/+page.svelte');

		assert.match(detailPage, /function formatVendorAddress\(v: Vendor\)/);
		assert.match(detailPage, /data-testid="vendor-detail-address"/);
		assert.match(detailPage, /formatVendorAddress\(vendor\)/);
	});
});
