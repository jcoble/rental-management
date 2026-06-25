import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { describe, test } from 'node:test';

describe('unit lifecycle next action handoff', () => {
	test('renders next-best-action as a real link using the API-provided href', () => {
		const railSource = readFileSync(new URL('./LifecycleRail.svelte', import.meta.url), 'utf8');

		assert.match(railSource, /data-testid="next-best-action"/);
		assert.match(railSource, /<a\s+[^>]*href=\{nextBestAction\.href\}/s);
		assert.doesNotMatch(railSource, /data-testid="next-best-action"[\s\S]*?onclick=\{/);
	});

	test('passes the unit dashboard next action through to the lifecycle rail', () => {
		const pageSource = readFileSync(
			new URL('../../../routes/(protected)/units/[id]/+page.svelte', import.meta.url),
			'utf8'
		);

		assert.match(pageSource, /<LifecycleRail[^>]+stage=\{dashboard\.lifecycleStage\}/s);
		assert.match(pageSource, /<LifecycleRail[^>]+nextBestAction=\{dashboard\.nextBestAction\}/s);
	});

	test('tenant renewal notice deep links open the notice dialog with the forced notice type', () => {
		const tenantPageSource = readFileSync(
			new URL('../../../routes/(protected)/tenants/[id]/+page.svelte', import.meta.url),
			'utf8'
		);

		assert.match(tenantPageSource, /readTenantNoticeAction\(page\.url\.searchParams\)/);
		assert.match(tenantPageSource, /openNoticeDialog\(action\.noticeType\)/);
		assert.match(tenantPageSource, /data-testid="tenant-notice-dialog"/);
	});
});
