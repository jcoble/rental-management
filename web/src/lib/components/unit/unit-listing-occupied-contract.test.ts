import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { describe, it } from 'node:test';

const source = readFileSync(new URL('./tabs/ListingTab.svelte', import.meta.url), 'utf8');

describe('occupied unit listing surface', () => {
	it('makes occupied listing views point to the canonical Tenant & lease surface', () => {
		assert.match(source, /dashboard\?\.occupancyPossession\.isOccupied/);
		assert.match(source, /dashboard\.leaseManagementId/);
		assert.match(source, /This unit is occupied/);
		assert.match(source, /Open Tenant &amp; lease/);
		assert.match(source, /tab=tenant-lease&view=agreements/);
	});

	it('does not offer prepare-listing as the empty state for an occupied unit', () => {
		assert.match(source, /\{#if workspace \|\| !isOccupied\}/);
		assert.match(source, /\{:else if !workspace && isOccupied\}/);
		assert.match(source, /Because this unit is occupied, there is no listing workspace to prepare right now\./);
		assert.match(source, /\{:else if !workspace\}[\s\S]*Prepare this rental listing/);
	});
});
