export type PortfolioStatus = 'Onboarding' | 'Active' | 'Archived';
export type PropertyType =
	| 'SingleFamily'
	| 'MultiFamily'
	| 'Condo'
	| 'Townhome'
	| 'Commercial'
	| 'MixedUse';
export type RentalStructure = 'SingleRental' | 'MultiRental';
export type PropertyStatus = 'Active' | 'UnderMaintenance' | 'Inactive';
export type DerivedUnitStatus = 'Vacant' | 'Occupied' | 'Reserved' | 'Offline';
export type LeaseStatus =
	| 'Draft'
	| 'PendingSignature'
	| 'Active'
	| 'NoticeGiven'
	| 'Expired'
	| 'Terminated';
export type EsignStatus = 'None' | 'Sent' | 'Signed' | 'Declined';
export type PaymentType =
	| 'Rent'
	| 'SecurityDeposit'
	| 'LateFee'
	| 'Utility'
	| 'Other';
export type PaymentStatus =
	| 'Scheduled'
	| 'Paid'
	| 'Partial'
	| 'Late'
	| 'Waived'
	| 'Failed'
	| 'Refunded';
export type ExpenseStatus =
	| 'Pending'
	| 'Approved'
	| 'Paid'
	| 'Rejected'
	| 'Draft';
export type WorkOrderPriority = 'Low' | 'Normal' | 'High' | 'Emergency';
export type WorkOrderStatus =
	| 'New'
	| 'Scheduled'
	| 'InProgress'
	| 'WaitingParts'
	| 'Completed'
	| 'Cancelled';
export type AppointmentType =
	| 'Showing'
	| 'MoveIn'
	| 'MoveOut'
	| 'Inspection'
	| 'MaintenanceVisit'
	| 'OwnerMeeting';
export type AppointmentStatus =
	| 'Scheduled'
	| 'Confirmed'
	| 'Completed'
	| 'Cancelled'
	| 'NoShow';
export type InspectionType = 'MoveIn' | 'MoveOut' | 'Routine' | 'AnnualSafety';
export type InspectionStatus =
	| 'Scheduled'
	| 'Completed'
	| 'NeedsFollowUp'
	| 'Cancelled';
export interface Portfolio {
	id: number;
	name: string;
	description?: string;
	managementCompanyName: string;
	timeZone: string;
	status: PortfolioStatus;
	settings?: string;
	/** Account-wide sandbox/live state. True = seeded example data. */
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
	/**
	 * True when the account has not yet made the first-login Sandbox-vs-Live choice. While true the
	 * app routes the user to the onboarding choice gate (`/choose-setup`) instead of the dashboard.
	 */
	onboardingChoicePending?: boolean;
}

/** Server-shaped checklist facts for the getting-started card. */
export interface GettingStartedSignalsResponse {
	portfolioId: number;
	portfolioNamed: boolean;
	ownerCount: number;
	propertyCount: number;
	unitCount: number;
	tenantCount: number;
	leaseCount: number;
	hasNotificationEmail: boolean;
	hasAutomations: boolean;
	isSandbox: boolean;
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
	assignedPropertyCount?: number;
	isPrimary?: boolean;
	hasActiveOwnerPortalAccess?: boolean;
	hasPendingOwnerPortalInvitation?: boolean;
	createdAt: string;
	updatedAt: string;
}

export interface Property {
	id: number;
	portfolioId: number;
	ownerships: PropertyOwnership[];
	name: string;
	// Wire field is `type` (PropertyResponse maps PropertyType → "type"); the grid column,
	// type filter, and create/edit forms all read/write this single name.
	type: PropertyType;
	rentalStructure: RentalStructure;
	status: PropertyStatus;
	addressLine1: string;
	addressLine2?: string;
	city: string;
	state: string;
	postalCode: string;
	yearBuilt?: number;
	managementFeePercent?: number;
	notes?: string;
	// Depreciation basis (year-end tax picture). Land is not depreciable.
	purchasePrice?: number | null;
	landValue?: number | null;
	inServiceDate?: string | null;
	manualAnnualDepreciation?: number | null;
	/** Cumulative depreciation taken to date (read-only; system-maintained). */
	accumulatedDepreciation?: number;
	unitCount?: number;
	occupiedUnits?: number;
	createdAt: string;
	updatedAt: string;
}

