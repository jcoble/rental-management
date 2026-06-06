export type PortfolioStatus = 'Onboarding' | 'Active' | 'Archived';
export type PropertyType = 'SingleFamily' | 'MultiFamily' | 'Condo' | 'Townhome' | 'Commercial' | 'MixedUse';
export type PropertyStatus = 'Active' | 'UnderMaintenance' | 'Inactive';
export type UnitStatus = 'Vacant' | 'Occupied' | 'Reserved' | 'Offline';
export type LeaseStatus = 'Draft' | 'PendingSignature' | 'Active' | 'NoticeGiven' | 'Expired' | 'Terminated';
export type EsignStatus = 'None' | 'Sent' | 'Signed' | 'Declined';
export type PaymentType = 'Rent' | 'SecurityDeposit' | 'LateFee' | 'Utility' | 'Other';
export type PaymentStatus = 'Scheduled' | 'Paid' | 'Partial' | 'Late' | 'Waived' | 'Failed' | 'Refunded';
export type ExpenseStatus = 'Pending' | 'Approved' | 'Paid' | 'Rejected' | 'Draft';
export type WorkOrderPriority = 'Low' | 'Normal' | 'High' | 'Emergency';
export type WorkOrderStatus = 'New' | 'Scheduled' | 'InProgress' | 'WaitingParts' | 'Completed' | 'Cancelled';
export type AppointmentType = 'Showing' | 'MoveIn' | 'MoveOut' | 'Inspection' | 'MaintenanceVisit' | 'OwnerMeeting';
export type AppointmentStatus = 'Scheduled' | 'Confirmed' | 'Completed' | 'Cancelled' | 'NoShow';
export type InspectionType = 'MoveIn' | 'MoveOut' | 'Routine' | 'AnnualSafety';
export type InspectionStatus = 'Scheduled' | 'Completed' | 'NeedsFollowUp' | 'Cancelled';
export type UserRole = 'Admin' | 'Manager' | 'Agent' | 'Owner' | 'Tenant';
export type PortalMessageStatus = 'Open' | 'InProgress' | 'Resolved' | 'Closed';
export type MessageStatus = 'Open' | 'InProgress' | 'Resolved' | 'Closed';

export interface Message {
	id: number;
	portfolioId: number;
	propertyId?: number;
	propertyName?: string;
	unitId?: number;
	unitLabel?: string;
	userAccountId: number;
	senderName?: string;
	subject: string;
	body: string;
	status: MessageStatus;
	reply?: string;
	createdAt: string;
	updatedAt: string;
}

export interface Portfolio {
	id: number;
	name: string;
	description?: string;
	managementCompanyName: string;
	timeZone: string;
	status: PortfolioStatus;
	settings?: string;
	/** Account-wide sandbox/live state. True = seeded demo sandbox (real outbound suppressed). */
	isSandbox?: boolean;
	propertyCount?: number;
	unitCount?: number;
	activeLeaseCount?: number;
	createdAt: string;
	updatedAt: string;
}

/** Sandbox/Live lifecycle state for the caller's portfolio (`GET /portfolio/sandbox-state`). */
export interface SandboxState {
	portfolioId: number;
	isSandbox: boolean;
	/** When the sandbox demo data was seeded; null once graduated to Live. */
	sandboxSeededAtUtc?: string | null;
}

export type OwnerEntityType = 'Person' | 'LLC' | 'Trust';

export interface Owner {
	id: number;
	portfolioId: number;
	ownerEntityType: OwnerEntityType;
	name: string;
	taxId?: string;
	addressLine1?: string;
	addressLine2?: string;
	city?: string;
	state?: string;
	postalCode?: string;
	/** Legacy/composed single-line address (kept in sync server-side for back-compat). */
	address?: string;
	phone?: string;
	email?: string;
	createdAt: string;
	updatedAt: string;
}

export interface Property {
	id: number;
	portfolioId: number;
	ownerId?: number;
	ownerEntityId?: number;
	name: string;
	propertyType: PropertyType;
	type?: PropertyType;
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
	hasScan?: boolean;
	scanIsImage?: boolean;
	createdAt: string;
	updatedAt: string;
}

