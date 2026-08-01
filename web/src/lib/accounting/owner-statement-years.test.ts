import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { describe, it } from 'node:test';
import {
	currentOwnerStatementYear,
	ownerStatementYearOptions,
	watchOwnerStatementYear
} from './owner-statement-years.ts';

const ownersReportSource = readFileSync(
	new URL('../../routes/(protected)/owners-report/+page.svelte', import.meta.url),
	'utf8'
);
const ownerPortalStatementsSource = readFileSync(
	new URL('../../routes/(protected)/owner/statements/+page.svelte', import.meta.url),
	'utf8'
);

describe('owner statement year selection', () => {
	it('uses the supplied business date as the current statement year', () => {
		assert.equal(currentOwnerStatementYear(new Date('2027-01-25T05:00:00.000Z')), 2027);
	});

	it('offers a five-year statement window from the business year', () => {
		assert.deepEqual(ownerStatementYearOptions(2027), [2027, 2026, 2025, 2024, 2023]);
	});

	it('notifies routes when the effective business year changes after mount', () => {
		let currentYear = 2026;
		let tick: (() => void) | undefined;
		let cleared = false;
		const seenYears: number[] = [];

		const stop = watchOwnerStatementYear((year) => seenYears.push(year), {
			intervalMs: 1,
			now: () => new Date(`${currentYear}-01-25T05:00:00.000Z`),
			setInterval: (handler) => {
				tick = handler;
				return 171;
			},
			clearInterval: (handle) => {
				cleared = handle === 171;
			}
		});

		assert.deepEqual(seenYears, [2026]);
		currentYear = 2027;
		tick?.();
		tick?.();
		assert.deepEqual(seenYears, [2026, 2027]);
		stop();
		assert.equal(cleared, true);
	});

	it('keeps the management and owner portal routes on the shared business-year helper', () => {
		for (const source of [ownersReportSource, ownerPortalStatementsSource]) {
			assert.match(source, /currentOwnerStatementYear/);
			assert.match(source, /ownerStatementYearOptions/);
			assert.match(source, /watchOwnerStatementYear\(applyBusinessYear\)/);
			assert.doesNotMatch(source, /new Date\(\)\.getFullYear\(\)/);
		}
	});
});