export interface PropertyOwnership {
	id: number;
	ownerEntityId: number;
	ownerName: string;
	ownershipSharePercent: number;
	effectiveFromUtc: string;
	effectiveToUtc?: string | null;
	statementRecipientName: string;
	statementRecipientEmail?: string | null;
	payeeName: string;
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
	status: DerivedUnitStatus;
	notes?: string;
	createdAt: string;
	updatedAt: string;
}

export type RentalListingStatus =
	| 'Draft'
	| 'ReadyToPublish'
	| 'Published'
	| 'Paused'
	| 'Filled'
	| 'Archived';
export type ListingPublicationStatus =
	| 'Draft'
	| 'Ready'
	| 'Publishing'
	| 'Published'
	| 'Paused'
	| 'Failed'
	| 'Removed'
	| 'ReconciliationRequired';

export interface ListingPhoto {
	id: number;
	position: number;
	category: string;
	caption?: string | null;
	storedFileId?: number | null;
	fileName?: string | null;
	sha256?: string | null;
	contentUrl?: string | null;
}

export interface ExternalListingSignal {
	id: number;
	signalType: string;
	suggestedExternalListingId?: string | null;
	suggestedListingUrl?: string | null;
	suggestedExternalStatus?: string | null;
	disposition: 'Unconfirmed' | 'Confirmed' | 'Rejected';
	receivedAtUtc: string;
}

export interface ListingPublication {
	id: number;
	providerKey: string;
	mode: 'Guided' | 'Connected';
	status: ListingPublicationStatus;
	externalListingId?: string | null;
	listingUrl?: string | null;
	applicationUrl?: string | null;
	managementUrl?: string | null;
	lastConfirmedExternalStatus?: string | null;
	lastConfirmedAtUtc?: string | null;
	copyConfirmed: boolean;
	termsConfirmed: boolean;
	photosConfirmed: boolean;
	providerWorkspaceOpened: boolean;
	needsRepublish: boolean;
	publishedContentVersion?: number | null;
	lastDeliveryKey?: string | null;
	lastDeliveryStatus?: string | null;
	lastDeliveryError?: string | null;
	lastDeliveryAttemptAtUtc?: string | null;
	channelAvailable: boolean;
	channelState: string;
	channelUnavailableReason?: string | null;
	unconfirmedSignals: ExternalListingSignal[];
}

export interface ListingWorkspace {
	id: number;
	portfolioId: number;
	propertyId: number;
	unitId: number;
	status: RentalListingStatus;
	contentVersion: number;
	headline: string;
	description: string;
	rent: number;
	securityDeposit?: number | null;
	bedrooms: number;
	bathrooms: number;
	squareFeet?: number | null;
	availableOn?: string | null;
	leaseTerms?: string | null;
	petPolicy?: string | null;
	utilities?: string | null;
	parking?: string | null;
	amenities?: string | null;
	photoManifest: ListingPhoto[];
	publications: ListingPublication[];
	signedLeaseImportUrl: string;
	createdAt: string;
	updatedAt: string;
}

export interface SaveListingWorkspaceRequest {
	status?: RentalListingStatus;
	headline?: string;
	description?: string;
	rent?: number | null;
	securityDeposit?: number | null;
	availableOn?: string | null;
	leaseTerms?: string | null;
	petPolicy?: string | null;
	utilities?: string | null;
	parking?: string | null;
	amenities?: string | null;
	zillowGuided?: {
		status?: ListingPublicationStatus;
		externalListingId?: string | null;
		listingUrl?: string | null;
		applicationUrl?: string | null;
		managementUrl?: string | null;
		lastConfirmedExternalStatus?: string | null;
		lastConfirmedAtUtc?: string | null;
		copyConfirmed?: boolean;
		termsConfirmed?: boolean;
		photosConfirmed?: boolean;
		providerWorkspaceOpened?: boolean;
		markCurrentVersionPublished?: boolean;
	};
}

