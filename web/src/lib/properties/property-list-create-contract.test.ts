import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { describe, it } from 'node:test';

const propertiesPage = readFileSync(
	new URL('../../routes/(protected)/properties/+page.svelte', import.meta.url),
	'utf8'
);
const propertiesEndpoint = readFileSync(
	new URL('../api/endpoints/properties.ts', import.meta.url),
	'utf8'
);

describe('properties list create contract', () => {
	it('opens canonical guided property setup from the management create action', () => {
		const openCreateBody = propertiesPage.match(
			/function openCreate\(\) \{([\s\S]*?)\n\t\}/
		)?.[1] ?? '';
		assert.match(propertiesPage, /import \{ hasAllPropertiesRentalsManageAuthority \} from '\$lib\/auth\/property-authority';/);
		assert.match(propertiesPage, /const canCreateProperty = \$derived\(hasAllPropertiesRentalsManageAuthority\(currentAccess\)\);/);
		assert.match(openCreateBody, /if \(!canCreateProperty\) return;/);
		assert.match(openCreateBody, /goto\('\/onboarding\?step=property&from=properties'\);/);
		assert.doesNotMatch(openCreateBody, /showForm = true;/);
		assert.match(propertiesPage, /<Dialog\.Title>Edit Property<\/Dialog\.Title>/);
		assert.match(propertiesPage, /emptyOnAction=\{canCreateProperty \? openCreate : undefined\}/);
		assert.doesNotMatch(propertiesPage, /emptyOnAction=\{canManageRentals \? openCreate : undefined\}/);
		assert.match(propertiesPage, /\{#if canCreateProperty\}[\s\S]*data-testid="property-create-button"[\s\S]*onclick=\{openCreate\}/);
		assert.match(propertiesPage, /<Button data-testid="property-create-button"[\s\S]*onclick=\{openCreate\}/);
	});

	it('retires the invalid list create mutation and leaves edit on update', () => {
		assert.match(propertiesPage, /if \(editingId == null\) return;/);
		assert.doesNotMatch(propertiesPage, /properties\.create\(vars\.data\)/);
		assert.doesNotMatch(propertiesPage, /mode: 'create'/);
		assert.match(propertiesPage, /mutationFn: \(vars: SavePropertyVariables\) => properties\.update\(vars\.id, vars\.data\)/);
		assert.match(propertiesPage, /savePropertyMutation\.mutate\(\s*\{ id: editingId, data \}\s*\)/);
		assert.match(propertiesPage, /function openEdit\(p: Property\) \{[\s\S]*editingId = p\.id;[\s\S]*showForm = true;[\s\S]*\}/);
		assert.doesNotMatch(propertiesEndpoint, /create: \(data:/);
		assert.doesNotMatch(propertiesEndpoint, /properties:create:/);
		assert.doesNotMatch(propertiesEndpoint, /\{ property: data, units: \[\] \}/);
	});

	it('blocks stale in-service-date submission while the picker text is invalid', () => {
		assert.match(propertiesPage, /let inServiceDateInvalid = \$state\(false\);/);
		assert.match(propertiesPage, /<DatePicker[\s\S]*?bind:invalid=\{inServiceDateInvalid\}/);
		assert.match(
			propertiesPage,
			/disabled=\{savePropertyMutation\.isPending \|\| inServiceDateInvalid\}/
		);
		const submitProperty = propertiesPage.match(
			/function submitProperty\(\) \{[\s\S]*?\n\t\}\n\n\t\/\/ DataGrid/
		)?.[0] ?? '';
		assert.match(submitProperty, /if \(inServiceDateInvalid\) \{[\s\S]*?return;/);
		const invalidGuardIndex = submitProperty.indexOf('if (inServiceDateInvalid)');
		const mutationIndex = submitProperty.indexOf('savePropertyMutation.mutate');
		assert.ok(invalidGuardIndex >= 0 && invalidGuardIndex < mutationIndex);
	});
});
