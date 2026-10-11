import { api } from "../client";
import { idempotentMutation } from "../idempotency";
import {
  buildLoanListPagePath,
  buildLoanListPath,
  type LoanListParams,
} from "./loan-list-path";
import { buildListQuery } from "../list-params";

/** Lifecycle of a per-property loan (mortgage). */
export type LoanStatus = "Active" | "PaidOff" | "Closed";

/** Status of one generated amortization row. */
export type LoanPaymentStatus = "Scheduled" | "Paid";

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

export interface LoanListResponse {
  items: Loan[];
  totalCount: number;
  skip: number;
  take: number;
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

export interface LoanPaymentListParams {
  status?: LoanPaymentStatus;
  skip?: number;
  take?: number;
  sort?: string;
}

function buildLoanPaymentsPath(id: number, params?: LoanPaymentListParams): string {
	return `/loans/${id}/payments${buildListQuery(undefined, {
		status: params?.status,
		skip: params?.skip,
		take: params?.take,
		sort: params?.sort
	})}`;
}

export const loans = {
  list: (params?: LoanListParams) => api.get<Loan[]>(buildLoanListPath(params)),
  listPage: (params?: LoanListParams) =>
    api.get<LoanListResponse>(buildLoanListPagePath(params)),
  get: (id: number) => api.get<Loan>(`/loans/${id}`),
  payments: (id: number, params?: LoanPaymentListParams) =>
    api.get<LoanPayment[]>(buildLoanPaymentsPath(id, params)),
  postPayment: (loanId: number, paymentId: number) =>
    idempotentMutation(`loans:${loanId}:payments:${paymentId}:post`, (key) =>
      api.post<LoanPayment>(
        `/loans/${loanId}/payments/${paymentId}/post`,
        {},
        { headers: { "Idempotency-Key": key } }
      )
    ),
  create: (data: Record<string, unknown>) =>
    idempotentMutation(`loans:create:${JSON.stringify(data)}`, (key) =>
      api.post<Loan>("/loans", data, { headers: { "Idempotency-Key": key } })
    ),
  update: (id: number, data: Record<string, unknown>) =>
    idempotentMutation(`loans:update:${id}:${JSON.stringify(data)}`, (key) =>
      api.patch<Loan>(`/loans/${id}`, data, {
        headers: { "Idempotency-Key": key },
      })
    ),
  remove: (id: number) =>
    idempotentMutation(`loans:delete:${id}`, (key) =>
      api.delete(`/loans/${id}`, { headers: { "Idempotency-Key": key } })
    ),
};
