import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { test } from 'node:test';
import {
	isTeamAssignmentEffectivelyActive,
	teamAssignmentDisplayStatus
} from './team-assignment-lifecycle.ts';

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

test('assignment controls use effective lifecycle rather than stored status alone', () => {
	assert.match(pageSource, /teamAssignmentDisplayStatus\(assignment\)/);
	assert.match(pageSource, /isTeamAssignmentEffectivelyActive\(assignment\)/);
	assert.doesNotMatch(pageSource, /canManageTeam && assignment\.status === 'Active'/);
});

test('property-scope replacement starts from the assignment current selected property ids', () => {
	const replaceStart = pageSource.indexOf('function beginReplaceProperties');
	const replaceEnd = pageSource.indexOf('function chooseAssignmentRole', replaceStart);
	const replaceFunction = pageSource.slice(replaceStart, replaceEnd);

	assert.match(endpointSource, /selectedPropertyIds: number\[\]/);
	assert.match(
		replaceFunction,
		/assignmentPropertyIds = assignment\.selectedPropertyIds\.toSorted\(\(a, b\) => a - b\)/
	);
	assert.doesNotMatch(replaceFunction, /assignmentPropertyIds = \[\]/);
});

test('active assignment is display-ended once its effective end is at or before app time', () => {
	const asOfUtc = new Date('2027-01-19T05:00:00.000Z');
	const endedAtAppTime = {
		status: 'Active',
		effectiveToUtc: '2027-01-19T05:00:00.000Z'
	};
	const endedBeforeAppTime = {
		status: 'Active',
		effectiveToUtc: '2027-01-19T04:59:59.999Z'
	};
	const activeFutureEnd = {
		status: 'Active',
		effectiveToUtc: '2027-01-19T05:00:00.001Z'
	};

	assert.equal(isTeamAssignmentEffectivelyActive(endedAtAppTime, asOfUtc), false);
	assert.equal(teamAssignmentDisplayStatus(endedAtAppTime, asOfUtc), 'Ended');
	assert.equal(isTeamAssignmentEffectivelyActive(endedBeforeAppTime, asOfUtc), false);
	assert.equal(teamAssignmentDisplayStatus(endedBeforeAppTime, asOfUtc), 'Ended');
	assert.equal(isTeamAssignmentEffectivelyActive(activeFutureEnd, asOfUtc), true);
	assert.equal(teamAssignmentDisplayStatus(activeFutureEnd, asOfUtc), 'Active');
});
