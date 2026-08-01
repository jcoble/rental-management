import assert from 'node:assert/strict';
import { registerHooks } from 'node:module';
import { describe, it } from 'node:test';
import { buildExpenseListPagePath } from '../../api/endpoints/expense-list-path.ts';
import { buildLoanListPagePath } from '../../api/endpoints/loan-list-path.ts';
import { parseScanContext, scanHref } from '../../scan/scan-context.ts';
import {
	canReverseTenantLedgerEntry,
	tenantLedgerReversalReason,
	unitMoneyIdentity,
	unitMoneySectionGates
} from './money.ts';

const clientStub =
	'data:text/javascript,export const api={get(){throw new Error("API calls are not expected in this pure contract test")}};export function fetchApi(){throw new Error("API calls are not expected in this pure contract test")}';

registerHooks({
	resolve(specifier, context, nextResolve) {
		if (
			specifier === '../client' &&
			context.parentURL?.endsWith('/api/endpoints/tenant-accounts.ts')
		) {
			return { url: clientStub, shortCircuit: true };
		}
		return nextResolve(specifier, context);
	}
});

const {
	buildTenantAccountChargesPagePath,
	buildTenantAccountDepositsPagePath,
	buildTenantAccountEntriesPagePath,
	buildTenantAccountReversalsPath
} = await import('../../api/endpoints/tenant-accounts.ts');

describe('unit payment canonical identity contract', () => {
	it('models account, relationship, and legal provenance without an ambiguous lease id', () => {
		const accountWithoutAgreement = unitMoneyIdentity({
			tenantAccountId: 41,
			leaseManagementId: null
		});
		const relationshipWithoutAccount = unitMoneyIdentity({
			tenantAccountId: null,
			leaseManagementId: 73
		});

		assert.deepEqual(accountWithoutAgreement, {
			tenantAccountId: 41,
			leaseManagementId: null
		});
		assert.deepEqual(relationshipWithoutAccount, {
			tenantAccountId: null,
			leaseManagementId: 73
		});
	});

	it('scopes Unit Money reads, writes, and scans with account and relationship ids', () => {
		const identity = unitMoneyIdentity({
			tenantAccountId: 41,
			leaseManagementId: 73
		});
		const scanContext = parseScanContext(
			new URL(scanHref({
				type: 'Payment',
				tenantAccountId: identity.tenantAccountId ?? undefined,
				leaseManagementId: identity.leaseManagementId ?? undefined
			}), 'https://rentalcommand.local').searchParams
		);

		assert.equal(
			buildTenantAccountEntriesPagePath(identity.tenantAccountId!, {
				skip: 20,
				take: 20,
				sort: '-postedAtUtc'
			}),
			'/tenant-accounts/41/entries/page?skip=20&take=20&sort=-postedAtUtc'
		);
		assert.deepEqual(scanContext, {
			type: 'Payment',
			leaseManagementId: 73,
			tenantAccountId: 41
		});
	});

	it('keeps every Unit Money family independently server-paged', () => {
		assert.equal(
			buildTenantAccountEntriesPagePath(41, { skip: 20, take: 20, sort: '-postedAtUtc' }),
			'/tenant-accounts/41/entries/page?skip=20&take=20&sort=-postedAtUtc'
		);
		assert.equal(
			buildTenantAccountChargesPagePath(41, { skip: 40, take: 20, sort: '-effectiveOn' }),
			'/tenant-accounts/41/charges/page?skip=40&take=20&sort=-effectiveOn'
		);
		assert.equal(
			buildTenantAccountDepositsPagePath({
				tenantAccountId: 41,
				skip: 60,
				take: 20,
				sort: '-createdAtUtc'
			}),
			'/tenant-accounts/deposits/page?tenantAccountId=41&skip=60&take=20&sort=-createdAtUtc'
		);
		assert.equal(
			buildTenantAccountReversalsPath(41),
			'/tenant-accounts/41/reversals'
		);
		assert.equal(
			buildExpenseListPagePath(9, {
				operationalScope: 'Unit',
				unitId: 17,
				skip: 80,
				take: 20,
				sort: '-incurredAt'
			}),
			'/expenses/page?skip=80&take=20&sort=-incurredAt&portfolioId=9&operationalScope=Unit&unitId=17'
		);
	});

	it('shows Property expenses and Financing only for persisted SingleRental', () => {
		assert.deepEqual(
			unitMoneySectionGates({
				rentalStructure: 'SingleRental',
				propertyId: 8,
				tenantAccountId: null
			}),
			{ tenantAccount: false, propertyExpenses: true, financing: true }
		);
		assert.equal(
			buildExpenseListPagePath(9, {
				operationalScope: 'Property',
				propertyId: 8,
				take: 20,
				sort: '-incurredAt'
			}),
			'/expenses/page?take=20&sort=-incurredAt&portfolioId=9&operationalScope=Property&propertyId=8'
		);
		assert.equal(
			buildLoanListPagePath({ propertyId: 8, take: 20, sort: '-startDate' }),
			'/loans/page?take=20&sort=-startDate&propertyId=8'
		);
		assert.deepEqual(
			unitMoneySectionGates({
				rentalStructure: 'MultiRental',
				propertyId: 8,
				tenantAccountId: 41
			}),
			{ tenantAccount: true, propertyExpenses: false, financing: false }
		);
		assert.deepEqual(
			unitMoneySectionGates({
				rentalStructure: null,
				propertyId: 8,
				tenantAccountId: null
			}),
			{ tenantAccount: false, propertyExpenses: false, financing: false }
		);
	});

	it('allows generic reversal only for non-payment tenant ledger rows', () => {
		assert.equal(
			canReverseTenantLedgerEntry({ entryType: 'OpeningBalance', reversesEntryId: null }),
			true
		);
		assert.equal(canReverseTenantLedgerEntry({ entryType: 'ManualCharge' }), true);
		assert.equal(
			canReverseTenantLedgerEntry({ entryType: 'OpeningBalance', hasReversal: true }),
			false
		);
		assert.equal(canReverseTenantLedgerEntry({ entryType: 'PaymentReceipt' }), false);
		assert.equal(canReverseTenantLedgerEntry({ entryType: 'Reversal', reversesEntryId: 10 }), false);
		assert.equal(canReverseTenantLedgerEntry({ entryType: 'TransferOut' }), false);
		assert.equal(
			canReverseTenantLedgerEntry({
				entryType: 'OpeningBalance',
				providerPaymentAttemptId: 22
			}),
			false
		);
	});

	it('seeds a bounded human-readable ledger reversal reason', () => {
		const reason = tenantLedgerReversalReason({
			entryType: 'OpeningBalance',
			description: 'Initial receivable created from manual lease setup'
		});

		assert.equal(reason, 'Reverse OpeningBalance: Initial receivable created from manual lease setup');
		assert.equal(
			tenantLedgerReversalReason({
				entryType: 'Adjustment',
				description: 'x'.repeat(600)
			}).length,
			500
		);
	});
});
