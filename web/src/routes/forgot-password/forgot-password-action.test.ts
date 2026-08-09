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

/**
 * Counterfactual: the pre-fix action at +page.server.ts:33-53 ignored non-2xx responses and
 * converted rejected fetches into { sent: true }; the first two behavioral assertions fail there.
 */
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
