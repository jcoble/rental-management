import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { test } from 'node:test';

const source = readFileSync(new URL('./+page.svelte', import.meta.url), 'utf8');

test('recent activity routes lease agreements to the unit leasing tab', () => {
	assert.match(source, /activity\.type === 'LeaseAgreement'[\s\S]*?`\/units\/\$\{activity\.unitId\}\?tab=leasing`/);
});

test('recent activity routes tenant accounts to that unit tenant ledger', () => {
	assert.match(
		source,
		/activity\.type === 'TenantAccount'[\s\S]*?`\/units\/\$\{activity\.unitId\}\?tab=money&view=tenant-account&tenantAccount=\$\{activity\.entityId\}`/
	);
});
