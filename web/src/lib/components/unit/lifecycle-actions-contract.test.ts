import assert from 'node:assert/strict';
import { registerHooks } from 'node:module';
import { after, beforeEach, describe, it } from 'node:test';
import type {
	CancelPlannedRelationshipResponse,
	CloseTenantAccountResponse,
	TransferLeaseManagementResponse
} from '../../api/endpoints/lease-managements.ts';

interface CapturedTransportCall {
	path: string;
	options: RequestInit;
}

type TransportOutcome =
	| { disposition: 'resolve'; value: unknown }
	| { disposition: 'reject'; value: Error };

const clientStubStateKey = '__rentalCommandL08LifecycleClientStub';
const clientStubState: {
	calls: CapturedTransportCall[];
	outcomes: TransportOutcome[];
} = {
	calls: [],
	outcomes: []
};

Object.defineProperty(globalThis, clientStubStateKey, {
	configurable: true,
	value: clientStubState
});

const clientStub = `data:text/javascript,
const state = globalThis.${clientStubStateKey};
if (!state) throw new Error("The lifecycle contract transport state is unavailable");
export const api = new Proxy({}, { get() { return () => { throw new Error("API calls are not expected in this lifecycle contract test"); }; } });
export function downloadFile() { throw new Error("Downloads are not expected in this lifecycle contract test"); }
export function fetchApi(path, options) {
	state.calls.push({ path, options });
	const outcome = state.outcomes.shift();
	if (!outcome) throw new Error("No lifecycle contract transport outcome was queued");
	return outcome.disposition === "reject"
		? Promise.reject(outcome.value)
		: Promise.resolve(outcome.value);
}`;

registerHooks({
	resolve(specifier, context, nextResolve) {
		if (
			specifier === '../client' &&
			context.parentURL?.endsWith('/api/endpoints/lease-managements.ts')
		) {
			return { url: clientStub, shortCircuit: true };
		}
		return nextResolve(specifier, context);
	}
});

const { buildLeaseLifecycleActionRequest, leaseManagements } = await import(
	'../../api/endpoints/lease-managements.ts'
);

function enqueueOutcome(outcome: unknown) {
	clientStubState.outcomes.push(
		outcome instanceof Error
			? { disposition: 'reject', value: outcome }
			: { disposition: 'resolve', value: outcome }
	);
}

function resetClientStub() {
	clientStubState.calls.length = 0;
	clientStubState.outcomes.length = 0;
}

function capturedRequest(index = 0) {
	const call = clientStubState.calls[index];
	assert.ok(call);
	return {
		path: call.path,
		method: call.options.method,
		idempotencyKey: new Headers(call.options.headers).get('Idempotency-Key'),
		body: JSON.parse(String(call.options.body))
	};
}

beforeEach(() => resetClientStub());
after(() => Reflect.deleteProperty(globalThis, clientStubStateKey));

