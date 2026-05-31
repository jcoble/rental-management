export type PortfolioStatus = 'Onboarding' | 'Active' | 'Archived';
export type PropertyType = 'SingleFamily' | 'MultiFamily' | 'Condo' | 'Townhome' | 'Commercial' | 'MixedUse';
export type PropertyStatus = 'Active' | 'UnderMaintenance' | 'Inactive';
export type UnitStatus = 'Vacant' | 'Occupied' | 'Reserved' | 'Offline';
export type LeaseStatus = 'Draft' | 'Active' | 'NoticeGiven' | 'Expired' | 'Terminated';
export type PaymentType = 'Rent' | 'SecurityDeposit' | 'LateFee' | 'Utility' | 'Other';
export type PaymentStatus = 'Scheduled' | 'Paid' | 'Partial' | 'Late' | 'Waived';
export type ExpenseStatus = 'Pending' | 'Approved' | 'Paid';
export type WorkOrderPriority = 'Low' | 'Normal' | 'High' | 'Emergency';
export type WorkOrderStatus = 'New' | 'Scheduled' | 'InProgress' | 'WaitingParts' | 'Completed' | 'Cancelled';
export type AppointmentType = 'Showing' | 'MoveIn' | 'MoveOut' | 'Inspection' | 'MaintenanceVisit' | 'OwnerMeeting';
export type AppointmentStatus = 'Scheduled' | 'Confirmed' | 'Completed' | 'Cancelled' | 'NoShow';
export type InspectionType = 'MoveIn' | 'MoveOut' | 'Routine' | 'AnnualSafety';
export type InspectionStatus = 'Scheduled' | 'Completed' | 'NeedsFollowUp' | 'Cancelled';
export type UserRole = 'Admin' | 'Manager' | 'Agent' | 'Owner' | 'Tenant';
export type PortalMessageStatus = 'Open' | 'InProgress' | 'Resolved' | 'Closed';

export interface Portfolio {
	id: number;
	name: string;
	description?: string;
	managementCompanyName: string;
	timeZone: string;
	status: PortfolioStatus;
	settings?: string;
	propertyCount?: number;
	unitCount?: number;
	activeLeaseCount?: number;
	createdAt: string;
	updatedAt: string;
}

export interface Owner {
	id: number;
	portfolioId: number;
	name: string;
	email?: string;
	phone?: string;
	mailingAddress?: string;
	notes?: string;
	propertyCount?: number;
	createdAt: string;
	updatedAt: string;
}

export interface Property {
	id: number;
	portfolioId: number;
	ownerId?: number;
	name: string;
	type: PropertyType;
	status: PropertyStatus;
	addressLine1: string;
	addressLine2?: string;
	city: string;
	state: string;
	postalCode: string;
	yearBuilt?: number;
	managementFeePercent?: number;
	notes?: string;
	ownerName?: string;
	unitCount?: number;
	occupiedUnits?: number;
	createdAt: string;
	updatedAt: string;
}

export interface Unit {
	id: number;
	propertyId: number;
	unitNumber: string;
	floorPlan?: string;
	bedrooms: number;
	bathrooms: number;
	squareFeet?: number;
	marketRent: number;
	status: UnitStatus;
	notes?: string;
	createdAt: string;
	updatedAt: string;
}

export interface Tenant {
	id: number;
	portfolioId: number;
	firstName: string;
	lastName: string;
	fullName?: string;
	email?: string;
	phone?: string;
	emergencyContact?: string;
	dateOfBirth?: string;
	notes?: string;
	activeLeaseCount?: number;
	createdAt: string;
	updatedAt: string;
}

export interface Lease {
	id: number;
	portfolioId: number;
	propertyId: number;
	unitId: number;
	tenantId: number;
	leaseNumber: string;
	status: LeaseStatus;
	startDate: string;
	endDate: string;
	moveInDate?: string;
	moveOutDate?: string;
	monthlyRent: number;
	securityDeposit: number;
	lateFeeAmount: number;
	rentDueDay: number;
	notes?: string;
	tenantName?: string;
	propertyName?: string;
	unitNumber?: string;
	createdAt: string;
	updatedAt: string;
}

