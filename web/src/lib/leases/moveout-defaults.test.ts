import assert from 'node:assert/strict';
import { describe, it } from 'node:test';
import { closeChecklist, defaultReturnDispositions } from './moveout-defaults.ts';

describe('move-out defaults', () => {
	it('ends every membership and removes every login by default', () => {
		const defaults = defaultReturnDispositions({
			parties: [
				{ leaseManagementPartyId: 11, role: 'PrimaryTenant' },
				{ leaseManagementPartyId: 12, role: 'CoTenant' },
				{ leaseManagementPartyId: 13, role: 'Guarantor' }
			],
			activeTenantUserAccesses: [{ tenantUserAccessId: 91 }, { tenantUserAccessId: 92 }]
		});

		assert.deepEqual(defaults.parties, {
			11: 'EndMembership',
			12: 'EndMembership',
			13: 'EndMembership'
		});
		assert.deepEqual(defaults.accesses, { 91: 'RevokeNow', 92: 'RevokeNow' });
	});

	it('returns empty defaults when nobody is left on the lease', () => {
		const defaults = defaultReturnDispositions({ parties: [], activeTenantUserAccesses: [] });
		assert.deepEqual(defaults.parties, {});
		assert.deepEqual(defaults.accesses, {});
	});
});

const settled = {
	unitId: 5,
	tenantAccountId: 77,
	keysReturned: true,
	amountOwed: 0,
	depositHeld: 0,
	paperworkPending: false
};

describe('close-out checklist', () => {
	it('is all green once the keys, deposit, balance, and paperwork are settled', () => {
		const checklist = closeChecklist(settled);
		assert.equal(checklist.allGreen, true);
		assert.deepEqual(
			checklist.rows.map((row) => row.id),
			['keys', 'deposit', 'balance', 'paperwork']
		);
		assert.ok(checklist.rows.every((row) => row.done));
	});

	it('flags the keys row and blocks closing while the keys are not back', () => {
		const checklist = closeChecklist({ ...settled, keysReturned: false });
		assert.equal(checklist.allGreen, false);
		assert.equal(checklist.rows.find((row) => row.id === 'keys')?.done, false);
	});

	it('links a held deposit to its return page', () => {
		const checklist = closeChecklist({ ...settled, depositHeld: 950 });
		const deposit = checklist.rows.find((row) => row.id === 'deposit');
		assert.equal(checklist.allGreen, false);
		assert.equal(deposit?.done, false);
		assert.equal(deposit?.href, '/deposits/77');
		assert.match(deposit?.detail ?? '', /\$950/);
	});

	it('links an unsettled balance, in either direction, to the tenant account', () => {
		const owed = closeChecklist({ ...settled, amountOwed: 120 });
		const credit = closeChecklist({ ...settled, amountOwed: -45 });
		const owedRow = owed.rows.find((row) => row.id === 'balance');
		assert.equal(owed.allGreen, false);
		assert.equal(owedRow?.done, false);
		assert.equal(owedRow?.href, '/units/5?tab=money&view=tenant-account&tenantAccount=77');
		assert.equal(credit.allGreen, false);
		assert.equal(credit.rows.find((row) => row.id === 'balance')?.done, false);
	});

	it('flags unfinished lease paperwork', () => {
		const checklist = closeChecklist({ ...settled, paperworkPending: true });
		assert.equal(checklist.allGreen, false);
		assert.equal(checklist.rows.find((row) => row.id === 'paperwork')?.done, false);
	});

	it('drops the money links when there is no tenant account yet', () => {
		const checklist = closeChecklist({ ...settled, tenantAccountId: null });
		assert.equal(checklist.rows.find((row) => row.id === 'deposit')?.href, null);
		assert.equal(checklist.rows.find((row) => row.id === 'balance')?.href, null);
	});

	it('never mentions system jargon in the rows a landlord reads', () => {
		const words =
			/disposition|possession|relationship|lifecycle|ledger|database|server|receivable|atomic/i;
		for (const row of closeChecklist({
			unitId: 5,
			tenantAccountId: 77,
			keysReturned: false,
			amountOwed: 120,
			depositHeld: 950,
			paperworkPending: true
		}).rows) {
			assert.doesNotMatch(row.label, words);
			assert.doesNotMatch(row.detail, words);
			assert.doesNotMatch(row.actionLabel ?? '', words);
		}
	});
});