/** A units-list row with health badges (GET /units/list-with-health). */
export interface UnitHealth {
	id: number;
	propertyId: number;
	propertyName: string;
	unitNumber: string;
	status: DerivedUnitStatus;
	marketRent: number;
	openWorkOrderCount: number;
	leaseEndsInDays?: number;
	docsNeedingReviewCount: number;
	/** Simplified list badge (Active/Renewal/Move-Out/Lease/Vacant/Turnover); not the full 9-stage label. */
	simpleStage: string;
	testId?: string;
}

/** The nine derived lifecycle stages a unit moves through (computed server-side, read-only). */
export type UnitLifecycleStage =
	| 'Ready'
	| 'Listed'
	| 'Applicant'
	| 'Lease'
	| 'MoveIn'
	| 'Active'
	| 'Renewal'
	| 'MoveOut'
	| 'Turnover';

export interface UnitNextBestAction {
	label: string;
	href: string;
}

export interface UnitDashboardHeader {
	/** One-word rent state: Overdue / Due / Current / NoLease. */
	rentState: string;
	outstandingRentBalance: number;
	openWorkOrderCount: number;
	leaseEndsInDays?: number;
	docsNeedingReviewCount: number;
	currentTenantName?: string;
}

export interface UnitOccupancyPossessionCondition {
	status: string;
	isOccupied: boolean;
	hasScheduledMoveIn: boolean;
	leaseManagementId?: number | null;
}

export interface UnitMarketingAvailabilityCondition {
	status: string;
	isAvailable: boolean;
}

export interface UnitTenantAccountCondition {
	status: string;
	tenantAccountId?: number | null;
	receivableBalance: number;
	pastDueAmount: number;
}

export interface UnitLegalNoticeCondition {
	status: string;
	agreementId?: number | null;
	agreementStatus?: string | null;
	openNoticeCount: number;
}

export interface UnitMaintenanceTurnoverCondition {
	status: string;
	openWorkOrderCount: number;
	isInTurnover: boolean;
	isOutOfService: boolean;
	isOnManagementHold: boolean;
}

export interface UnitLeaseSummary {
	id: number;
	leaseManagementId: number;
	tenantAccountId?: number | null;
	leaseNumber: string;
	status: string;
	startDate: string;
	endDate: string;
	monthlyRent: number;
	securityDeposit: number;
}

export interface UnitTenantSummary {
	id: number;
	name: string;
	email?: string;
	phone?: string;
}

export interface UnitPaymentSummary {
	id: number;
	tenantAccountId: number;
	leaseManagementId: number;
	leaseAgreementId: number | null;
	type: string;
	status: string;
	description: string;
	amount: number;
	dueDate: string;
	paidDate?: string;
}

export interface UnitWorkOrderSummary {
	id: number;
	title: string;
	status: string;
	priority: string;
	requestedAt: string;
}

export interface UnitDocumentSummary {
	id: number;
	fileName: string;
	contentType: string;
	entityType?: string;
	entityId?: number;
	uploadedAt: string;
}

export interface UnitAppointmentSummary {
	id: number;
	title: string;
	type: string;
	status: string;
	scheduledStart: string;
	assignedTo?: string;
}

export interface UnitDashboardOverview {
	recentPayments: UnitPaymentSummary[];
	openWorkOrders: UnitWorkOrderSummary[];
	pendingDocs: UnitDocumentSummary[];
	upcomingAppointments: UnitAppointmentSummary[];
}

export type UnitTurnoverStatus =
	| 'NotStarted'
	| 'AwaitingVacancy'
	| 'MoveOut'
	| 'InProgress'
	| 'RentReady';

export interface UnitTurnoverSummary {
	status: UnitTurnoverStatus | string;
	totalTaskCount: number;
	openTaskCount: number;
	completedTaskCount: number;
	receiptCount: number;
	estimatedCost: number;
	actualCost: number;
	startedAt?: string | null;
	targetReadyDate?: string | null;
	lastActivityAt?: string | null;
	daysInTurnover?: number | null;
}

