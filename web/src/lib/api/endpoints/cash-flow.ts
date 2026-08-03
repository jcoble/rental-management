import { api } from '../client';

export interface CashFlowParams {
	from?: string;
	to?: string;
	propertyId?: number;
	propertyIds?: number[];
}

export interface PropertyCashFlow {
	propertyId: number;
	propertyName: string;
	income: number;
	operatingExpenses: number;
	noi: number;
	debtService: number;
	cashFlow: number;
	operatingExpenseDetails?: CashFlowDetailRow[];
	debtServiceDetails?: CashFlowDetailRow[];
}
export interface CashFlowDetailRow { label: string; amount: number; }
export interface MonthlyCashFlow { month: string; income: number; operatingExpenses: number; debtService: number; cashFlow: number; }

export interface CashFlowSummaryResponse {
	from: string;
	to: string;
	properties: PropertyCashFlow[];
	months?: MonthlyCashFlow[];
	totalIncome: number;
	totalOperatingExpenses: number;
	totalNoi: number;
	totalDebtService: number;
	totalCashFlow: number;
}

export function buildCashFlowPath(params: CashFlowParams = {}): string {
	const query = new URLSearchParams();
	if (params.from) query.set('from', params.from);
	if (params.to) query.set('to', params.to);
	if (params.propertyId != null) query.set('propertyId', String(params.propertyId));
	for (const propertyId of params.propertyIds ?? []) query.append('propertyIds', String(propertyId));
	const queryString = query.toString();
	return `/accounting/cash-flow${queryString ? `?${queryString}` : ''}`;
}

export const cashFlow = {
	get: (params?: CashFlowParams) =>
		api.get<CashFlowSummaryResponse>(buildCashFlowPath(params))
};
