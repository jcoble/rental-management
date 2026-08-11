import assert from 'node:assert/strict';
import { register } from 'node:module';
import { test } from 'node:test';

register('./token-refresh-test-loader.mjs', import.meta.url);

const { serverRefreshToken } = await import('./token-refresh.ts');

test('concurrent refreshes share one rotation and the same result', async () => {
	const originalFetch = globalThis.fetch;
	const refreshToken = `refresh-concurrency-${Date.now()}`;
	let fetchCalls = 0;
	let releaseFetch!: () => void;
	const fetchReleased = new Promise<void>(resolve => {
		releaseFetch = resolve;
	});

	globalThis.fetch = async () => {
		fetchCalls += 1;
		await fetchReleased;
		return new Response(
			JSON.stringify({
				user: { id: 1, email: 'refresh@example.test', displayName: 'Refresh Test', emailVerified: true },
				accessToken: 'access-after-refresh',
				accessTokenExpiration: new Date(Date.now() + 900_000).toISOString(),
				access: {}
			}),
			{
				status: 200,
				headers: {
					'set-cookie': 'rc_refresh_token=rotated-refresh-token; Expires=Wed, 12 Aug 2026 00:00:00 GMT'
				}
			}
		);
	};

	try {
		const first = serverRefreshToken(refreshToken);
		const second = serverRefreshToken(refreshToken);
		releaseFetch();
		const [firstResult, secondResult] = await Promise.all([first, second]);

		assert.equal(fetchCalls, 1);
		assert.equal(firstResult?.accessToken, 'access-after-refresh');
		assert.equal(secondResult?.accessToken, 'access-after-refresh');
		assert.equal(firstResult?.newRefreshToken, 'rotated-refresh-token');
	} finally {
		globalThis.fetch = originalFetch;
	}
});
