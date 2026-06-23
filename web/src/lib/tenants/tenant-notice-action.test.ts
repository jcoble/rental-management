import assert from 'node:assert/strict';
import { describe, test } from 'node:test';
import { readTenantNoticeAction } from './tenant-notice-action.ts';

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
		assert.deepEqual(readTenantNoticeAction(new URLSearchParams('action=create-notice&noticeType=LateRentNotice')), {});
		assert.equal(readTenantNoticeAction(new URLSearchParams('noticeType=RenewalOffer')), null);
		assert.equal(readTenantNoticeAction(new URLSearchParams('action=edit&noticeType=RenewalOffer')), null);
	});
});