export interface Payment {
	id: number;
	portfolioId: number;
	leaseId: number;
	paymentType: PaymentType;
	status: PaymentStatus;
	amount: number;
	dueDate: string;
	paidDate?: string;
	method?: string;
	externalReference?: string;
	notes?: string;
	tenantName?: string;
	leaseNumber?: string;
	hasScan?: boolean;
	scanIsImage?: boolean;
	createdAt: string;
	updatedAt: string;
}

export interface ExpenseLineItem {
	description: string;
	quantity?: number | null;
	unitPrice?: number | null;
	amount?: number | null;
	lineNumber: number;
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
	/** Typed line items loaded from ExpenseLineItems child rows (populated on single GET). */
	lineItems?: ExpenseLineItem[];
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
	snapshot: MoneySnapshot;
}

export interface MoneySnapshot {
	title: string;
	summary: string;
	bullets: string[];
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

/** Read-only report workspace from GET /api/v1/accounting/reports. */
export interface AccountingReports {
	portfolioId: number;
	generatedAt: string;
	totalIncome: number;
	totalExpenses: number;
	netCashFlow: number;
	ledger: LedgerTransaction[];
	properties: PropertyFinancialSummary[];
	scheduleE: ScheduleECategoryTotal[];
	vendors1099: Vendor1099Summary[];
}

export interface AccountingTransactionsResponse {
	items: AccountingTransaction[];
	totalCount: number;
	skip: number;
	take: number;
}

export interface AccountingTransaction {
	kind: 'Payment' | 'Expense' | 'Bank';
	id: number;
	date: string;
	/** When the row entered the system (created). Backs the "Entered" column + default sort. */
	createdAt: string;
	/** When the row was last edited; equals createdAt for untouched rows. */
	updatedAt: string;
	description: string;
	category: string;
	status: string;
	amount: number;
	propertyId?: number;
	propertyName?: string;
	counterparty?: string;
	detailHref?: string;
	hasReceipt: boolean;
	receiptIsImage: boolean;
}

export interface LedgerTransaction {
	date: string;
	type: 'Payment' | 'Expense' | 'Charge' | 'Bank' | string;
	id: number;
	description: string;
	amount: number;
	propertyId?: number;
	propertyName?: string;
	counterparty?: string;
	category?: string;
	status: string;
	sourceHref: string;
	/** Plain-English "why this is here", derived deterministically (no LLM). */
	explanation: string;
}

/** Tenant-facing ledger from GET /api/v1/leases/{id}/ledger (LeaseLedgerResponse). */
export interface LeaseLedger {
	leaseId: number;
	leaseNumber: string;
	tenantName?: string;
	propertyName?: string;
	totalCharged: number;
	totalPaid: number;
	/** Outstanding balance (charges minus payments). Negative = credit/overpayment. */
	balance: number;
	entries: LedgerTransaction[];
	testId: string;
}

/** Plain-English money snapshot from GET /api/v1/accounting/snapshot (MoneySnapshotResponse). */
export interface MoneySnapshotResponse {
	portfolioId: number;
	periodLabel: string;
	periodStart: string;
	periodEnd: string;
	collected: number;
	spent: number;
	net: number;
	pastDueAmount: number;
	pastDueCount: number;
	collectedLast30Days: number;
	spentLast30Days: number;
	netLast30Days: number;
	explanations: MoneySnapshotExplanations;
}

export interface MoneySnapshotExplanations {
	collected: string;
	spent: string;
	net: string;
	pastDue: string;
}

export interface PropertyFinancialSummary {
	propertyId: number;
	propertyName: string;
	income: number;
	expenses: number;
	net: number;
	overdue: number;
	overdueCount: number;
}

export interface Vendor1099Summary {
	vendorId: number;
	vendorName: string;
	totalPaid: number;
	is1099Eligible: boolean;
	w9OnFile: boolean;
	needsW9: boolean;
	needs1099Review: boolean;
}

export interface BankingSummary {
	connectionCount: number;
	transactionCount: number;
	unmatchedCount: number;
	suggestedMatchCount: number;
	lastSyncedAt?: string;
	connections: BankConnection[];
	recentTransactions: BankTransaction[];
}

export interface PlaidSettings {
	plaidEnvironment: string;
	configured: boolean;
}

export interface PlaidLinkTokenResponse {
	linkToken: string;
	expiration?: string;
	requestId?: string;
	configured: boolean;
	message?: string;
}

export interface BankConnection {
	id: number;
	provider: string;
	institutionName: string;
	accountName: string;
	accountMask?: string;
	accountType?: string;
	accountSubtype?: string;
	status: string;
	lastSyncedAt?: string;
}

export interface ExchangePlaidPublicTokenRequest {
	publicToken: string;
	institutionName: string;
	accountId: string;
	accountName: string;
	accountMask?: string;
	accountType?: string;
	accountSubtype?: string;
}

export interface BankTransaction {
	id: number;
	bankConnectionId: number;
	institutionName: string;
	accountName: string;
	providerTransactionId: string;
	postedAt: string;
	authorizedAt?: string;
	description: string;
	merchantName?: string;
	amount: number;
	isoCurrencyCode: string;
	category?: string;
	matchedPaymentId?: number;
	matchedExpenseId?: number;
	matchStatus: string;
	matchConfidence?: number;
	notes?: string;
	suggestedMatch?: BankMatchSuggestion;
}

export interface BankMatchSuggestion {
	entityType: 'Payment' | 'Expense' | string;
	entityId: number;
	confidence: number;
	label: string;
	reason: string;
}

export interface ImportBankTransactionsRequest {
	provider: string;
	institutionName: string;
	accountName: string;
	accountMask?: string;
	accountType?: string;
	accountSubtype?: string;
	transactions: ImportBankTransactionItem[];
}

export interface ImportBankTransactionItem {
	providerTransactionId: string;
	postedAt: string;
	authorizedAt?: string;
	description: string;
	merchantName?: string;
	amount: number;
	isoCurrencyCode: string;
	category?: string;
	rawData?: string;
}

export interface ImportBankTransactionsResponse {
	connection: BankConnection;
	importedCount: number;
	skippedCount: number;
	transactions: BankTransaction[];
}

export interface SyncBankConnectionResponse {
	connection: BankConnection;
	importedCount: number;
	skippedCount: number;
	transactions: BankTransaction[];
}

export interface MatchBankTransactionRequest {
	entityType: string;
	entityId: number;
}

export interface ConfirmBankMatchRequest {
	paymentId?: number;
	expenseId?: number;
}

export interface BankReviewQueueItem {
	transaction: BankTransaction;
	suggestion: BankMatchSuggestion;
}

export interface BankReviewQueueResponse {
	count: number;
	items: BankReviewQueueItem[];
}

export interface NoticeDraft {
	id: number;
	leaseId: number;
	tenantId: number;
	propertyId?: number;
	tenantName: string;
	propertyName?: string;
	unitNumber?: string;
	noticeType: string;
	status: string;
	subject: string;
	body: string;
	reason: string;
	triggerDate: string;
	conversationId?: number;
	approvedChannels?: string;
	createdAt: string;
	updatedAt: string;
	approvedAt?: string;
	dismissedAt?: string;
}

export interface GenerateNoticeDraftsResponse {
	createdCount: number;
	drafts: NoticeDraft[];
}

export interface ApproveNoticeDraftRequest {
	channels: string[];
}

export interface UpdateNoticeDraftRequest {
	subject?: string;
	body?: string;
}

export interface LeaseQuestionResponse {
	answer: string;
	llmEnhanced: boolean;
	sources: string[];
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
	/** Cached average star rating (1–5); null until the vendor has been rated. */
	averageRating?: number | null;
	/** Number of ratings behind {@link averageRating}. */
	ratingCount: number;
	/** Count of work orders this vendor has completed. */
	jobsCompleted: number;
	createdAt: string;
	updatedAt: string;
}

/** Vendor dispatch status (mirrors the API's VendorDispatchStatus enum, serialized as strings). */
export type VendorDispatchStatus = 'Dispatched' | 'Acknowledged' | 'Completed' | 'Cancelled';

/** Result of texting a job to a vendor (POST /work-orders/{id}/dispatch). */
export interface VendorDispatch {
	id: number;
	portfolioId: number;
	workOrderId: number;
	vendorId: number;
	status: VendorDispatchStatus;
	dispatchedAtUtc: string;
	respondedAtUtc?: string | null;
	message?: string | null;
}

/** A recorded 1–5 star rating of a vendor (POST /vendors/{id}/ratings). */
export interface VendorRating {
	id: number;
	vendorId: number;
	workOrderId?: number | null;
	stars: number;
	comment?: string | null;
	createdAtUtc: string;
}

/** Vendor performance scorecard (GET /vendors/{id}/scorecard). */
export interface VendorScorecard {
	vendorId: number;
	name: string;
	averageRating?: number | null;
	ratingCount: number;
	jobsCompleted: number;
	/** Average hours from a job being texted to the vendor's DONE reply; null until one completes. */
	avgResponseHours?: number | null;
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
	hasScan?: boolean;
	scanIsImage?: boolean;
}

/** One status-change event in a work order's history (oldest→newest). */
export interface WorkOrderTimelineEntry {
	id: number;
	/** null on the create event. */
	fromStatus: WorkOrderStatus | null;
	toStatus: WorkOrderStatus;
	note?: string | null;
	/** "Staff" / "Tenant" / a person's name. */
	changedByLabel?: string | null;
	createdAtUtc: string;
}

/** A single work order plus its status timeline (returned by GET /work-orders/{id} and the portal detail). */
export interface WorkOrderDetail extends WorkOrder {
	timeline?: WorkOrderTimelineEntry[];
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

export type InspectionItemResult = 'Pending' | 'Pass' | 'Fail' | 'NotApplicable';

export interface InspectionItem {
	id: number;
	inspectionId: number;
	area: string;
	label: string;
	result: InspectionItemResult;
	note?: string;
	photoStoredFileId?: number;
	spawnedWorkOrderId?: number;
	sortOrder: number;
}

/** Inspection detail/create response: the inspection fields plus checklist items. */
export interface InspectionDetail extends Inspection {
	templateId?: number;
	reportStoredFileId?: number;
	inspector?: string;
	items: InspectionItem[];
}

export interface InspectionTemplateItem {
	area: string;
	label: string;
	sortOrder: number;
}

export interface InspectionTemplate {
	/** Built-in templates have NEGATIVE ids (e.g. -1, -2). Pass them back to create as-is. */
	id: number;
	portfolioId?: number;
	name: string;
	inspectionType: InspectionType;
	isBuiltIn: boolean;
	items: InspectionTemplateItem[];
}

export interface InspectionCompleteResult {
	inspectionId: number;
	status: InspectionStatus;
	totalItems: number;
	passCount: number;
	failCount: number;
	notApplicableCount: number;
	pendingCount: number;
	reportStoredFileId?: number;
	createdWorkOrderIds: number[];
}

/** One row of the unified audit trail returned by `GET /api/v1/audit`. */
export interface AuditEntry {
	id: number;
	portfolioId: number;
	operation: string;
	operationName: string;
	entityType: string;
	entityId: number;
	actor: string;
	description: string;
	detailHref?: string;
	timestamp: string;
	testId?: string;
}

/**
 * Admin-only forensic row from `GET /api/v1/admin/audit`: the {@link AuditEntry} fields plus the IP
 * address and raw old→new JSON the landlord-facing endpoint intentionally withholds.
 */
export interface AdminAuditEntry extends AuditEntry {
	userId?: number | null;
	actorLabel?: string | null;
	ipAddress?: string | null;
	oldValues?: string | null;
	newValues?: string | null;
	changeReason?: string | null;
}

/**
 * Shape of the dashboard "recent activity" widget items (the API's `DashboardActivity`).
 * Distinct from {@link AuditEntry}: the dashboard endpoint pre-humanizes each row.
 */
export interface DashboardActivity {
	id: number;
	type: string;
	action?: string;
	description?: string;
	actor?: string;
	createdAt: string;
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
	recentActivity: DashboardActivity[];
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

/** Matches DocumentDto from GET /api/v1/documents and POST /api/v1/documents. */
export interface DocumentItem {
	id: number;
	fileName: string;
	contentType: string;
	sizeBytes?: number;
	entityType: string;
	entityId: number;
	isImage: boolean;
	uploadedAt: string;
	category?: string;
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
