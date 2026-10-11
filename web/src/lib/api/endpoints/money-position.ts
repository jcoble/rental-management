import { api } from '../client';
import { buildListQuery } from '../list-params';

export interface MoneyPositionParams {
	from?: string;
	to?: string;
}

export interface MoneyPositionResponse {
	asOfUtc: string;
	fromUtc: string;
	toUtc: string;
	totalCashOnHand: number;
	tenantDepositsHeld: number;
	cashAfterTenantDeposits: number;
	rentStillOwed: number;
	loanBalance: number;
	bookEquity: number;
	cashReceived: number;
	cashPaid: number;
	netCashMovement: number;
	profitOrLoss: number;
	pastDueAmount: number;
	pastDueCount: number;
}

export function buildMoneyPositionPath(params: MoneyPositionParams = {}): string {
	return `/accounting/money-position${buildListQuery(undefined, {
		from: params.from,
		to: params.to
	})}`;
}

export const moneyPosition = {
	get: (params?: MoneyPositionParams) =>
		api.get<MoneyPositionResponse>(buildMoneyPositionPath(params))
};
