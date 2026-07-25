import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { describe, it } from 'node:test';

const source = readFileSync('src/lib/components/unit/tabs/ApplicationsTab.svelte', 'utf8');

describe('unit applications tab', () => {
	it('uses the empty-state application link action without also rendering the list toolbar action', () => {
		assert.match(source, /data-testid="unit-application-empty-create-link"/);
		assert.match(
			source,
			/\{#if !applicationsQuery\.isLoading && appList\.length > 0\}[\s\S]*data-testid="unit-application-create-link"[\s\S]*\{\/if\}[\s\S]*\{#if applicationsQuery\.isLoading\}/
		);
	});

	it('queries the selected Unit with server search, sort, and page state', () => {
		assert.match(source, /unitId,/);
		assert.match(source, /retry: false/);
		assert.match(source, /search: appSearch \|\| undefined/);
		assert.match(source, /sort: appSort/);
		assert.match(source, /skip: \(appPage - 1\) \* TAB_PAGE_SIZE/);
		assert.match(source, /applicationSearch/);
		assert.match(source, /applicationSort/);
		assert.match(source, /applicationPage/);
	});

	it('renders loading, retryable error, empty, page, canonical detail, and Back states', () => {
		assert.match(source, /Loading applications/);
		assert.match(source, /unit-applications-error/);
		assert.match(source, /applicationsQuery\.refetch\(\)/);
		assert.match(source, /unit-applications-empty/);
		assert.match(source, /unit-applications-paging/);
		assert.match(source, /<ApplicationDetail/);
		assert.match(source, /application-back-to-list/);
		assert.match(source, /goto\(listUrl\(\{ app: id \}\)/);
	});

	it('keeps search draft sync from writing to its own tracked dependency', () => {
		assert.match(source, /import \{ untrack \} from 'svelte';/);
		assert.match(source, /if \(untrack\(\(\) => searchDraft\) !== appSearch\) searchDraft = appSearch;/);
		assert.doesNotMatch(source, /\$effect\(\(\) => \{\s*searchDraft = appSearch;\s*\}\);/);
	});
});
