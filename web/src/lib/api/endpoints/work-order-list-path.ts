import { buildListQuery, type ListParams } from '../list-params.ts';

export interface WorkOrderListParams extends ListParams {
	propertyId?: number;
	unitId?: number;
	vendorId?: number;
	status?: string;
	priority?: string;
	openOnly?: boolean;
	requestedFrom?: string;
	requestedTo?: string;
	scheduledFrom?: string;
	scheduledTo?: string;
	completedFrom?: string;
	completedTo?: string;
}

export function buildWorkOrderListPath(portfolioId: number, params?: WorkOrderListParams): string {
	const { list, extra } = splitWorkOrderListParams(portfolioId, params);
	return `/work-orders${buildListQuery(list, extra)}`;
}

export function buildWorkOrderListPagePath(portfolioId: number, params?: WorkOrderListParams): string {
	const { list, extra } = splitWorkOrderListParams(portfolioId, params);
	return `/work-orders/page${buildListQuery(list, extra)}`;
}

function splitWorkOrderListParams(portfolioId: number, params?: WorkOrderListParams) {
	const {
		propertyId,
		unitId,
		vendorId,
		status,
		priority,
		openOnly,
		requestedFrom,
		requestedTo,
		scheduledFrom,
		scheduledTo,
		completedFrom,
		completedTo,
		...list
	} = params ?? {};

	return {
		list,
		extra: {
			portfolioId,
			propertyId,
			unitId,
			vendorId,
			status,
			priority,
			openOnly: openOnly ? 'true' : undefined,
			requestedFrom,
			requestedTo,
			scheduledFrom,
			scheduledTo,
			completedFrom,
			completedTo
		}
	};
}
