import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { describe, it } from 'node:test';
import { formatLeaseAnswerText } from './lease-answer-display.ts';

describe('portal lease answer display', () => {
	it('strips raw Markdown emphasis markers from visible answer text', () => {
		assert.equal(
			formatLeaseAnswerText('Rent is due on the **1st of each month**.'),
			'Rent is due on the 1st of each month.'
		);
	});

	it('uses the formatter on the tenant portal lease page answer', () => {
		const pageSource = readFileSync(
			new URL('../../routes/(portal)/portal/lease/+page.svelte', import.meta.url),
			'utf8'
		);

		assert.match(pageSource, /formatLeaseAnswerText\(leaseAnswer\)/);
	});
});
