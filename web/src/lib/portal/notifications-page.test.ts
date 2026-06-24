import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { describe, it } from 'node:test';

describe('portal notifications page', () => {
	it('marks notifications read when a tenant opens them from the full page', () => {
		const source = readFileSync(
			new URL('../../routes/(portal)/portal/notifications/+page.svelte', import.meta.url),
			'utf8'
		);

		assert.match(source, /notificationStore\.markAsRead\(item\.id\)/);
		assert.match(source, /notificationStore\.refresh\(\)/);
		assert.match(source, /goto\(portalActionUrl\(item\.actionUrl\)/);
		assert.doesNotMatch(source, /<a\s+href=\{portalActionUrl\(item\.actionUrl\)\}/);
	});
});
