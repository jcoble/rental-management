import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { test } from 'node:test';

const pageSource = readFileSync(
	new URL('../../routes/(admin)/admin/users/+page.svelte', import.meta.url),
	'utf8'
);
const endpointSource = readFileSync(new URL('../api/endpoints/team.ts', import.meta.url), 'utf8');

test('Team assignment controls use canonical capability and assignment APIs', () => {
	assert.match(pageSource, /hasCapability\('team\.manage'\)/);
	assert.match(endpointSource, /\/team\/members\/\$\{accessContextId\}\/assignments/);
	assert.match(endpointSource, /\/assignments\/\$\{assignmentId\}\/end/);
	assert.match(endpointSource, /\/assignments\/\$\{assignmentId\}\/properties/);
	assert.match(pageSource, /applyMutationRevision/);
});

test('Team explains orthogonal scopes and excludes relationship experiences from jobs', () => {
	assert.match(pageSource, /Property scope grants access across all or selected rentals/);
	assert.match(pageSource, /Assigned-work scope grants access only to work orders/);
	assert.match(pageSource, /Owners and tenants remain relationship-based experiences/);
	assert.doesNotMatch(pageSource, /<option value="(?:owner|tenant)"/i);
});

test('the sole administrator cannot suspend or end their own access from Team', () => {
	assert.match(pageSource, /disabled=\{isSelf\(member\) \|\| statusMutation\.isPending\}/);
	assert.match(pageSource, /isSelf\(selectedMember\) \|\| endAssignmentMutation\.isPending/);
});
