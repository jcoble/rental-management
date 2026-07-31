import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { describe, it } from 'node:test';

const source = readFileSync(
	'src/lib/components/shared/RemoteRecordSelect.svelte',
	'utf8'
);

describe('RemoteRecordSelect', () => {
	it('pages beyond first page', () => {
		assert.match(source, /skip: \(resultPage - 1\) \* pageSize/);
		assert.match(source, /take: pageSize/);
		assert.match(
			source,
			/resultPage = Math\.min\(totalPages, resultPage \+ 1\)/
		);
		assert.match(source, /data-testid="\{testid\}-paging"/);
	});

	it('keeps selection stable', () => {
		assert.match(source, /rememberedSelection/);
		assert.match(source, /selectedLabel/);
		assert.match(source, /rememberedSelection\?\.value === value/);
		assert.doesNotMatch(source, /value\s*=\s*recordsQuery\.data/);
	});

	it('hides cross-scope records', () => {
		assert.match(source, /loadPage: \(params:/);
		assert.match(source, /loadPage\(\{/);
		assert.match(source, /search: debouncedSearch\.value \|\| undefined/);
		assert.doesNotMatch(source, /\.filter\(/);
		assert.doesNotMatch(source, /take:\s*(100|200|500)/);
		assert.doesNotMatch(source, /placeholderData/);
	});
});
