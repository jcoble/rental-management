import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { describe, test } from 'node:test';

const todayPage = readFileSync(
	new URL('../../../routes/(protected)/leasing/+page.svelte', import.meta.url),
	'utf8'
);
const listPage = readFileSync(new URL('./LeasingListPage.svelte', import.meta.url), 'utf8');
const detailPage = readFileSync(
	new URL('../../../routes/(protected)/leasing/[record]/[id]/+page.svelte', import.meta.url),
	'utf8'
);

describe('leasing role loading states', () => {
	test('uses layout-preserving shared loaders for primary queries', () => {
		for (const source of [todayPage, listPage, detailPage]) {
			assert.match(source, /LoadingState/);
			assert.match(source, /variant="page"/);
		}
		assert.match(todayPage, /leasing-today-loading/);
		assert.match(listPage, /leasing-\{kind\}-loading/);
		assert.match(detailPage, /leasing-record-loading/);
	});

	test('offers a retry when leasing reads fail', () => {
		assert.match(todayPage, /todayQuery\.refetch\(\)/);
		assert.match(listPage, /pageQuery\.refetch\(\)/);
		assert.match(detailPage, /detailQuery\.refetch\(\)/);
	});

	test('keys pipeline rows by kind and record id before unit id', () => {
		assert.match(
			listPage,
			/if \(kind === 'pipeline' && record\.kind != null && record\.recordId != null\) \{\s+return `\$\{record\.kind\}-\$\{record\.recordId\}`;\s+\}/
		);
		assert.match(listPage, /\{#each result\.items as raw \(itemKey\(raw\)\)\}/);
	});
});