/** The Unit Command Center at-a-glance aggregate (GET /units/{id}/dashboard). */
export interface UnitDashboard {
	unit: Unit;
	propertyName: string;
	lifecycleStage: UnitLifecycleStage;
	nextBestAction: UnitNextBestAction;
	header: UnitDashboardHeader;
	occupancyPossession: UnitOccupancyPossessionCondition;
	marketingAvailability: UnitMarketingAvailabilityCondition;
	tenantAccountCondition: UnitTenantAccountCondition;
	legalNoticeCondition: UnitLegalNoticeCondition;
	maintenanceTurnover: UnitMaintenanceTurnoverCondition;
	leaseManagementId?: number | null;
	tenantAccountId?: number | null;
	currentLease?: UnitLeaseSummary;
	currentTenant?: UnitTenantSummary;
	currentTenants?: UnitTenantSummary[];
	overview: UnitDashboardOverview;
	turnover: UnitTurnoverSummary;
	recentTimeline: AuditEntry[];
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
	leaseHistoryCount?: number;
	canDelete?: boolean;
	deleteBlockedReason?: string;
	createdAt: string;
	updatedAt: string;
}

export interface LeaseManagementSummary {
	leaseManagementId: number;
	leaseManagementPublicId: string;
	relationshipNumber: string;
	propertyId: number;
	propertyName: string;
	unitId: number;
	unitNumber: string;
	lifecycle: string;
	businessDate: string;
	leaseAgreementId?: number | null;
	agreementNumber?: string | null;
	agreementStatus?: string | null;
	termStartOn?: string | null;
	termEndOn?: string | null;
	baseRentAmount?: number | null;
	upcomingLeaseAgreementId?: number | null;
	upcomingAgreementNumber?: string | null;
	upcomingAgreementStatus?: string | null;
	upcomingTermStartOn?: string | null;
	upcomingTermEndOn?: string | null;
	tenantAccountId?: number | null;
	primaryTenantId?: number | null;
	primaryTenantName?: string | null;
	currentPartyCount: number;
	currentResidentCount: number;
	currentFinanciallyResponsiblePartyCount: number;
	hasReconciliationException: boolean;
	hasGoverningAgreementWithoutPossession: boolean;
	plannedPossessionAtUtc?: string | null;
	possessionGivenAtUtc?: string | null;
	plannedMoveOutAtUtc?: string | null;
	possessionReturnedAtUtc?: string | null;
	accountClosedAtUtc?: string | null;
	canceledAtUtc?: string | null;
	endingDisposition: LeaseManagementEndingDisposition;
	endingDispositionDecidedAtUtc?: string | null;
	endingDispositionDecidedByUserId?: number | null;
	noticeGivenAtUtc?: string | null;
	cancellationReasonCode?: string | null;
	cancellationNote?: string | null;
	updatedAtUtc: string;
}

export type LeaseManagementEndingDisposition =
	| 'Undecided'
	| 'OfferRenewal'
	| 'OfferMonthToMonth'
	| 'NonRenewalMoveOut';

export interface LeaseManagementParty {
	leaseManagementPartyId: number;
	leaseManagementId: number;
	tenantId: number;
	tenantName: string;
	email?: string;
	phone?: string;
	role: 'PrimaryTenant' | 'CoTenant' | 'Guarantor' | 'Occupant' | string;
	effectiveFrom: string;
	effectiveThrough?: string | null;
	guarantorLegalNoticeEligible: boolean;
	isCurrent: boolean;
	canGrantTenantPortalAccess: boolean;
}

export interface LeaseManagementDetail {
	summary: LeaseManagementSummary;
	endingDisposition: LeaseManagementEndingDisposition;
	endingDispositionDecidedAtUtc?: string | null;
	endingDispositionDecidedByUserId?: number | null;
	noticeGivenAtUtc?: string | null;
	cancellationReasonCode?: string | null;
	cancellationNote?: string | null;
	parties: LeaseManagementParty[];
	agreementCount: number;
	addendumCount: number;
	legalArtifactCount: number;
}

