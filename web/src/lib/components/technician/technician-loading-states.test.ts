import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { describe, test } from 'node:test';

const listPage = readFileSync(new URL('./TechnicianAssignmentList.svelte', import.meta.url), 'utf8');
const detailPage = readFileSync(
	new URL('../../../routes/(protected)/my-work/[id]/+page.svelte', import.meta.url),
	'utf8'
);

describe('technician role loading states', () => {
	test('uses shared page skeletons for assigned work', () => {
		for (const source of [listPage, detailPage]) {
			assert.match(source, /LoadingState/);
			assert.match(source, /variant="page"/);
		}
		assert.match(listPage, /technician-assignment-list-loading/);
		assert.match(detailPage, /technician-assignment-loading/);
	});

	test('keeps failed assignment reads recoverable', () => {
		assert.match(listPage, /query\.refetch\(\)/);
		assert.match(detailPage, /assignment\.refetch\(\)/);
	});
});
