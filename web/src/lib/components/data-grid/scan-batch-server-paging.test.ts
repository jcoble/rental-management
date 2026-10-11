import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';

const batchPage = readFileSync(
	new URL('../../../routes/(protected)/scan/batch/[id]/+page.svelte', import.meta.url),
	'utf8'
);
const scanApi = readFileSync(new URL('../../api/scan.ts', import.meta.url), 'utf8');
const scanController = readFileSync(
	new URL('../../../../../RentalCommand.Api/Controllers/ScanController.cs', import.meta.url),
	'utf8'
);

test('scan batch drafts are counted and paged on the server', () => {
	assert.match(scanApi, /getBatch:[\s\S]*skip\?: number; take\?: number/);
	assert.match(batchPage, /scan\.getBatch\(batchId,[\s\S]*skip: \(currentPage - 1\) \* PAGE_SIZE/);
	assert.match(batchPage, /<DataGrid[\s\S]*serverSide[\s\S]*totalCount=\{batch\?\.draftTotalCount/);
	assert.match(batchPage, /data\.counts\.pending > 0/);
	assert.doesNotMatch(batchPage, /drafts\.filter\(/);
	assert.doesNotMatch(batchPage, /drafts\.some\(/);
	assert.match(
		scanController,
		/AuthorizedDrafts\(scope\)[\s\S]*\.Where\(d => d\.BatchId == id\)[\s\S]*\.Skip\(skip\)[\s\S]*\.Take\(take\)[\s\S]*\.ToListAsync\(ct\)/
	);
});