describe('unit lifecycle actions', () => {
	it('cancels with reason', async () => {
		const response: CancelPlannedRelationshipResponse = {
			leaseManagementId: 41,
			unitId: 19,
			canceledAtUtc: '2026-07-24T14:00:00Z',
			accountClosedAtUtc: '2026-07-24T14:00:00Z',
			canceledAgreementDraftIds: [701],
			canceledAddendumDraftIds: [801],
			revokedAccessIds: [901],
			retainedAccessIds: [902],
			replayed: false
		};
		enqueueOutcome(response);

		const result = await leaseManagements.cancelPlannedRelationship(
			41,
			{
				unitId: 19,
				cancellationReasonCode: 'APPLICANT_WITHDREW',
				cancellationNote: 'Household chose another rental.',
				draftCancellationReason: 'Applicant withdrew before possession.',
				accesses: [
					{ tenantUserAccessId: 901, disposition: 'RevokeNow' },
					{ tenantUserAccessId: 902, disposition: 'Retain' }
				]
			},
			'cancel-41'
		);

		assert.deepEqual(capturedRequest(), {
			path: '/lease-managements/41/cancel',
			method: 'POST',
			idempotencyKey: 'cancel-41',
			body: {
				unitId: 19,
				cancellationReasonCode: 'APPLICANT_WITHDREW',
				cancellationNote: 'Household chose another rental.',
				draftCancellationReason: 'Applicant withdrew before possession.',
				accesses: [
					{ tenantUserAccessId: 901, disposition: 'RevokeNow' },
					{ tenantUserAccessId: 902, disposition: 'Retain' }
				]
			}
		});
		assert.deepEqual(result, response);
	});

	it('transfers atomically', async () => {
		const request = {
			sourceUnitId: 19,
			destinationUnitId: 27,
			effectiveOn: '2026-08-01',
			plannedDestinationPossessionAtUtc: '2026-08-01T14:00:00Z',
			giveDestinationPossessionNow: true,
			possessionAgreementExceptionReason: 'Signed transfer agreement is on file.',
			destinationDocumentTemplateId: 137,
			carryTenantBalance: true,
			carrySecurityDeposit: true,
			transferReason: 'Household requested another Unit.'
		};
		const applied: TransferLeaseManagementResponse = {
			transferPublicId: 'transfer-public-id',
			sourceLeaseManagementId: 41,
			sourceUnitId: 19,
			destinationLeaseManagementId: 42,
			destinationUnitId: 27,
			destinationTenantAccountId: 149,
			destinationAgreementId: 159,
			replayed: false
		};
		const replayed = { ...applied, replayed: true };
		enqueueOutcome(applied);
		enqueueOutcome(replayed);

		const first = await leaseManagements.transferToUnit(41, request, 'transfer-41-to-27');
		const replay = await leaseManagements.transferToUnit(41, request, 'transfer-41-to-27');

		const expectedMutation = {
			path: '/lease-managements/41/transfer',
			method: 'POST',
			idempotencyKey: 'transfer-41-to-27',
			body: request
		};
		assert.deepEqual(capturedRequest(0), expectedMutation);
		assert.deepEqual(capturedRequest(1), expectedMutation);
		assert.deepEqual(first, applied);
		assert.deepEqual(replay, replayed);
		assert.equal(first.replayed, false);
		assert.equal(replay.replayed, true);
	});

	it('blocks invalid close', async () => {
		const request = {
			tenantAccountId: 149,
			closeReasonCode: 'TRANSFERRED',
			closeNote: 'Closed after transfer.'
		};
		const blockers = [
			'Possession must be returned before the Tenant Account can close.',
			'Resolve all receivable balances and unapplied credits with explicit ledger commands before closing.',
			'Refund, deduct, adjust, or transfer the held deposit before closing.',
			'Finish or cancel draft, signature, payment, and autopay workflows before closing.'
		];

		for (const blocker of blockers) {
			enqueueOutcome(new Error(blocker));
			await assert.rejects(
				leaseManagements.closeAccount(41, request, 'close-149'),
				(error: unknown) => error instanceof Error && error.message === blocker
			);
		}

		const closed: CloseTenantAccountResponse = {
			value: {
				outcome: 'Closed',
				leaseManagementId: 41,
				tenantAccountId: 149,
				closedAtUtc: '2026-07-24T15:00:00Z',
				error: null
			},
			replayed: false
		};
		const replayed = { ...closed, replayed: true };
		enqueueOutcome(closed);
		enqueueOutcome(replayed);
		assert.deepEqual(await leaseManagements.closeAccount(41, request, 'close-149'), closed);
		assert.deepEqual(await leaseManagements.closeAccount(41, request, 'close-149'), replayed);

		for (let index = 0; index < clientStubState.calls.length; index += 1) {
			assert.deepEqual(capturedRequest(index), {
				path: '/lease-managements/41/close-account',
				method: 'POST',
				idempotencyKey: 'close-149',
				body: request
			});
		}
		assert.equal(closed.value.outcome, 'Closed');
		assert.equal(closed.replayed, false);
		assert.equal(replayed.replayed, true);

		assert.deepEqual(
			buildLeaseLifecycleActionRequest(41, 'close-account', request, 'close-149'),
			{
				path: '/lease-managements/41/close-account',
				options: {
					method: 'POST',
					headers: { 'Idempotency-Key': 'close-149' },
					body: JSON.stringify(request)
				}
			}
		);
	});
});
