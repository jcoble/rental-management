import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { readPublicError } from './public-error.ts';

describe('public API error parsing', () => {
	it('prefers ProblemDetails detail over the generic title', async () => {
		const response = new Response(
			JSON.stringify({
				title: 'Conflict',
				detail:
					'An application for qa.applicant.001@example.local already exists as application #1. Review the existing application before creating another.'
			}),
			{ status: 409, headers: { 'Content-Type': 'application/problem+json' } }
		);

		const message = await readPublicError(response);

		assert.equal(
			message,
			'An application for qa.applicant.001@example.local already exists as application #1. Review the existing application before creating another.'
		);
	});
});
