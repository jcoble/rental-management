import type { Expense } from '$lib/types';
import { api } from '../client';
import { buildListQuery, type ListParams } from '../list-params';
import type { CapitalAsset, CapitalizeExpenseRequest } from './capital-assets';
import { buildExpenseListPagePath, type ExpenseListParams } from './expense-list-path';
import { idempotentMutation } from '../idempotency';

export interface ExpenseListResponse {
	items: Expense[];
	totalCount: number;
	skip: number;
	take: number;
}

export const expenses = {
	list: (portfolioId: number, params?: ExpenseListParams) => {
		const {
			propertyId,
			unitId,
			workOrderId,
			workOrderLinkedOnly,
			incurredFrom,
			incurredTo,
			dueFrom,
			dueTo,
			paidFrom,
			paidTo,
			...list
		} = params ?? {};
		return api.get<Expense[]>(
			`/expenses${buildListQuery(list, {
				portfolioId,
				propertyId,
				unitId,
				workOrderId,
				workOrderLinkedOnly: workOrderLinkedOnly ? 'true' : undefined,
				incurredFrom,
				incurredTo,
				dueFrom,
				dueTo,
				paidFrom,
				paidTo
			})}`
		);
	},
	listPage: (portfolioId: number, params?: ExpenseListParams) => {
		return api.get<ExpenseListResponse>(buildExpenseListPagePath(portfolioId, params));
	},
	get: (id: number) => api.get<Expense>(`/expenses/${id}`),
	create: (data: Record<string, unknown>) =>
		idempotentMutation(`expenses:create:${JSON.stringify(data)}`, (key) =>
			api.post<Expense>('/expenses', data, {
				headers: { 'Idempotency-Key': key }
			})
		),
	update: (id: number, data: Record<string, unknown>) =>
		idempotentMutation(`expenses:update:${id}:${JSON.stringify(data)}`, (key) =>
			api.patch<Expense>(`/expenses/${id}`, data, {
				headers: { 'Idempotency-Key': key }
			})
		),
	capitalize: (id: number, data: CapitalizeExpenseRequest) =>
		api.post<CapitalAsset>(`/expenses/${id}/capitalize`, data),
	delete: (id: number) =>
		idempotentMutation(`expenses:delete:${id}`, (key) =>
			api.delete(`/expenses/${id}`, { headers: { 'Idempotency-Key': key } })
		)
	// Expense totals (by Schedule E category + grand total) live on the accounting summary;
	// use the `accounting` endpoint module rather than an expenses-only summary route.
};
