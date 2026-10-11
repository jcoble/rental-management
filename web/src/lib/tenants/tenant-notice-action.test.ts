import assert from 'node:assert/strict';
import { describe, test } from 'node:test';
import {
	clearTenantNoticeActionUrl,
	readTenantNoticeAction,
	tenantNoticeActionKey,
} from './tenant-notice-action.ts';

describe('tenant notice deep-link action', () => {
	test('reads a forced renewal notice action from URL params', () => {
		assert.deepEqual(
			readTenantNoticeAction(new URLSearchParams('action=create-notice&noticeType=lease-renewal-offer')),
			{ noticeType: 'lease-renewal-offer' }
		);
	});

	test('opens the notice dialog without forcing a type when only action is present', () => {
		assert.deepEqual(readTenantNoticeAction(new URLSearchParams('action=create-notice')), {});
	});

	test('ignores unsupported notice types and unrelated actions', () => {
		assert.equal(readTenantNoticeAction(new URLSearchParams('noticeType=lease-renewal-offer')), null);
		assert.equal(readTenantNoticeAction(new URLSearchParams('action=edit&noticeType=lease-renewal-offer')), null);
	});

	test('keys handled notice actions by tenant and forced type', () => {
		assert.equal(
			tenantNoticeActionKey(7, { noticeType: 'lease-renewal-offer' }),
			'7:lease-renewal-offer'
		);
		assert.equal(tenantNoticeActionKey(7, {}), '7:all');
		assert.notEqual(
			tenantNoticeActionKey(7, { noticeType: 'lease-renewal-offer' }),
			tenantNoticeActionKey(8, { noticeType: 'lease-renewal-offer' })
		);
	});

	test('reads a past-due rent action from URL params', () => {
		assert.deepEqual(
			readTenantNoticeAction(new URLSearchParams('action=create-notice&noticeType=late-rent-late-fee')),
			{ noticeType: 'late-rent-late-fee' }
		);
	});

	test('recognizes the two new forceable notice types', () => {
		assert.deepEqual(
			readTenantNoticeAction(new URLSearchParams('action=create-notice&noticeType=rent-reminder')),
			{ noticeType: 'rent-reminder' }
		);

		assert.deepEqual(
			readTenantNoticeAction(new URLSearchParams('action=create-notice&noticeType=month-to-month-offer')),
			{ noticeType: 'month-to-month-offer' }
		);
	});

	test('clears handled notice action params while preserving other URL state', () => {
		assert.equal(
			clearTenantNoticeActionUrl(
				new URL('https://rental.local/tenants/7?action=create-notice&noticeType=lease-renewal-offer&tab=leases')
			),
			'/tenants/7?tab=leases'
		);
	});
});
