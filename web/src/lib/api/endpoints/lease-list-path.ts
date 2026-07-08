import { buildListQuery, type ListParams } from '../list-params.ts';

export interface LeaseListParams extends ListParams {
	tenantId?: number;
	propertyId?: number;
	unitId?: number;
	status?: string;
	startFrom?: string;
	startTo?: string;
	endFrom?: string;
	endTo?: string;
	activeOn?: string;
	activeFrom?: string;
	activeTo?: string;
}

export type LeaseListPageParams = LeaseListParams;

export function buildLeaseListPath(portfolioId: number, params?: LeaseListParams): string {
	const { list, extra } = splitLeaseListParams(portfolioId, params);
	return `/leases${buildListQuery(list, extra)}`;
}

export function buildLeaseListPagePath(portfolioId: number, params?: LeaseListPageParams): string {
	const { list, extra } = splitLeaseListParams(portfolioId, params);
	return `/leases/page${buildListQuery(list, extra)}`;
}

function splitLeaseListParams(portfolioId: number, params?: LeaseListParams) {
	const {
		tenantId,
		propertyId,
		unitId,
		status,
		startFrom,
		startTo,
		endFrom,
		endTo,
		activeOn,
		activeFrom,
		activeTo,
		...list
	} = params ?? {};

	return {
		list,
		extra: {
			portfolioId,
			tenantId,
			propertyId,
			unitId,
			status,
			startFrom,
			startTo,
			endFrom,
			endTo,
			activeOn,
			activeFrom,
			activeTo
		}
	};
}
