import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { describe, test } from 'node:test';

const dashboard = readFileSync(
	new URL('../../routes/(protected)/+page.svelte', import.meta.url),
	'utf8'
);
const briefing = readFileSync(
	new URL('../../routes/(protected)/DashboardBriefing.svelte', import.meta.url),
	'utf8'
);
const aiEndpoint = readFileSync(new URL('../api/endpoints/ai.ts', import.meta.url), 'utf8');

describe('dashboard loading contract', () => {
	test('opens on the ranked needs-attention list, with the assistant as a quiet line above it', () => {
		const headerPosition = dashboard.indexOf('data-testid="dashboard-today-header"');
		const briefingPosition = dashboard.indexOf('<DashboardBriefing');
		const attentionPosition = dashboard.indexOf('data-testid="needs-attention"');

		assert.ok(headerPosition >= 0, 'the dashboard should start with the Today header');
		assert.ok(briefingPosition > headerPosition, 'the assistant line sits under the header');
		assert.ok(
			attentionPosition > briefingPosition,
			'the needs-attention list must be the first section on the page'
		);
		// The rest of the story still follows underneath, never above, the list.
		for (const testid of ['dashboard-hero', 'dashboard-latest-messages', 'dashboard-activity-row']) {
			const position = dashboard.indexOf(`data-testid="${testid}"`);
			assert.ok(position >= 0, `${testid} should still be on the dashboard`);
			assert.ok(position > attentionPosition, `${testid} must sit below the needs-attention list`);
		}
	});

	test('fails a stalled optional AI polish quickly and offers a manual retry', () => {
		assert.match(briefing, /retry:\s*false/);
		assert.match(briefing, /briefingQuery\.refetch\(\)/);
		assert.match(aiEndpoint, /AI_BRIEFING_TIMEOUT_MS/);
		assert.match(
			aiEndpoint,
			/briefing:\s*\(\)\s*=>\s*fetchApi<BriefingResponse>\('\/ai\/briefing',\s*\{\s*timeoutMs:\s*AI_BRIEFING_TIMEOUT_MS\s*\}\)/
		);
	});

	test('lets the assistant line and the attention rows shrink and wrap in a narrow browser', () => {
		assert.match(briefing, /class="min-w-0 break-words"/);
		assert.match(briefing, /flex min-w-0 items-start gap-2/);
		assert.match(dashboard, /class="min-w-0 truncate text-sm font-medium text-foreground"/);
	});
});
