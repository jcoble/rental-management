import { api } from '../client';
import { buildListQuery } from '../list-params';

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
	return `/accounting/cash-flow${buildListQuery(undefined, {
		from: params.from,
		to: params.to,
		propertyId: params.propertyId,
		propertyIds: params.propertyIds
	})}`;
}

export const cashFlow = {
	get: (params?: CashFlowParams) =>
		api.get<CashFlowSummaryResponse>(buildCashFlowPath(params))
};
