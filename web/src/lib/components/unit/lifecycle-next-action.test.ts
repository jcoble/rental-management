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

	test('move-in next action funds the prepared canonical deposit account and completes the appointment', () => {
		const pageSource = readFileSync(
			new URL('../../../routes/(protected)/units/[id]/+page.svelte', import.meta.url),
			'utf8'
		);

		assert.match(pageSource, /import \{ securityDeposits \} from '\$lib\/api\/endpoints\/securityDeposits';/);
		assert.match(pageSource, /import \{ appointments \} from '\$lib\/api\/endpoints\/appointments';/);
		assert.match(pageSource, /page\.url\.searchParams\.get\('action'\) === 'confirm-move-in'/);
		assert.match(pageSource, /data-testid="unit-move-in-dialog"/);
		assert.match(pageSource, /securityDeposits\.list\(lease\.leaseManagementId\)/);
		assert.match(pageSource, /securityDeposits\.fund\(account\.tenantAccountId, operationKey/);
		assert.match(pageSource, /securityDepositAccountId: account\.id/);
		assert.match(pageSource, /paymentMethodSummary: moveInDepositPaymentMethod\.trim\(\)/);
		assert.doesNotMatch(pageSource, /securityDeposits\.create/);
		assert.match(pageSource, /appointments\.update\(moveInAppointment\.id,\s*\{\s*status: 'Completed'\s*\}\)/s);
		assert.match(pageSource, /queryClient\.invalidateQueries\(\{ queryKey: \['unit-dashboard', id\] \}\)/);
	});

	test('tenant renewal notice deep links open the notice dialog with the forced notice type', () => {
		const tenantPageSource = readFileSync(
			new URL('../../../routes/(protected)/tenants/[id]/+page.svelte', import.meta.url),
			'utf8'
		);
		const tenantNoticeDialogSource = readFileSync(
			new URL('../notices/TenantNoticeDialog.svelte', import.meta.url),
			'utf8'
		);

		assert.match(tenantPageSource, /readTenantNoticeAction\(page\.url\.searchParams\)/);
		assert.match(tenantPageSource, /openNoticeDialog\(action\.noticeType\)/);
		assert.match(tenantPageSource, /<TenantNoticeDialog[\s\S]*bind:open=\{showNoticeDialog\}/);
		assert.match(tenantPageSource, /<TenantNoticeDialog[\s\S]*initialNoticeType=\{noticeDialogType\}/);
		assert.match(tenantNoticeDialogSource, /data-testid="tenant-notice-dialog"/);
	});

	test('tenant renewal notice deep links can be handled more than once', () => {
		const tenantPageSource = readFileSync(
			new URL('../../../routes/(protected)/tenants/[id]/+page.svelte', import.meta.url),
			'utf8'
		);

		assert.match(tenantPageSource, /tenantNoticeActionKey\(id, action\)/);
		assert.match(tenantPageSource, /handledNoticeActionKey/);
		assert.match(tenantPageSource, /clearTenantNoticeActionUrl\(page\.url\)/);
		assert.doesNotMatch(tenantPageSource, /noticeActionHandled/);
		assert.doesNotMatch(tenantPageSource, /tenantLeases\.filter\(\(lease\) => lease\.status === 'Active'\)/);
	});

	test('move-out and turnover stages open the dedicated turnover workspace', () => {
		const railSource = readFileSync(new URL('./LifecycleRail.svelte', import.meta.url), 'utf8');
		const pageSource = readFileSync(
			new URL('../../../routes/(protected)/units/[id]/+page.svelte', import.meta.url),
			'utf8'
		);

		assert.match(railSource, /key: 'MoveOut'[\s\S]*tab: 'turnover'/);
		assert.match(railSource, /key: 'Turnover'[\s\S]*tab: 'turnover'/);
		assert.match(pageSource, /data-testid="tab-turnover"/);
		assert.match(pageSource, /<TurnoverTab \{dashboard\} onScan=\{goScan\} \/>/);
	});
});