export interface LegalDocumentArtifactSummary {
	legalDocumentArtifactId: number;
	publicId: string;
	artifactKind: string;
	fileName: string;
	contentType: string;
	byteLength: number;
	contentSha256: string;
	createdAtUtc: string;
}

export interface LeaseAgreementSummary {
	leaseManagementId: number;
	leaseAgreementId: number;
	publicId: string;
	versionNumber: number;
	agreementNumber: string;
	changeType: string;
	correctionReason?: string | null;
	replacesAgreementId?: number | null;
	renewsAgreementId?: number | null;
	reissuesAgreementId?: number | null;
	reissueReason?: string | null;
	hasLiveReissue: boolean;
	termType: string;
	termStartOn: string;
	termEndOn?: string | null;
	governingFromOn: string;
	supersededEffectiveOn?: string | null;
	baseRentAmount: number;
	agreementStatus: string;
	isGoverning: boolean;
	signerCount: number;
	hasSourceScan: boolean;
	issuedArtifact?: LegalDocumentArtifactSummary | null;
	executedArtifact?: LegalDocumentArtifactSummary | null;
	issuedAtUtc?: string | null;
	fullyExecutedAtUtc?: string | null;
	voidedAtUtc?: string | null;
	draftCanceledAtUtc?: string | null;
	draftCanceledByUserId?: number | null;
	draftCancellationReason?: string | null;
	createdAtUtc: string;
	updatedAtUtc: string;
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
	operationalScope?: 'Portfolio' | 'Property' | 'Unit' | 'WorkOrder';
	propertyId?: number;
	unitId?: number;
	vendorId?: number;
	workOrderId?: number;
	capitalizedAssetId?: number | null;
	category: string;
	description: string;
	status: ExpenseStatus;
	amount: number;
	allocationTotal?: number;
	subtotal?: number;
	taxAmount?: number;
	incurredAt: string;
	dueDate?: string;
	paidAt?: string;
	billableToOwner: boolean;
	notes?: string;
	receiptData?: string;
	propertyName?: string;
	unitNumber?: string;
	vendorName?: string;
	workOrderTitle?: string;
	createdAt: string;
	updatedAt: string;
	accountId?: number | null;
	accountName?: string | null;
	journalEntryPublicId?: string | null;
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
	totalDistributed: number;
	undistributed: number;
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
	totalDistributed: number;
	undistributed: number;
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
	/** Mortgage interest deducted (from the loan split; principal excluded). */
	mortgageInterest: number;
	/** Computed straight-line depreciation deducted. */
	depreciation: number;
	/** True when the depreciation figure is the first-year IRS mid-month estimate. */
	depreciationIsFirstYearEstimate: boolean;
}

export interface ScheduleEReport {
	year: number;
	properties: ScheduleEPropertyReport[];
	expensesByCategory: ScheduleECategoryAmount[];
	totalRentalIncome: number;
	totalExpenses: number;
	netIncome: number;
	unallocatedActivity: {
		canView: boolean;
		requiresAllocation: boolean;
		incomeEntryCount: number;
		rentalIncome: number;
		expenseCount: number;
		expensesByCategory: ScheduleECategoryAmount[];
		totalExpenses: number;
		netIncome: number;
		warning: string;
	};
	reconciledTotalRentalIncome: number;
	reconciledTotalExpenses: number;
	reconciledNetIncome: number;
}

/** One property's true cash flow for a period (rent − opex − debt service). */
export interface PropertyCashFlow {
	propertyId: number;
	propertyName: string;
	income: number;
	operatingExpenses: number;
	noi: number;
	debtService: number;
	cashFlow: number;
}

/** Per-property + portfolio true cash flow for a period. */
export interface CashFlowSummary {
	from: string;
	to: string;
	properties: PropertyCashFlow[];
	totalIncome: number;
	totalOperatingExpenses: number;
	totalNoi: number;
	totalDebtService: number;
	totalCashFlow: number;
}

/** One rent-roll row in the year-end view. */
export interface YearEndRentRollRow {
	propertyName: string;
	unitNumber: string;
	tenantName: string;
	monthlyRent: number;
	leaseStart: string;
	leaseEnd: string;
	leaseStatus: string;
	pastDueBalance: number;
}

