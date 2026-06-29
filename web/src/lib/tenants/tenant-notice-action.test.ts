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
			readTenantNoticeAction(new URLSearchParams('action=create-notice&noticeType=RenewalOffer')),
			{ noticeType: 'RenewalOffer' }
		);
	});

	test('opens the notice dialog without forcing a type when only action is present', () => {
		assert.deepEqual(readTenantNoticeAction(new URLSearchParams('action=create-notice')), {});
	});

	test('ignores unsupported notice types and unrelated actions', () => {
		assert.equal(readTenantNoticeAction(new URLSearchParams('noticeType=RenewalOffer')), null);
		assert.equal(readTenantNoticeAction(new URLSearchParams('action=edit&noticeType=RenewalOffer')), null);
	});

	test('keys handled notice actions by tenant and forced type', () => {
		assert.equal(
			tenantNoticeActionKey(7, { noticeType: 'RenewalOffer' }),
			'7:RenewalOffer'
		);
		assert.equal(tenantNoticeActionKey(7, {}), '7:all');
		assert.notEqual(
			tenantNoticeActionKey(7, { noticeType: 'RenewalOffer' }),
			tenantNoticeActionKey(8, { noticeType: 'RenewalOffer' })
		);
	});

	test('reads a LateRentNotice action from URL params', () => {
		assert.deepEqual(
			readTenantNoticeAction(new URLSearchParams('action=create-notice&noticeType=LateRentNotice')),
			{ noticeType: 'LateRentNotice' }
		);
	});

	test('recognizes the two new forceable notice types', () => {
		assert.deepEqual(
			readTenantNoticeAction(new URLSearchParams('action=create-notice&noticeType=RentReminder')),
			{ noticeType: 'RentReminder' }
		);

		assert.deepEqual(
			readTenantNoticeAction(new URLSearchParams('action=create-notice&noticeType=MonthToMonthConversion')),
			{ noticeType: 'MonthToMonthConversion' }
		);
	});

	test('clears handled notice action params while preserving other URL state', () => {
		assert.equal(
			clearTenantNoticeActionUrl(
				new URL('https://rental.local/tenants/7?action=create-notice&noticeType=RenewalOffer&tab=leases')
			),
			'/tenants/7?tab=leases'
		);
	});
});