export interface Payment {
	id: number;
	portfolioId: number;
	leaseId: number;
	type: PaymentType;
	status: PaymentStatus;
	amount: number;
	dueDate: string;
	paidDate?: string;
	method?: string;
	externalReference?: string;
	notes?: string;
	tenantName?: string;
	leaseNumber?: string;
	createdAt: string;
	updatedAt: string;
}

export interface Expense {
	id: number;
	portfolioId: number;
	propertyId?: number;
	vendorId?: number;
	workOrderId?: number;
	category: string;
	description: string;
	status: ExpenseStatus;
	amount: number;
	subtotal?: number;
	taxAmount?: number;
	incurredAt: string;
	dueDate?: string;
	paidAt?: string;
	billableToOwner: boolean;
	notes?: string;
	receiptData?: string;
	propertyName?: string;
	vendorName?: string;
	workOrderTitle?: string;
	createdAt: string;
	updatedAt: string;
	hasReceipt?: boolean;
	receiptIsImage?: boolean;
}

// --- Owner statement types ---

export interface OwnerStatementSummary {
	ownerId: number;
	ownerName: string;
	netToOwner: number;
}

export interface OwnerStatementPropertyLine {
	propertyId: number;
	propertyName: string;
	rentalIncome: number;
	expenses: number;
	managementFee: number;
	netToOwner: number;
}

export interface OwnerStatementReport {
	ownerId: number;
	ownerName: string;
	year: number;
	properties: OwnerStatementPropertyLine[];
	totalIncome: number;
	totalExpenses: number;
	totalManagementFee: number;
	totalNetToOwner: number;
}

// --- Schedule E tax report types ---

export interface ScheduleECategoryAmount {
	category: string;
	amount: number;
}

export interface ScheduleEPropertyReport {
	propertyId: number;
	propertyName: string;
	rentalIncome: number;
	expensesByCategory: ScheduleECategoryAmount[];
	totalExpenses: number;
	netIncome: number;
}

export interface ScheduleEReport {
	year: number;
	properties: ScheduleEPropertyReport[];
	totalRentalIncome: number;
	totalExpenses: number;
	netIncome: number;
}

// ---

/** Read-only financial rollup from GET /api/v1/accounting/summary (AccountingSummaryResponse). */
export interface AccountingSummary {
	portfolioId: number;
	expensesByCategory: ScheduleECategoryTotal[];
	totalExpenses: number;
	payments: PaymentRollup;
}

export interface ScheduleECategoryTotal {
	category: number;
	categoryName: string;
	total: number;
	count: number;
}

export interface PaymentRollup {
	collected: number;
	outstanding: number;
	overdue: number;
	overdueCount: number;
}

export interface Vendor {
	id: number;
	portfolioId: number;
	name: string;
	serviceType: string;
	email?: string;
	phone?: string;
	taxId?: string;
	is1099Eligible: boolean;
	w9OnFile: boolean;
	preferred: boolean;
	notes?: string;
	createdAt: string;
	updatedAt: string;
}

export interface WorkOrder {
	id: number;
	portfolioId: number;
	propertyId: number;
	unitId?: number;
	tenantId?: number;
	leaseId?: number;
	vendorId?: number;
	title: string;
	description: string;
	category: string;
	priority: WorkOrderPriority;
	status: WorkOrderStatus;
	requestedAt: string;
	scheduledFor?: string;
	completedAt?: string;
	estimatedCost?: number;
	actualCost?: number;
	createdBy?: string;
	updatedAt: string;
	propertyName?: string;
	unitNumber?: string;
	tenantName?: string;
	vendorName?: string;
}

export interface Appointment {
	id: number;
	portfolioId: number;
	propertyId?: number;
	unitId?: number;
	leaseId?: number;
	tenantId?: number;
	title: string;
	prospectName?: string;
	prospectEmail?: string;
	type: AppointmentType;
	status: AppointmentStatus;
	scheduledStart: string;
	scheduledEnd?: string;
	assignedTo?: string;
	notes?: string;
	propertyName?: string;
	unitNumber?: string;
	tenantName?: string;
	createdAt: string;
	updatedAt: string;
}

