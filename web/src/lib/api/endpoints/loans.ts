import { api } from '../client';
import { buildListQuery, type ListParams } from '../list-params';

/** Lifecycle of a per-property loan (mortgage). */
export type LoanStatus = 'Active' | 'PaidOff' | 'Closed';

/** Status of one generated amortization row. */
export type LoanPaymentStatus = 'Scheduled' | 'Paid';

/**
 * A per-property mortgage. Debt service is modeled separately from expenses so the
 * principal/interest/escrow split is explicit. `currentBalance` is a derived cache; the
 * authoritative amortization is the loan's LoanPayment rows.
 */
export interface Loan {
	id: number;
	portfolioId: number;
	propertyId: number;
	propertyName?: string | null;
	lender: string;
	originalAmount: number;
	currentBalance: number;
	annualInterestRatePct: number;
	termMonths: number;
	startDate: string;
	dayOfMonthDue: number;
	monthlyPrincipalInterest: number;
	monthlyEscrow: number;
	escrowCoversTaxes: boolean;
	escrowCoversInsurance: boolean;
	status: LoanStatus;
	notes?: string | null;
	createdAt: string;
	updatedAt: string;
	/** Stable selector for tests, e.g. `loan-1`. */
	testId: string;
}

/** One row of a loan's amortization schedule (read-only history). */
export interface LoanPayment {
	id: number;
	loanId: number;
	periodKey: string;
	dueDate: string;
	paidDate?: string | null;
	interestAmount: number;
	principalAmount: number;
	escrowAmount: number;
	totalAmount: number;
	balanceAfter: number;
	status: LoanPaymentStatus;
	/** True when the scheduled P&I did not cover the period's interest (flagged). */
	paymentDoesNotCoverInterest: boolean;
}

export const loans = {
	list: (params?: ListParams & { propertyId?: number }) => {
		const { propertyId, ...list } = params ?? {};
		return api.get<Loan[]>(`/loans${buildListQuery(list, { propertyId })}`);
	},
	get: (id: number) => api.get<Loan>(`/loans/${id}`),
	payments: (id: number) => api.get<LoanPayment[]>(`/loans/${id}/payments`),
	create: (data: Record<string, unknown>) => api.post<Loan>('/loans', data),
	update: (id: number, data: Record<string, unknown>) => api.patch<Loan>(`/loans/${id}`, data),
	remove: (id: number) => api.delete(`/loans/${id}`)
};
