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
	test('renders the selected portfolio card before the independently queried Daily Briefing', () => {
		const briefingPosition = dashboard.indexOf('<DashboardBriefing');
		const portfolioCardPosition = dashboard.indexOf('data-testid="dashboard-hero"');

		assert.ok(briefingPosition >= 0, 'the dashboard should render the Daily Briefing component');
		assert.ok(
			portfolioCardPosition >= 0 && portfolioCardPosition < briefingPosition,
			'the selected portfolio card must be the first dashboard card'
		);
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

	test('lets briefing text and action cards shrink and wrap in a narrow browser', () => {
		assert.match(briefing, /class="grid min-w-0 gap-5 lg:grid-cols-5"/);
		assert.match(briefing, /class="min-w-0 lg:col-span-3"/);
		assert.match(
			briefing,
			/class="min-w-0 lg:col-span-2" data-testid="dashboard-briefing-actions"/
		);
		assert.match(briefing, /class="min-w-0 flex-1 break-words text-sm font-medium/);
	});
});
