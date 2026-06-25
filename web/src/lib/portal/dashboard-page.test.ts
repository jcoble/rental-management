import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { describe, it } from 'node:test';

describe('portal dashboard page', () => {
	it('loads and renders upcoming tenant appointments instead of static placeholder copy', () => {
		const source = readFileSync(
			new URL('../../routes/(portal)/portal/+page.svelte', import.meta.url),
			'utf8'
		);

		assert.match(source, /queryKey: \['portal-appointments'\]/);
		assert.match(source, /queryFn: \(\) => portal\.appointments\(\)/);
		assert.match(source, /const appointments = \$derived/);
		assert.match(source, /#each upcomingAppointments\.slice\(0, 3\) as appointment/);
		assert.match(source, /appointmentWindow\(appointment\)/);
		assert.match(source, /locationLabel\(appointment\)/);
		assert.doesNotMatch(
			source,
			/<section id="appointments"[\s\S]*Upcoming visits, inspections, and maintenance appointments will appear here\.[\s\S]*<\/section>/
		);
	});

	it('shows validation for empty dashboard maintenance requests', () => {
		const source = readFileSync(
			new URL('../../routes/(portal)/portal/+page.svelte', import.meta.url),
			'utf8'
		);

		assert.match(source, /let workOrderSubmitted = \$state\(false\)/);
		assert.match(source, /workOrderTitleError/);
		assert.match(source, /workOrderDescriptionError/);
		assert.match(source, /data-testid="tenant-dashboard-maintenance-title-error"/);
		assert.match(source, /data-testid="tenant-dashboard-maintenance-description-error"/);
		assert.doesNotMatch(source, /if \(!workOrderForm\.title\.trim\(\) \|\| !workOrderForm\.description\.trim\(\)\) return;/);
	});
});
