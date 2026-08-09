import assert from 'node:assert/strict';
import { register } from 'node:module';
import { test } from 'node:test';

register('./forgot-password-action-test-loader.mjs', import.meta.url);

const { actions } = await import('./+page.server.ts');
const action = actions.default as (event: { request: Request }) => Promise<any>;

const email = 'landlord@example.test';

function requestFor(value: string): Request {
	return new Request('https://rental-command.test/forgot-password', {
		method: 'POST',
		headers: { 'content-type': 'application/x-www-form-urlencoded' },
		body: new URLSearchParams({ email: value })
	});
}

async function withFetch(
	fetchImpl: typeof fetch,
	callback: () => Promise<void>
): Promise<void> {
	const originalFetch = globalThis.fetch;
	const originalConsoleError = console.error;
	globalThis.fetch = fetchImpl;
	console.error = () => undefined;
	try {
		await callback();
	} finally {
		globalThis.fetch = originalFetch;
		console.error = originalConsoleError;
	}
}

type FetchOutcome =
	| { kind: 'response'; response: Response }
	| { kind: 'rejected'; error: Error };

function assertRetryableFailureContract(result: Record<string, unknown>, error: string): void {
	assert.deepEqual(result, {
		status: 503,
		data: {
			error,
			sent: false,
			email
		}
	});
}

/** The pre-fix mutation: every fetch outcome is reported as a neutral success. */
function preFixResponseHandler(_email: string, _outcome: FetchOutcome): { sent: true } {
	return { sent: true };
}

function assertPreFixViolation(
	scenario: string,
	outcome: FetchOutcome,
	error: string
): { scenario: string; assertion: string; actual: { sent: true }; expected: object } {
	const actual = preFixResponseHandler(email, outcome);
	let assertionError: unknown;

	try {
		assertRetryableFailureContract(actual, error);
	} catch (caught) {
		assertionError = caught;
	}

	assert.ok(assertionError, `${scenario} counterfactual unexpectedly satisfied the failure contract`);
	assert.equal((assertionError as Error).name, 'AssertionError');

	return {
		scenario,
		assertion: (assertionError as Error).message.split('\n', 1)[0],
		actual,
		expected: {
			status: 503,
			data: { error, sent: false, email }
		}
	};
}

test('forgot-password failure contract rejects the pre-fix always-sent counterfactual', () => {
	const violations = [
		assertPreFixViolation(
			'non-2xx API response',
			{ kind: 'response', response: new Response(null, { status: 503 }) },
			'Password recovery is temporarily unavailable. Please try again.'
		),
		assertPreFixViolation(
			'rejected fetch',
			{ kind: 'rejected', error: new Error('connection refused') },
			'Unable to connect to Rental Command. Please try again.'
		)
	];

	console.log(`Counterfactual violations (pre-fix always-sent): ${JSON.stringify(violations)}`);
	assert.deepEqual(
		violations.map(({ scenario }) => scenario),
		['non-2xx API response', 'rejected fetch']
	);
});

test('forgot-password action returns retryable 503 for a non-2xx API response', async () => {
	await withFetch(
		async () => new Response(null, { status: 503 }),
		async () => {
			const result = await action({ request: requestFor(email) });

			assert.equal(result.status, 503);
			assert.deepEqual(result.data, {
				error: 'Password recovery is temporarily unavailable. Please try again.',
				sent: false,
				email
			});
		}
	);
});

test('forgot-password action returns retryable 503 when fetch rejects', async () => {
	await withFetch(
		async () => {
			throw new Error('connection refused');
		},
		async () => {
			const result = await action({ request: requestFor(email) });

			assert.equal(result.status, 503);
			assert.deepEqual(result.data, {
				error: 'Unable to connect to Rental Command. Please try again.',
				sent: false,
				email
			});
		}
	);
});

test('forgot-password action returns neutral success only after a 2xx response', async () => {
	await withFetch(
		async () => new Response(null, { status: 200 }),
		async () => {
			const result = await action({ request: requestFor(email) });

			assert.deepEqual(result, { sent: true });
		}
	);
});