export interface Inspection {
	id: number;
	portfolioId: number;
	propertyId: number;
	unitId?: number;
	leaseId?: number;
	type: InspectionType;
	status: InspectionStatus;
	scheduledFor: string;
	completedAt?: string;
	outcome?: string;
	notes?: string;
	propertyName?: string;
	unitNumber?: string;
	createdAt: string;
	updatedAt: string;
}

export interface ActivityLog {
	id: number;
	portfolioId: number;
	type: string;
	typeName: string;
	entityType?: string;
	entityId?: number;
	action?: string;
	description?: string;
	actor?: string;
	createdAt: string;
	testId?: string;
}

export interface Dashboard {
	portfolio: {
		id: number;
		name: string;
		managementCompanyName: string;
		status: PortfolioStatus;
		timeZone: string;
	};
	occupancy: {
		totalUnits: number;
		occupiedUnits: number;
		vacantUnits: number;
		reservedUnits: number;
		occupancyRate: number;
	};
	leasing: {
		totalLeases: number;
		activeLeases: number;
		expiringSoon: Array<{
			id: number;
			leaseNumber: string;
			tenant?: string;
			unit?: string;
			property?: string;
			endDate: string;
			monthlyRent: number;
		}>;
		byStatus: Record<string, number>;
	};
	accounting: {
		dueThisMonthAmount: number;
		paidThisMonthAmount: number;
		overdueAmount: number;
		expensesThisMonthAmount: number;
		netThisMonth: number;
	};
	maintenance: {
		openCount: number;
		emergencyCount: number;
		inProgressCount: number;
	};
	upcomingAppointments: Array<{
		id: number;
		title: string;
		type: AppointmentType;
		status: AppointmentStatus;
		scheduledStart: string;
		assignedTo?: string;
		propertyId?: number;
		unitId?: number;
	}>;
	recentActivity: ActivityLog[];
}

export interface AuthUser {
	id: number;
	portfolioId: number;
	displayName: string;
	email: string;
	role: UserRole;
	ownerId?: number;
	tenantId?: number;
	lastLoginAt?: string;
}

export type SecurityDepositStatus = 'Held' | 'PartiallyReturned' | 'Returned';

export interface DepositDeduction {
	reason: string;
	amount: number;
	notes?: string;
}

export interface SecurityDepositHolding {
	id: number;
	leaseId: number;
	leaseNumber?: string;
	amount: number;
	status: SecurityDepositStatus;
	heldAt: string;
	returnedAt?: string;
	returnedAmount?: number;
	deductions: DepositDeduction[];
	totalDeductions: number;
	netRefund: number;
	notes?: string;
}

export interface PortalMessage {
	id: number;
	portfolioId: number;
	userAccountId: number;
	author?: string;
	authorRole?: UserRole;
	propertyId?: number;
	unitId?: number;
	subject: string;
	body: string;
	status: PortalMessageStatus;
	reply?: string;
	createdAt: string;
	updatedAt: string;
}

/** Matches the AdminUsersController TeamMemberDto. */
export interface TeamMember {
	id: number;
	email: string;
	displayName?: string;
	role: UserRole;
	isActive: boolean;
	ownerId?: number;
	tenantId?: number;
	createdAt: string;
}

/** Returned from POST /api/v1/admin/users — includes the one-time generated password. */
export interface CreateTeamMemberResponse extends TeamMember {
	generatedPassword?: string;
}

// --- Analytics / Insights types ---

export interface MonthlyPoint {
	/** "yyyy-MM" */
	month: string;
	income: number;
	expenses: number;
	net: number;
}

export interface CountAmount {
	count: number;
	amount: number;
}

export interface PriorityCount {
	priority: string;
	count: number;
}

export interface AnalyticsOverview {
	totalUnits: number;
	occupiedUnits: number;
	/** 0–100 */
	occupancyRate: number;
	monthRentScheduled: number;
	monthRentCollected: number;
	/** 0–100 */
	collectionRate: number;
	overdue: CountAmount;
	/** Last 12 months */
	trend: MonthlyPoint[];
	leasesExpiring30: number;
	leasesExpiring60: number;
	leasesExpiring90: number;
	openWorkOrders: PriorityCount[];
	monthlyRecurringRent: number;
}
