import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { describe, test } from 'node:test';

describe('Unit destination history contract', () => {
	const pageSource = readFileSync(
		new URL('../../../routes/(protected)/units/[id]/+page.svelte', import.meta.url),
		'utf8',
	);
	const ledgerSource = readFileSync(new URL('./tabs/LedgerTab.svelte', import.meta.url), 'utf8');
	const listingSource = readFileSync(new URL('./tabs/ListingTab.svelte', import.meta.url), 'utf8');

	test('uses subordinate section switchers so each destination shows one focused workspace', () => {
		for (const surface of ['unit-leasing-surface', 'unit-tenant-lease-surface', 'unit-maintenance-surface', 'unit-documents-history-surface']) {
			assert.match(pageSource, new RegExp(`data-testid="${surface}"`));
		}
		assert.match(ledgerSource, /data-testid="unit-money-surface"/);
		for (const subnav of ['unit-leasing-subnav', 'unit-tenant-lease-subnav', 'unit-maintenance-subnav', 'unit-documents-history-subnav']) {
			assert.match(pageSource, new RegExp(`data-testid="${subnav}"`));
		}
		assert.match(ledgerSource, /data-testid="unit-money-subnav"/);
		assert.equal(pageSource.match(/m3-tabs-list/g)?.length, 1);
		assert.doesNotMatch(ledgerSource, /m3-tabs-(?:list|trigger)/);
		assert.match(pageSource, /class=\{UNIT_SUBNAV_LIST_CLASS\}/);
		assert.match(ledgerSource, /class=\{UNIT_SUBNAV_LIST_CLASS\}/);
		assert.match(ledgerSource, /pushState/);
	});

	test('uses validated views as the visible panel authority without scroll-to-section navigation', () => {
		for (const section of ['applications', 'residents', 'inspections', 'recurring', 'turnover', 'history']) {
			assert.match(pageSource, new RegExp(`data-testid="unit-${section}-section"`));
		}
		assert.match(ledgerSource, /page\.url\.searchParams\.get\('view'\)/);
		assert.match(ledgerSource, /operating-costs/);
		assert.match(pageSource, /pushState\(`\$\{url\.pathname\}\$\{url\.search\}`, \{/);
		assert.match(pageSource, /unitTab: destination\.tab/);
		assert.match(pageSource, /unitView: destination\.view \?\? null/);
		assert.doesNotMatch(pageSource, /scrollIntoView/);
	});

	test('uses validated shallow state with the landing URL as its fallback authority', () => {
		assert.match(pageSource, /import \{ untrack \} from 'svelte';/);
		assert.match(pageSource, /const activeDestination = \$derived\(resolveUnitDestination\(/);
		assert.match(pageSource, /const activeTab = \$derived\(activeDestination\.tab\);/);
		assert.match(pageSource, /const activeView = \$derived\(activeDestination\.view\);/);
		assert.match(pageSource, /page\.state\.unitTab \?\? page\.url\.searchParams\.get\('tab'\)/);
		assert.match(pageSource, /page\.state\.unitView \?\? page\.url\.searchParams\.get\('view'\)/);
		assert.doesNotMatch(pageSource, /import \* as Tabs/);
		assert.doesNotMatch(pageSource, /<Tabs\.(?:Root|Content)/);
		assert.doesNotMatch(pageSource, /let activeTab = \$state/);
		assert.doesNotMatch(pageSource, /let activeView = \$state/);
		assert.doesNotMatch(pageSource, /activeTab = destination\.tab/);
		assert.doesNotMatch(pageSource, /activeView = destination\.view/);
		assert.match(pageSource, /if \(`\$\{url\.pathname\}\$\{url\.search\}` === `\$\{page\.url\.pathname\}\$\{page\.url\.search\}`\) return;/);
	});

	test('keeps the move-in action guard outside its own reactive dependency', () => {
		assert.doesNotMatch(pageSource, /handledUnitLanding|document\.querySelector<HTMLElement>|scrollIntoView/);
		assert.match(pageSource, /if \(untrack\(\(\) => handledMoveInActionKey\) !== actionKey\)/);
		assert.match(pageSource, /url\.searchParams\.set\('tab', 'tenant-lease'\);/);
		assert.match(pageSource, /url\.searchParams\.set\('view', 'agreements'\);/);
		assert.match(pageSource, /url\.searchParams\.set\('action', 'confirm-move-in'\);/);
		assert.match(pageSource, /unitTab: 'tenant-lease'/);
		assert.match(pageSource, /unitView: 'agreements'/);
	});

	test('bounds listing loading and exposes a retryable error state before the empty state', () => {
		assert.match(listingSource, /retry: false/);
		assert.match(listingSource, /workspaceQuery\.isError/);
		assert.match(listingSource, /data-testid="unit-listing-error"/);
		assert.match(listingSource, /workspaceQuery\.refetch\(\)/);
		assert.match(
			listingSource,
			/\{#if workspaceQuery\.isLoading\}[\s\S]*\{:else if workspaceQuery\.isError\}[\s\S]*\{:else if !workspace\}/,
		);
	});
});
