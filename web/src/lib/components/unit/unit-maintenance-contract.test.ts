import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { describe, test } from 'node:test';

const page = readFileSync(
	new URL('../../../routes/(protected)/units/[id]/+page.svelte', import.meta.url),
	'utf8',
);
const inspections = readFileSync(new URL('../../api/endpoints/inspections.ts', import.meta.url), 'utf8');
const recurring = readFileSync(new URL('../../api/endpoints/recurring-maintenance.ts', import.meta.url), 'utf8');
const inspectionDetail = readFileSync(new URL('../records/InspectionDetail.svelte', import.meta.url), 'utf8');

describe('Unit Maintenance query contract', () => {
	test('requests server-paged Unit inspections', () => {
		assert.match(inspections, /\/inspections\/page/);
		assert.match(inspections, /buildListQuery\(list, \{ unitId \}\)/);
		assert.match(page, /inspections\.listUnitPage\(\{[\s\S]*unitId: id/);
	});

	test('requests server-paged Unit recurring maintenance', () => {
		assert.match(recurring, /\/recurring-maintenance\/page/);
		assert.match(recurring, /unitId/);
		assert.match(page, /recurringMaintenance\.listPage\(\{[\s\S]*unitId: id/);
	});

	test('opens an inspection detail from the Unit list with a maintenance-owned record key', () => {
		assert.match(page, /page\.url\.searchParams\.get\('inspection'\)/);
		assert.match(page, /goto\(`\/units\/\$\{id\}\?tab=maintenance&view=inspections&inspection=\$\{inspectionId\}`/);
		assert.match(page, /<InspectionDetail inspectionId=\{selectedInspectionId\} expectedUnitId=\{id\} \/>/);
		assert.match(page, /data-testid="inspection-detail-close"/);
		assert.match(page, /formatDateOnly\(inspection\.scheduledFor\)/);
		assert.match(inspectionDetail, /inspections\.get\(inspectionId\)/);
		assert.match(inspectionDetail, /data-testid="inspection-detail-facts"/);
		assert.match(inspectionDetail, /data-testid="inspection-detail-checklist"/);
	});
});
