import assert from 'node:assert/strict';
import { describe, it } from 'node:test';

import {
	buildAsOfQueryString,
	buildDateRangeQueryString,
	getCurrentMonthDateRange,
	getTodayIsoDate,
	isIsoDate,
	readAsOfState,
	readDateRangeState
} from './statement-state.ts';

const frozenNow = new Date('2027-02-12T05:00:00.000Z');

describe('statement state', () => {
	it('formats today and the full current UTC month for date-only controls', () => {
		assert.equal(getTodayIsoDate(frozenNow), '2027-02-12');
		assert.deepEqual(getCurrentMonthDateRange(frozenNow), {
			from: '2027-02-01',
			to: '2027-02-28'
		});
	});

	it('reads a valid date range and falls back for missing, invalid, or reversed values', () => {
		assert.deepEqual(
			readDateRangeState(new URLSearchParams('from=2027-01-01&to=2027-01-31'), frozenNow),
			{ from: '2027-01-01', to: '2027-01-31' }
		);
		assert.deepEqual(readDateRangeState(new URLSearchParams('from=2027-02-31'), frozenNow), {
			from: '2027-02-01',
			to: '2027-02-28'
		});
		assert.deepEqual(readDateRangeState(new URLSearchParams('from=2027-03-01&to=2027-02-01'), frozenNow), {
			from: '2027-02-01',
			to: '2027-02-28'
		});
	});

	it('reads an as-of date and defaults to today', () => {
		assert.deepEqual(readAsOfState(new URLSearchParams('to=2027-01-31'), frozenNow), { to: '2027-01-31' });
		assert.deepEqual(readAsOfState(new URLSearchParams(), frozenNow), { to: '2027-02-12' });
		assert.deepEqual(readAsOfState(new URLSearchParams('to=not-a-date'), frozenNow), { to: '2027-02-12' });
	});

	it('builds a range query while preserving unrelated URL state', () => {
		assert.equal(
			buildDateRangeQueryString(
				{ from: '2027-01-01', to: '2027-01-31' },
				new URLSearchParams('coach=reports&from=old&to=old')
			),
			'coach=reports&from=2027-01-01&to=2027-01-31'
		);
	});

	it('builds an as-of query with only to for point-in-time statements', () => {
		assert.equal(
			buildAsOfQueryString({ to: '2027-01-31' }, new URLSearchParams('coach=reports&from=old&to=old')),
			'coach=reports&to=2027-01-31'
		);
	});

	it('recognizes only real ISO calendar dates', () => {
		assert.equal(isIsoDate('2027-02-28'), true);
		assert.equal(isIsoDate('2027-02-29'), false);
		assert.equal(isIsoDate('02/28/2027'), false);
		assert.equal(isIsoDate(null), false);
	});
});
