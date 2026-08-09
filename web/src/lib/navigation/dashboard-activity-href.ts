import { resolveUnitUrlDestination } from '../components/unit/unit-tabs.ts';
import type { DashboardActivity } from '../types/index.ts';
import { recordHref, type RecordType } from './record-href.ts';

function recordTypeForEntity(entityType: string | null | undefined): RecordType | null {
	switch (entityType) {
		case 'WorkOrder': return 'workOrder';
		case 'Payment': return 'payment';
		case 'LeaseManagement': return 'leaseManagement';
		case 'Expense': return 'expense';
		case 'RentalApplication':
		case 'Application': return 'application';
		default: return null;
	}
}

export function dashboardActivityHref(activity: DashboardActivity): string | null {
	if (activity.entityId == null) return null;
	if (activity.type === 'LeaseAgreement' && activity.unitId != null) {
		return `/units/${activity.unitId}?tab=leasing`;
	}
	if (activity.type === 'TenantAccount' && activity.unitId != null) {
		return `/units/${activity.unitId}?tab=money&view=tenant-account&tenantAccount=${activity.entityId}`;
	}
	const recordType = recordTypeForEntity(activity.type);
	if (recordType) return recordHref(recordType, { id: activity.entityId, unitId: activity.unitId });
	switch (activity.type) {
		case 'Tenant': return `/tenants/${activity.entityId}`;
		case 'Unit': return `/units/${activity.entityId}`;
		case 'Property': return `/properties/${activity.entityId}`;
		case 'Vendor': return `/vendors/${activity.entityId}`;
		case 'OwnerEntity': return `/owners/${activity.entityId}`;
		case 'Appointment': return `/appointments/${activity.entityId}`;
		case 'Inspection': return `/maintenance/inspections/${activity.entityId}`;
		case 'SecurityDeposit': return `/deposits/${activity.entityId}`;
		default: return null;
	}
}

export function resolveDashboardActivityUnitHref(href: string) {
	return resolveUnitUrlDestination(new URL(href, 'https://rental.local'));
}
