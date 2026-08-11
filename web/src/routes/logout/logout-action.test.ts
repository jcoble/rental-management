import assert from 'node:assert/strict';
import { register } from 'node:module';
import { test } from 'node:test';

register('./logout-action-test-loader.mjs', import.meta.url);

const { actions } = await import('./+page.server.ts');
const action = actions.default as unknown as (event: { cookies: FakeCookies }) => Promise<unknown>;
const testGlobals = globalThis as typeof globalThis & { __logoutRefreshCalls?: number };

class FakeCookies {
	private readonly values: Map<string, string>;
	readonly deleted: string[] = [];

	constructor(values: Record<string, string>) {
		this.values = new Map(Object.entries(values));
	}

	get(name: string): string | undefined {
		return this.values.get(name);
	}

	delete(name: string): void {
		this.deleted.push(name);
		this.values.delete(name);
	}
}

async function expectRedirect(cookies: FakeCookies): Promise<void> {
	await assert.rejects(
		action({ cookies }),
		(error: { status?: number; location?: string }) =>
			error.status === 303 && error.location === '/login'
	);
}

async function withFetch(
	fetchImpl: typeof fetch,
	callback: () => Promise<void>
): Promise<void> {
	const originalFetch = globalThis.fetch;
	globalThis.fetch = fetchImpl;
	try {
		await callback();
	} finally {
		globalThis.fetch = originalFetch;
	}
}

test('logout awaits API revocation with the access bearer and clears every auth cookie', async () => {
	let request: { input: string; init?: RequestInit } | undefined;
	await withFetch(async (input, init) => {
		request = { input: String(input), init };
		return new Response(null, { status: 200 });
	}, async () => {
		const cookies = new FakeCookies({
			rc_access_token: 'access-token',
			rc_access_token_expiration: new Date(Date.now() + 60_000).toISOString(),
			rc_refresh_token: 'refresh-token'
		});

		await expectRedirect(cookies);

		assert.equal(request?.input, 'https://api.test/api/v1/auth/logout');
		const headers = new Headers(request?.init?.headers);
		assert.equal(headers.get('Authorization'), 'Bearer access-token');
		assert.equal(headers.get('Cookie'), 'rc_refresh_token=refresh-token');
		assert.deepEqual(cookies.deleted, [
			'rc_access_token',
			'rc_access_token_expiration',
			'rc_refresh_token'
		]);
	});
});

test('logout refreshes when the access bearer is absent and still clears cookies', async () => {
	let calls = 0;
	await withFetch(async (_input, init) => {
		calls += 1;
		const headers = new Headers(init?.headers);
		assert.equal(headers.get('Authorization'), 'Bearer refreshed-access-token');
		return new Response(null, { status: 200 });
	}, async () => {
		const cookies = new FakeCookies({ rc_refresh_token: 'refresh-token' });
		await expectRedirect(cookies);
		assert.equal(calls, 1);
		assert.equal(testGlobals.__logoutRefreshCalls, 1);
		assert.deepEqual(cookies.deleted, [
			'rc_access_token',
			'rc_access_token_expiration',
			'rc_refresh_token'
		]);
	});
	delete testGlobals.__logoutRefreshCalls;
});

test('logout reports a non-2xx revocation but never blocks cookie deletion', async () => {
	const errors: unknown[][] = [];
	const originalConsoleError = console.error;
	console.error = (...args: unknown[]) => errors.push(args);
	try {
		await withFetch(async () => new Response(null, { status: 503 }), async () => {
			const cookies = new FakeCookies({
				rc_access_token: 'access-token',
				rc_refresh_token: 'refresh-token'
			});
			await expectRedirect(cookies);
			assert.deepEqual(cookies.deleted, [
				'rc_access_token',
				'rc_access_token_expiration',
				'rc_refresh_token'
			]);
		});
	} finally {
		console.error = originalConsoleError;
	}

	assert.ok(errors.some(args => String(args[0]).includes('Logout API returned 503')));
});
