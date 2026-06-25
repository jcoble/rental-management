import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { describe, it } from 'node:test';

const depositDetailSource = readFileSync(
	'src/routes/(protected)/deposits/[id]/+page.svelte',
	'utf8'
);
const importSource = readFileSync('src/routes/(protected)/import/+page.svelte', 'utf8');
const taxSource = readFileSync('src/routes/(protected)/tax/+page.svelte', 'utf8');

describe('pass 27 UI regressions', () => {
	it('labels the deposit return confirmation as a return action instead of delete', () => {
		assert.match(depositDetailSource, /confirmLabel="Process return"/);
	});

	it('shows the same tenant CSV columns that the downloaded template accepts', () => {
		assert.match(importSource, /columns: 'firstName, lastName, email, phone'/);
		assert.doesNotMatch(importSource, /columns: 'first name, last name, email, phone'/);
	});

	it('keeps the year-end packet selector aligned to the visible tax summary year', () => {
		assert.doesNotMatch(taxSource, /let packetYear = \$state/);
		assert.doesNotMatch(taxSource, /PACKET_YEAR_OPTIONS/);
		assert.match(taxSource, /downloadYearEndPacket\(Number\(selectedYear\)\)/);
	});
});
