import type {
	ReturnPossessionAccessDisposition,
	ReturnPossessionPartyDisposition
} from '$lib/api/endpoints/lease-managements';

/** The people and logins the move-out screen has to decide about. */
export interface MoveOutReturnContext {
	parties: { leaseManagementPartyId: number; role: string }[];
	activeTenantUserAccesses: { tenantUserAccessId: number }[];
}

export interface MoveOutReturnDefaults {
	parties: Record<number, ReturnPossessionPartyDisposition>;
	accesses: Record<number, ReturnPossessionAccessDisposition>;
}

/**
 * The normal move-out: everyone on the lease is done here and their app sign-in ends.
 * Guarantors are included; keeping one is a deliberate change the landlord makes by hand.
 */
export function defaultReturnDispositions(context: MoveOutReturnContext): MoveOutReturnDefaults {
	const parties: Record<number, ReturnPossessionPartyDisposition> = {};
	for (const party of context.parties) parties[party.leaseManagementPartyId] = 'EndMembership';

	const accesses: Record<number, ReturnPossessionAccessDisposition> = {};
	for (const access of context.activeTenantUserAccesses) {
		accesses[access.tenantUserAccessId] = 'RevokeNow';
	}

	return { parties, accesses };
}

/** Everything the close-out checklist needs to know, in the landlord's terms. */
export interface CloseOutState {
	unitId: number;
	tenantAccountId: number | null;
	keysReturned: boolean;
	/** Positive when the tenant still owes, negative when they are owed money back. */
	amountOwed: number;
	depositHeld: number;
	paperworkPending: boolean;
}

export type CloseOutRowId = 'keys' | 'deposit' | 'balance' | 'paperwork';

export interface CloseOutRow {
	id: CloseOutRowId;
	label: string;
	detail: string;
	done: boolean;
	href: string | null;
	actionLabel: string | null;
}

export interface CloseOutChecklist {
	rows: CloseOutRow[];
	allGreen: boolean;
}

function money(value: number) {
	return new Intl.NumberFormat('en-US', {
		style: 'currency',
		currency: 'USD',
		maximumFractionDigits: Number.isInteger(value) ? 0 : 2
	}).format(Math.abs(value));
}

/** The four things that must be true before a tenant's account can be closed for good. */
export function closeChecklist(state: CloseOutState): CloseOutChecklist {
	const depositHref = state.tenantAccountId ? `/deposits/${state.tenantAccountId}` : null;
	const accountHref = state.tenantAccountId
		? `/units/${state.unitId}?tab=money&view=tenant-account&tenantAccount=${state.tenantAccountId}`
		: null;

	const rows: CloseOutRow[] = [
		{
			id: 'keys',
			label: 'Keys are back',
			detail: state.keysReturned
				? 'The tenant handed the keys back and moved out.'
				: 'Record the keys as returned on the Tenant & lease tab first.',
			done: state.keysReturned,
			href: null,
			actionLabel: null
		},
		{
			id: 'deposit',
			label: 'Deposit settled',
			detail:
				state.depositHeld === 0
					? 'None of the deposit is still being held.'
					: `${money(state.depositHeld)} of the deposit is still being held.`,
			done: state.depositHeld === 0,
			href: state.depositHeld === 0 ? null : depositHref,
			actionLabel: state.depositHeld === 0 ? null : 'Return the deposit'
		},
		{
			id: 'balance',
			label: 'Nothing owed either way',
			detail:
				state.amountOwed === 0
					? 'The tenant owes nothing and is owed nothing.'
					: state.amountOwed > 0
						? `The tenant still owes ${money(state.amountOwed)}.`
						: `${money(state.amountOwed)} is still owed back to the tenant.`,
			done: state.amountOwed === 0,
			href: state.amountOwed === 0 ? null : accountHref,
			actionLabel: state.amountOwed === 0 ? null : "Open the tenant's account"
		},
		{
			id: 'paperwork',
			label: 'Nothing else pending',
			detail: state.paperworkPending
				? 'A lease document is still waiting to be finished or signed.'
				: 'No lease documents or signatures are waiting.',
			done: !state.paperworkPending,
			href: null,
			actionLabel: null
		}
	];

	return { rows, allGreen: rows.every((row) => row.done) };
}
