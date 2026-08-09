import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { describe, it } from 'node:test';

const dashboardSource = readFileSync(
	new URL('../../routes/(protected)/+page.svelte', import.meta.url),
	'utf8',
);
const messagesPageSource = readFileSync(
	new URL('../../routes/(protected)/messages/+page.svelte', import.meta.url),
	'utf8',
);
const noticesSource = readFileSync(
	new URL('../../routes/(protected)/notices/+page.svelte', import.meta.url),
	'utf8',
);
const messagesEndpointSource = readFileSync(
	new URL('./endpoints/messages.ts', import.meta.url),
	'utf8',
);
const gridUrlSource = readFileSync(
	new URL('../utils/grid-url-state.svelte.ts', import.meta.url),
	'utf8',
);

describe('route recovery contracts', () => {
	it('uses canonical conversation detail links from the dashboard', () => {
		assert.match(dashboardSource, /href=\{`\/messages\/\$\{thread\.id\}`\}/);
		assert.doesNotMatch(dashboardSource, /href="\/messages\?conversation=\{thread\.id\}"/);
		assert.match(noticesSource, /href=\{`\/messages\/\$\{draft\.conversationId\}`\}/);
	});

	it('renders dashboard recovery actions after a failed query', () => {
		assert.match(dashboardSource, /data-testid="dashboard-error"/);
		assert.match(dashboardSource, /data-testid="dashboard-retry"/);
		assert.match(dashboardSource, /dashboardQuery\.refetch\(\)/);
		assert.match(dashboardSource, /data-testid="dashboard-properties-link"/);
	});

	it('fetches a conversation before issuing the mark-read mutation', () => {
		const fetchIndex = messagesEndpointSource.indexOf('const conversation = await api.get<Conversation>');
		const markReadIndex = messagesEndpointSource.indexOf('idempotentMutation(`conversations:read:${id}`');
		assert.ok(fetchIndex >= 0);
		assert.ok(markReadIndex > fetchIndex);
		assert.match(messagesEndpointSource, /return conversation;/);
		assert.match(messagesPageSource, /data-testid="conversation-error-back"/);
		assert.match(messagesPageSource, /onclick=\{backToList\}/);
	});

	it('keeps grid filter changes shallow and hydration-safe', () => {
		assert.match(gridUrlSource, /import \{ replaceState \} from '\$app\/navigation';/);
		assert.match(gridUrlSource, /import \{ tick \} from 'svelte';/);
		assert.doesNotMatch(gridUrlSource, /\bgoto\(/);
		assert.match(gridUrlSource, /replaceState\(/);
	});
});