export interface YearEndPropertyDisposition {
	id: number;
	propertyId: number;
	propertyName?: string | null;
	closedOnDate: string;
	salePrice: number;
	sellingCosts: number;
	netSaleProceeds: number;
	purchasePrice: number;
	landValue: number;
	buildingBasis: number;
	accumulatedDepreciationBeforeSale: number;
	saleYearDepreciation: number;
	totalDepreciation: number;
	adjustedBasis: number;
	gainLoss: number;
	unrecapturedSection1250Gain: number;
}

/** The year-end three-block view: cash flow vs taxable income + rent roll + accountant caveats. */
export interface YearEndView {
	year: number;
	cashFlow: CashFlowSummary;
	scheduleE: ScheduleEReport;
	rentRoll: YearEndRentRollRow[];
	propertyDispositions: YearEndPropertyDisposition[];
	accountantNotes: string[];
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
	ledgerTotalCount: number;
	recentLedger: LedgerTransaction[];
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
	kind:
		| 'Payment'
		| 'TenantLedger'
		| 'Expense'
		| 'Bank'
		| 'ApplicationFee'
		| 'LoanPayment'
		| 'OwnerActivity'
		| 'Reversal';
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
	tenantAccountId?: number | null;
	propertyId?: number;
	/** Unit id for the row (via Lease for payments, direct for expenses; null for bank rows).
	 * Lets the ledger route Payment/Expense rows into their unit's Command Center tab. */
	unitId?: number | null;
	propertyName?: string;
	counterparty?: string;
	detailHref?: string;
	hasReceipt: boolean;
	receiptIsImage: boolean;
	effectiveOn: string;
	enteredAtUtc: string;
	displayType:
		| 'Charge'
		| 'PaymentReceived'
		| 'Credit'
		| 'Expense'
		| 'Bill'
		| 'BankTransfer'
		| 'LoanPayment'
		| 'OwnerActivity'
		| 'Reversal';
	title: string;
	sourceContext: string | null;
	paidByOrTo: string | null;
	chargeAmount: number;
	paymentAmount: number;
	creditAmount: number;
	accountId: number | null;
	accountCode: string | null;
	accountName: string | null;
	journalEntryPublicId: string | null;
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
	/** Rent row was reduced for a partial first/final billing period. */
	isProrated?: boolean;
	/** Plain-English "why this is here", derived deterministically (no LLM). */
	explanation: string;
}

