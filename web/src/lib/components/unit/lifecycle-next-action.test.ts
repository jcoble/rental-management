import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { describe, test } from 'node:test';

describe('unit lifecycle next action handoff', () => {
	test('renders next-best-action as a real link using the API-provided href', () => {
		const overviewSource = readFileSync(new URL('./tabs/OverviewTab.svelte', import.meta.url), 'utf8');

		assert.match(overviewSource, /data-testid="next-best-action"/);
		assert.match(overviewSource, /<a\s+[^>]*href=\{dashboard\.nextBestAction\.href\}/s);
		assert.doesNotMatch(overviewSource, /<a\s+[^>]*data-testid="next-best-action"[^>]*onclick=\{/s);
	});

	test('keeps the next action in Summary without a clickable lifecycle rail', () => {
		const pageSource = readFileSync(
			new URL('../../../routes/(protected)/units/[id]/+page.svelte', import.meta.url),
			'utf8'
		);

		assert.match(pageSource, /<OverviewTab \{dashboard\} onOpenTab=\{setTab\} \/>/);
		assert.doesNotMatch(pageSource, /LifecycleRail/);
	});

	test('move-in next action uses one receipt-backed API command for deposit, possession, and appointment', () => {
		const pageSource = readFileSync(
			new URL('../../../routes/(protected)/units/[id]/+page.svelte', import.meta.url),
			'utf8'
		);

		assert.match(pageSource, /import \{ leaseManagements \} from '\$lib\/api\/endpoints\/lease-managements';/);
		assert.doesNotMatch(pageSource, /api\/endpoints\/securityDeposits/);
		assert.doesNotMatch(pageSource, /api\/endpoints\/appointments/);
		assert.match(pageSource, /page\.url\.searchParams\.get\('action'\) === 'confirm-move-in'/);
		assert.match(pageSource, /data-testid="unit-move-in-dialog"/);
		assert.match(pageSource, /leaseManagements\.confirmMoveIn\(/);
		assert.match(pageSource, /depositEffectiveOn: lease\.securityDeposit > 0/);
		assert.match(pageSource, /depositPaymentMethodSummary:/);
		assert.match(pageSource, /moveInAppointmentId: moveInAppointment\?\.id \?\? null/);
		assert.doesNotMatch(pageSource, /securityDeposits\.(?:get|fund|create|list)/);
		assert.doesNotMatch(pageSource, /appointments\.update/);
		assert.doesNotMatch(pageSource, /leaseManagements\.givePossession/);
		assert.match(pageSource, /operationKey: crypto\.randomUUID\(\)/);
		assert.match(pageSource, /showSuccess\('Possession given\. Move-in confirmed\.'\)/);
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

	test('the dedicated turnover workspace remains in the canonical Maintenance area', () => {
		const pageSource = readFileSync(
			new URL('../../../routes/(protected)/units/[id]/+page.svelte', import.meta.url),
			'utf8'
		);

		assert.match(pageSource, /data-testid="unit-turnover-section"/);
		assert.match(pageSource, /<TurnoverTab \{dashboard\} onScan=\{goScan\} \/>/);
	});
});
