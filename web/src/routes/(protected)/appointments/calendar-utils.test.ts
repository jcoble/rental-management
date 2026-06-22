import { test } from 'node:test';
import assert from 'node:assert/strict';

import { labelForType } from './calendar-utils.ts';

test('appointment type labels are user-facing names, not enum tokens', () => {
	assert.equal(labelForType('MaintenanceVisit'), 'Maintenance');
	assert.equal(labelForType('OwnerMeeting'), 'Owner meeting');
	assert.equal(labelForType('MoveIn'), 'Move-in');
	assert.equal(labelForType('MoveOut'), 'Move-out');
});

test('unknown appointment type has a stable fallback label', () => {
	assert.equal(labelForType('Unrecognized'), 'Other');
	assert.equal(labelForType(undefined), '');
});
