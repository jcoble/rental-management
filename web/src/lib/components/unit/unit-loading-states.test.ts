import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { describe, test } from 'node:test';

const sources = [
	'./tabs/LeaseTab.svelte',
	'./tabs/ListingTab.svelte',
	'./tabs/ApplicationsTab.svelte',
	'./tabs/ExpensesTab.svelte',
	'./tabs/MaintenanceTab.svelte',
	'./tabs/RentTab.svelte',
	'./tabs/TimelineTab.svelte',
	'../../../routes/(protected)/units/[id]/+page.svelte',
	'../leases/LeaseManagementDetail.svelte',
].map((path) => ({
	path,
	source: readFileSync(new URL(path, import.meta.url), 'utf8'),
}));
const expensesTab = sources.find(({ path }) => path === './tabs/ExpensesTab.svelte')!.source;

describe('Unit and lease loading states', () => {
	test('uses the shared accessible loader for primary and major query surfaces', () => {
		for (const { path, source } of sources) {
			assert.match(source, /LoadingState/, path);
			assert.doesNotMatch(
				source,
				/<p[^>]*>\s*Loading (?:tenant relationships|listing workspace|applications|expenses|Property expenses|financing|work orders|history|account activity|charges|deposits|receipts|inspections|recurring maintenance|tenant and lease relationship|agreement versions|addendum versions)…?\s*<\/p>/i,
				path
			);
		}
	});

	test('keeps retry actions beside every owned query error branch', () => {
		for (const { path, source } of sources) {
			for (const match of source.matchAll(/\{:else if ([A-Za-z]+Query)\.isError\}/g)) {
				const branch = source.slice(match.index, source.indexOf('{:else', match.index + 1));
				assert.match(branch, new RegExp(`${match[1]}\\.refetch\\(\\)`), path);
			}
		}
	});

	test('holds the Property money region while its gating query loads or fails', () => {
		const loadingRegion = expensesTab.slice(
			expensesTab.indexOf('{#if propertyQuery.isLoading}'),
			expensesTab.indexOf('{:else if propertyQuery.isError}')
		);
		assert.match(
			expensesTab,
			/\{#if propertyQuery\.isLoading\}[\s\S]*property-money-sections-loading[\s\S]*LoadingState[\s\S]*\{:else if propertyQuery\.isError\}[\s\S]*property-money-sections-error[\s\S]*propertyQuery\.refetch\(\)/
		);
		assert.equal(loadingRegion.match(/<LoadingState/g)?.length, 1);
		assert.match(loadingRegion, /label="Loading Property money details"/);
		assert.doesNotMatch(loadingRegion, /Loading Property expenses|Loading financing/);
		assert.match(
			expensesTab,
			/\{#if !propertyQuery\.isLoading && !propertyQuery\.isError && moneySections\.propertyExpenses\}[\s\S]*\{#if !propertyQuery\.isLoading && !propertyQuery\.isError && moneySections\.financing\}/
		);
	});
});
