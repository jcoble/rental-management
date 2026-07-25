import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { describe, test } from 'node:test';

const sources = {
	overview: readFileSync(
		new URL('../../../routes/(protected)/owner/+page.svelte', import.meta.url),
		'utf8'
	),
	properties: readFileSync(
		new URL('../../../routes/(protected)/owner/properties/+page.svelte', import.meta.url),
		'utf8'
	),
	statements: readFileSync(
		new URL('../../../routes/(protected)/owner/statements/+page.svelte', import.meta.url),
		'utf8'
	),
	items: readFileSync(new URL('./OwnerItemsPage.svelte', import.meta.url), 'utf8')
};

describe('owner role loading states', () => {
	test('uses shared skeletons for owner reads', () => {
		for (const source of Object.values(sources)) assert.match(source, /LoadingState/);
		assert.match(sources.overview, /owner-overview-loading/);
		assert.match(sources.properties, /owner-properties-loading/);
		assert.match(sources.statements, /owner-statements-loading/);
		assert.match(sources.statements, /owner-statement-loading/);
		assert.match(sources.statements, /owner-distributions-loading/);
		assert.match(sources.items, /owner-\{kind\}-loading/);
	});

	test('offers a retry for each failed owner read', () => {
		assert.match(sources.overview, /overviewQuery\.refetch\(\)/);
		assert.match(sources.properties, /propertiesQuery\.refetch\(\)/);
		assert.match(sources.statements, /summariesQuery\.refetch\(\)/);
		assert.match(sources.statements, /statementQuery\.refetch\(\)/);
		assert.match(sources.statements, /distributionsQuery\.refetch\(\)/);
		assert.match(sources.items, /itemsQuery\.refetch\(\)/);
	});
});
