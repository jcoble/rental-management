import { api } from '../client';
import { idempotentMutation } from '../idempotency';
import { buildListQuery, type ListParams } from '../list-params';

/** How often a recurring maintenance task spawns a fresh work order. */
export type RecurrenceInterval = 'Weekly' | 'Monthly' | 'Quarterly' | 'SemiAnnually' | 'Annually';

/** Priority stamped onto the work orders this task generates (mirrors WorkOrderPriority). */
export type RecurringMaintenancePriority = 'Low' | 'Normal' | 'High' | 'Emergency';

/**
 * A standing recurring-maintenance chore ("HVAC filter every 90 days"). The Engine's
 * recurring-maintenance worker turns each due task into a work order per period.
 * `nextDueDate` is a calendar date (no time component); ids reference in-portfolio
 * property/unit/vendor records — look up display names from the matching queries.
 */
export interface RecurringMaintenanceTask {
	id: number;
	portfolioId: number;
	propertyId: number;
	unitId?: number | null;
	vendorId?: number | null;
	propertyName?: string | null;
	unitNumber?: string | null;
	vendorName?: string | null;
	title: string;
	description?: string | null;
	category?: string | null;
	recurrenceInterval: RecurrenceInterval;
	/** Calendar date of the next generation (ISO `YYYY-MM-DD...`). */
	nextDueDate: string;
	/** Local time of day (`HH:mm:ss`) used for the generated work order's scheduled time. */
	scheduledTime?: string | null;
	estimatedCost?: number | null;
	monthlyEstimatedCost?: number | null;
	generatedWorkOrderCount: number;
	lastGeneratedWorkOrderId?: number | null;
	/** When the worker last spawned a work order from this task; null until first run. */
	lastGeneratedAtUtc?: string | null;
	isActive: boolean;
	priority: RecurringMaintenancePriority;
	createdAt: string;
	updatedAt: string;
	/** Stable selector for tests, e.g. `recurring-maintenance-1`. */
	testId: string;
}

export interface RecurringMaintenanceTaskListResponse {
	items: RecurringMaintenanceTask[];
	totalCount: number;
	skip: number;
	take: number;
}

export interface CreateRecurringMaintenanceTask {
	propertyId: number;
	unitId?: number | null;
	vendorId?: number | null;
	title: string;
	description?: string | null;
	category?: string | null;
	recurrenceInterval?: RecurrenceInterval;
	nextDueDate: string;
	scheduledTime?: string | null;
	estimatedCost?: number | null;
	isActive?: boolean;
	priority?: RecurringMaintenancePriority;
}

/** Update payload: every field optional; `propertyId` cannot be re-pointed. */
export interface UpdateRecurringMaintenanceTask {
	unitId?: number | null;
	vendorId?: number | null;
	title?: string;
	description?: string | null;
	category?: string | null;
	recurrenceInterval?: RecurrenceInterval;
	nextDueDate?: string;
	scheduledTime?: string | null;
	estimatedCost?: number | null;
	isActive?: boolean;
	priority?: RecurringMaintenancePriority;
}

export const recurringMaintenance = {
	list: (
		params?: ListParams & { propertyId?: number; activeOnly?: boolean }
	) => {
		const { propertyId, activeOnly, ...list } = params ?? {};
		return api.get<RecurringMaintenanceTask[]>(
			`/recurring-maintenance${buildListQuery(list, {
				propertyId,
				activeOnly: activeOnly == null ? undefined : String(activeOnly),
			})}`
		);
	},
	listPage: (
		params?: ListParams & { propertyId?: number; unitId?: number; activeOnly?: boolean }
	) => {
		const { propertyId, unitId, activeOnly, ...list } = params ?? {};
		return api.get<RecurringMaintenanceTaskListResponse>(
			`/recurring-maintenance/page${buildListQuery(list, {
				propertyId,
				unitId,
				activeOnly: activeOnly == null ? undefined : String(activeOnly),
			})}`
		);
	},
	get: (id: number) => api.get<RecurringMaintenanceTask>(`/recurring-maintenance/${id}`),
	create: (data: CreateRecurringMaintenanceTask) =>
		idempotentMutation(`recurring-maintenance:create:${JSON.stringify(data)}`, (key) =>
			api.post<RecurringMaintenanceTask>('/recurring-maintenance', data, {
				headers: { 'Idempotency-Key': key }
			})
		),
	update: (id: number, data: UpdateRecurringMaintenanceTask) =>
		idempotentMutation(`recurring-maintenance:update:${id}:${JSON.stringify(data)}`, (key) =>
			api.patch<RecurringMaintenanceTask>(`/recurring-maintenance/${id}`, data, {
				headers: { 'Idempotency-Key': key }
			})
		),
	setActive: (id: number, isActive: boolean) =>
		idempotentMutation(`recurring-maintenance:active:${id}:${isActive}`, (key) =>
			api.patch<RecurringMaintenanceTask>(`/recurring-maintenance/${id}/active`, { isActive }, {
				headers: { 'Idempotency-Key': key }
			})
		),
	remove: (id: number) =>
		idempotentMutation(`recurring-maintenance:delete:${id}`, (key) =>
			api.delete(`/recurring-maintenance/${id}`, { headers: { 'Idempotency-Key': key } })
		),
};
