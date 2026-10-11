import type {
	NotificationDestination,
	NotificationNavigationIntent,
	NotificationResource
} from '$lib/api/types/notification';
import type { SelectedAccessContextSummary, WorkspaceExperience } from '$lib/types/user';

const ACTIONS = new Set(['Open', 'Review', 'Resolve']);
const DESTINATIONS = new Set<NotificationDestination>([
	'Home',
	'Notifications',
	'Rentals',
	'Owners',
	'Money',
	'Work',
	'Inbox',
	'UnitSummary',
	'UnitTenantLease',
	'UnitMoney',
	'UnitMaintenance',
	'UnitRecords',
	'TenantLedgerEntry',
	'Expense',
	'ScanDraft',
	'Message',
	'WorkOrder',
	'TechnicianWork',
	'LeasingRental',
	'LeasingApplication',
	'LeasingAppointment',
	'LeasingConversation',
	'LeasingMoveIn',
	'TenantAccount'
]);

export function notificationIntentUrl(
	intent: NotificationNavigationIntent | null | undefined,
	authority: SelectedAccessContextSummary | null | undefined,
	nowUtc = Date.now()
): string {
	const safeHome = experienceHome(authority?.activeExperience);
	if (
		!intent ||
		!authority ||
		'actionUrl' in intent ||
		!isKnownDestination(intent.destination) ||
		!ACTIONS.has(intent.action) ||
		!Number.isSafeInteger(intent.accessContextId) ||
		intent.accessContextId <= 0 ||
		!Number.isSafeInteger(intent.accessRevision) ||
		intent.accessRevision <= 0 ||
		typeof intent.expiresAtUtc !== 'string' ||
		!intent.expiresAtUtc.endsWith('Z')
	) {
		return safeHome;
	}

	const expiresAt = Date.parse(intent.expiresAtUtc);
	if (
		!Number.isFinite(expiresAt) ||
		expiresAt <= nowUtc ||
		intent.accessContextId !== authority.accessContextId ||
		intent.accessRevision !== authority.accessRevision ||
		intent.experience !== authority.activeExperience
	) {
		return safeFallback(intent.fallbackDestination, authority.activeExperience);
	}

	return resolveDestination(intent) ?? safeFallback(intent.fallbackDestination, authority.activeExperience);
}

function resolveDestination(intent: NotificationNavigationIntent): string | null {
	const resource = intent.resource;
	switch (intent.destination) {
		case 'Home':
			return experienceHome(intent.experience);
		case 'Notifications':
			return intent.experience === 'Tenant' ? '/portal/notifications' : experienceHome(intent.experience);
		case 'Rentals':
			return intent.experience === 'Leasing' ? '/leasing/rentals' : '/units';
		case 'Owners':
			return intent.experience === 'Owner' ? '/owner' : '/owners';
		case 'Money':
			return intent.experience === 'Tenant'
				? '/portal/payments'
				: intent.experience === 'Owner'
					? '/owner/statements'
					: '/accounting';
		case 'Work':
			return intent.experience === 'Maintenance' ? '/my-work' : '/maintenance';
		case 'Inbox':
			return experienceInbox(intent.experience);
		case 'UnitSummary':
			return unitRoute(resource, 'summary');
		case 'UnitTenantLease':
			return unitRoute(resource, 'tenant-lease');
		case 'UnitMoney':
			return unitRoute(resource, 'money');
		case 'UnitMaintenance':
			return unitRoute(resource, 'maintenance');
		case 'UnitRecords':
			return unitRoute(resource, 'records');
		case 'TenantLedgerEntry':
			return tenantLedgerRoute(intent.experience, intent.parentResource, resource);
		case 'Expense':
			return detailRoute('/accounting/expenses', resource, 'Expense');
		case 'ScanDraft':
			return detailRoute('/scan', resource, 'ScanDraft');
		case 'Message': {
			const id = kindId(resource, 'Conversation');
			return id === null
				? null
				: intent.experience === 'Tenant'
					? `/portal/messages?conversation=${id}`
					: intent.experience === 'Owner'
						? `/owner/messages?conversation=${id}`
						: `/messages/${id}`;
		}
		case 'WorkOrder': {
			const id = kindId(resource, 'WorkOrder');
			return id === null
				? null
				: intent.experience === 'Tenant'
					? `/portal/maintenance?workOrder=${id}`
					: `/maintenance/${id}`;
		}
		case 'TechnicianWork':
			return detailRoute('/my-work', resource, 'WorkOrder');
		case 'LeasingRental':
			return detailRoute('/leasing/rentals', resource, 'Unit');
		case 'LeasingApplication':
			return detailRoute('/leasing/applications', resource, 'RentalApplication');
		case 'LeasingAppointment':
			return detailRoute('/leasing/appointments', resource, 'Appointment');
		case 'LeasingConversation':
			return detailRoute('/leasing/conversations', resource, 'Conversation');
		case 'LeasingMoveIn':
			return detailRoute('/leasing/move-ins', resource, 'LeaseManagement');
		case 'TenantAccount': {
			const id = kindId(resource, 'TenantAccount');
			return id === null || intent.experience !== 'Tenant'
				? null
				: `/portal/payments?account=${id}`;
		}
		default: {
			const exhaustiveDestination: never = intent.destination;
			void exhaustiveDestination;
			return null;
		}
	}
}

function safeFallback(
	destination: NotificationDestination,
	experience: WorkspaceExperience
): string {
	return destination === 'Notifications' && experience === 'Tenant'
		? '/portal/notifications'
		: experienceHome(experience);
}

function experienceHome(experience: WorkspaceExperience | null | undefined): string {
	switch (experience) {
		case 'Leasing':
			return '/leasing';
		case 'Maintenance':
			return '/my-work';
		case 'Owner':
			return '/owner';
		case 'Tenant':
			return '/portal';
		case 'Management':
		default:
			return '/';
	}
}

function experienceInbox(experience: WorkspaceExperience): string {
	switch (experience) {
		case 'Leasing':
			return '/leasing/inbox';
		case 'Maintenance':
			return '/assignment-inbox';
		case 'Owner':
			return '/owner/messages';
		case 'Tenant':
			return '/portal/messages';
		case 'Management':
			return '/messages';
	}
}

function isKnownDestination(value: string): value is NotificationDestination {
	return DESTINATIONS.has(value as NotificationDestination);
}

function unitRoute(resource: NotificationResource | null, tab: string): string | null {
	const id = kindId(resource, 'Unit');
	return id === null ? null : `/units/${id}?tab=${tab}`;
}

function detailRoute(
	prefix: string,
	resource: NotificationResource | null,
	kind: string
): string | null {
	const id = kindId(resource, kind);
	return id === null ? null : `${prefix}/${id}`;
}

function kindId(resource: NotificationResource | null, kind: string): number | null {
	return resource?.kind === kind && Number.isSafeInteger(resource.id) && resource.id > 0
		? resource.id
		: null;
}

function tenantLedgerRoute(
	experience: WorkspaceExperience,
	parent: NotificationResource | null,
	resource: NotificationResource | null
): string | null {
	const accountId = kindId(parent, 'TenantAccount');
	const entryId = kindId(resource, 'TenantLedgerEntry');
	if (accountId === null || entryId === null) return null;
	return experience === 'Tenant'
		? `/portal/payments?account=${accountId}&entry=${entryId}`
		: `/tenant-accounts/${accountId}/entries/${entryId}`;
}