/** Tenant-facing projection of one continuous TenantAccount. */
export interface LeaseManagementLedger {
	leaseManagementId: number;
	tenantAccountId: number;
	accountNumber: string;
	tenantName?: string;
	propertyName?: string;
	totalCharged: number;
	totalPaid: number;
	/** Outstanding balance (charges minus payments). Negative = credit/overpayment. */
	balance: number;
	/** Open past-due charges from the authoritative server-side account view. */
	pastDueCount: number;
	/** Opening-balance anchor, returned separately from the paged entries so it stays stable across
	 * pages. Null when the lease carries no opening balance. */
	opening: LedgerTransaction | null;
	/** Immutable ledger rows for the requested page (newest first). */
	entries: LedgerTransaction[];
	/** Total non-opening ledger entries on the account — drives paging. */
	totalCount: number;
	skip: number;
	take: number;
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

/**
 * The "Who's behind" list from GET /api/v1/accounting/past-due (PastDueResponse). Shares the snapshot's
 * past-due definition server-side, so `totalCount` always equals the dashboard "tenants behind" KPI
 * while `items` is one bounded server page.
 */
export interface PastDueResponse {
	items: PastDueLease[];
	/** Number of leases/tenants behind across every page — equals the snapshot's pastDueCount. */
	totalCount: number;
	/** Total amount past due across all behind leases — equals the snapshot's pastDueAmount. */
	totalPastDueAmount: number;
	/** Portfolio-local date used by the database to age charges; null only when the scope has no current accounts. */
	businessDate: string | null;
	skip: number;
	take: number;
}

/** One behind lease/tenant row (PastDueLeaseResponse). */
export interface PastDueLease {
	leaseManagementId: number;
	tenantAccountId: number;
	currentAgreementId?: number | null;
	/** Unit id on the behind lease, so the "open oldest payment" link deep-links into the unit's
	 * Command Center Rent tab rather than the generic payment detail page. */
	unitId: number;
	tenantName?: string | null;
	/** Tenant phone, for a one-tap reminder text; null when not on file. */
	tenantPhone?: string | null;
	relationshipNumber?: string | null;
	propertyName?: string | null;
	unitNumber?: string | null;
	pastDueAmount: number;
	overduePaymentCount: number;
	/** Due date of the oldest past-due payment (drives the "N days late" label). */
	oldestDueOn: string;
	/** Id of the oldest past-due payment, so the row can deep-link into its detail. */
	oldestLedgerEntryId: number;
	oldestLedgerEntryOpenAmount: number;
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
	clientOperationId: string;
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
	propertyId?: number;
	propertyName?: string;
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
	matchedTenantAccountId?: number;
	matchedTenantLedgerEntryId?: number;
	matchedExpenseId?: number;
	matchStatus: string;
	matchConfidence?: number;
	notes?: string;
	updatedAt: string;
	suggestedMatch?: BankMatchSuggestion;
}

export interface BankTransactionListResponse {
	totalCount: number;
	skip: number;
	take: number;
	items: BankTransaction[];
}

export interface BankMatchSuggestion {
	entityType: 'TenantLedgerEntry' | 'Expense' | string;
	entityId: number;
	tenantAccountId?: number;
	confidence: number;
	label: string;
	reason: string;
}

export interface OperationalBankTransaction {
	id: number;
	postedAt: string;
	description: string;
	merchantName?: string;
	amount: number;
	isoCurrencyCode: string;
	category?: string;
	matchStatus: string;
	updatedAt: string;
}

export interface OperationalBankMatchSuggestion {
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
	operationKey: string;
	expectedUpdatedAtUtc: string;
	tenantAccountId?: number;
	tenantLedgerEntryId?: number;
	expenseId?: number;
}

export interface RouteBankTransactionRequest {
	operationKey: string;
	propertyId?: number;
	expectedUpdatedAtUtc: string;
}

export interface ConfirmBankMatchRequest {
	operationKey: string;
	expectedUpdatedAtUtc: string;
	tenantAccountId?: number;
	tenantLedgerEntryId?: number;
	expenseId?: number;
}

export interface BankReviewQueueItem {
	transaction: OperationalBankTransaction;
	suggestion: OperationalBankMatchSuggestion;
}

export interface BankReviewQueueResponse {
	count: number;
	skip: number;
	take: number;
	items: BankReviewQueueItem[];
}

export interface NoticeDraft {
	id: number;
	leaseManagementId: number;
	tenantAccountId: number;
	tenantLedgerEntryId?: number;
	recipientTenantId: number;
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

export interface GenerateNoticeDraftsRequest {
	leaseManagementId?: number;
	tenantAccountId?: number;
	tenantLedgerEntryId?: number;
	recipientTenantId?: number;
	noticeType?: string;
}

export interface ApproveNoticeDraftRequest {
	channels: string[];
	/**
	 * Set true to send even when the Fair Housing review flags the notice copy (the landlord
	 * reviewed the concerns and is overriding). Omitted/false → flagged copy is blocked with a 422
	 * carrying `fairHousingConcerns`.
	 */
	acknowledgedFairHousingReview?: boolean;
}

export interface UpdateNoticeDraftRequest {
	subject?: string;
	body?: string;
}

export interface NoticeTemplateResponse {
	noticeType: string;
	subject: string;
	body: string;
	hasTemplate: boolean;
	availableFields: string[];
	updatedAt?: string | null;
}

export interface UpsertNoticeTemplateRequest {
	subject: string;
	body: string;
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
	addressLine1?: string | null;
	city?: string | null;
	state?: string | null;
	postalCode?: string | null;
	email?: string;
	phone?: string;
	website?: string | null;
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
export type VendorDispatchStatus =
	| 'Dispatched'
	| 'Acknowledged'
	| 'Completed'
	| 'Cancelled';

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
	recurringMaintenanceTaskId?: number;
	title: string;
	description: string;
	technicianAccessInstructions?: string;
	category: string;
	priority: WorkOrderPriority;
	status: WorkOrderStatus;
	requestedAt: string;
	scheduledFor?: string;
	/** End of the scheduled arrival window (visit expected between scheduledFor and this). */
	scheduledWindowEnd?: string;
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
	detailRole?: 'tenant' | 'maintenance' | 'manager' | string;
	capabilities?: WorkOrderDetailCapabilities;
	submittedByLabel?: string | null;
	requesterName?: string | null;
	requesterPhone?: string | null;
	requesterEmail?: string | null;
	residentMustBePresent?: boolean | null;
	callBeforeEntry?: boolean | null;
	callIfNotHome?: boolean | null;
	permissionToEnter?: boolean | null;
	entryNotes?: string | null;
	petWarnings?: string | null;
	accessWarnings?: string | null;
	residentNames?: string[];
	privateManagementNotes?: string | null;
	canViewCosts?: boolean;
	photos?: WorkOrderPhotoSummary[];
	activity?: WorkOrderActivityItem[];
}

export interface WorkOrderDetailCapabilities {
	canViewTenantContact?: boolean;
	canViewResidents?: boolean;
	canViewAccessInstructions?: boolean;
	canViewPrivateManagementNotes?: boolean;
	canViewCosts?: boolean;
	canCommentPublicly?: boolean;
	canCommentPrivately?: boolean;
	canUploadPhoto?: boolean;
	canDeletePhoto?: boolean;
	canCancel?: boolean;
	canEditRequestFields?: boolean;
	canEditManagementFields?: boolean;
	canAssignTechnician?: boolean;
	canDispatchVendor?: boolean;
	allowedStatusTransitions?: WorkOrderStatus[];
}

export interface WorkOrderPhotoSummary {
	id: number;
	fileName: string;
	caption?: string | null;
	uploadedAtUtc?: string | null;
	uploadedByLabel?: string | null;
}

export interface WorkOrderActivityItem {
	id?: number;
	kind?: string;
	label?: string;
	note?: string | null;
	body?: string | null;
	fromStatus?: WorkOrderStatus | null;
	toStatus?: WorkOrderStatus | null;
	createdAtUtc?: string | null;
	actorLabel?: string | null;
	visibility?: 'Public' | 'Private' | string;
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
	/** True when an open vendor dispatch exists (texted, awaiting DONE) — drives the dispatched banner. */
	hasActiveDispatch?: boolean;
}

export interface Appointment {
	id: number;
	portfolioId: number;
	propertyId?: number;
	unitId?: number;
	leaseId?: number;
	tenantId?: number;
	workOrderId?: number;
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

export type InspectionItemResult =
	| 'Pending'
	| 'Pass'
	| 'Fail'
	| 'NotApplicable';

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

export interface InspectionItemInput {
	area: string;
	label: string;
}

export interface InspectionItemUpdate {
	area?: string;
	label?: string;
	result?: InspectionItemResult;
	note?: string;
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

export interface InspectionTemplateItemInput {
	area: string;
	label: string;
}

export interface InspectionTemplateInput {
	name: string;
	inspectionType: InspectionType;
	items: InspectionTemplateItemInput[];
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

/** One sanitized field change inside an {@link AuditEntry} (friendly name + formatted old→new). */
export interface AuditFieldChange {
	field: string;
	oldValue: string;
	newValue: string;
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
	/** Sanitized field-level diff for an Updated row; empty for Created/Deleted. */
	changes?: AuditFieldChange[];
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
	/** Primary key of the touched entity, for deep-linking to its detail page. */
	entityId: number;
	/** Owning unit when the touched entity belongs to a unit. */
	unitId?: number | null;
	action?: string;
	description?: string;
	/** Human label naming the specific record this row touched (null when the type has no cheap label). */
	label?: string;
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
