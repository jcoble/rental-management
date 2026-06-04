import { api } from '../client';
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
	title: string;
	description?: string | null;
	category?: string | null;
	recurrenceInterval: RecurrenceInterval;
	/** Calendar date of the next generation (ISO `YYYY-MM-DD...`). */
	nextDueDate: string;
	/** When the worker last spawned a work order from this task; null until first run. */
	lastGeneratedAtUtc?: string | null;
	isActive: boolean;
	priority: RecurringMaintenancePriority;
	createdAt: string;
	updatedAt: string;
	/** Stable selector for tests, e.g. `recurring-maintenance-1`. */
	testId: string;
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
	get: (id: number) => api.get<RecurringMaintenanceTask>(`/recurring-maintenance/${id}`),
	create: (data: CreateRecurringMaintenanceTask) =>
		api.post<RecurringMaintenanceTask>('/recurring-maintenance', data),
	update: (id: number, data: UpdateRecurringMaintenanceTask) =>
		api.patch<RecurringMaintenanceTask>(`/recurring-maintenance/${id}`, data),
	setActive: (id: number, isActive: boolean) =>
		api.patch<RecurringMaintenanceTask>(`/recurring-maintenance/${id}/active`, { isActive }),
	remove: (id: number) => api.delete(`/recurring-maintenance/${id}`),
};
