import type { WorkspaceExperience } from '$lib/types/user';

export type NotificationSeverity =
  | "Info"
  | "Success"
  | "Warning"
  | "Error"
  | "Critical";

export type NotificationDestination =
  | "Home"
  | "Notifications"
  | "Rentals"
  | "Owners"
  | "Money"
  | "Work"
  | "Inbox"
  | "UnitSummary"
  | "UnitTenantLease"
  | "UnitMoney"
  | "UnitMaintenance"
  | "UnitRecords"
  | "TenantLedgerEntry"
  | "Expense"
  | "ScanDraft"
  | "Message"
  | "WorkOrder"
  | "TechnicianWork"
  | "LeasingRental"
  | "LeasingApplication"
  | "LeasingAppointment"
  | "LeasingConversation"
  | "LeasingMoveIn"
  | "TenantAccount";

export type NotificationAction = "Open" | "Review" | "Resolve";

export interface NotificationResource {
  kind: string;
  id: number;
}

export interface NotificationNavigationIntent {
  experience: WorkspaceExperience;
  destination: NotificationDestination;
  accessContextId: number;
  accessRevision: number;
  resource: NotificationResource | null;
  parentResource: NotificationResource | null;
  childResource: NotificationResource | null;
  action: NotificationAction;
  expiresAtUtc: string;
  fallbackDestination: NotificationDestination;
}

export interface NotificationItem {
  id: number;
  type: string;
  title: string;
  message: string;
  severity: NotificationSeverity;
  navigationIntent?: NotificationNavigationIntent | null;
  relatedEntityType?: string | null;
  relatedEntityId?: number | null;
  isRead: boolean;
  createdAt: string;
}

export interface NotificationListParams {
  unreadOnly?: boolean;
  take?: number;
  skip?: number;
}

export interface UnreadCountResponse {
  count: number;
}

export interface MyAlertsResponse {
  userId: number;
  displayName: string;
  email: string | null;
  phoneNumber: string | null;
  enableInApp: boolean;
  enableMobilePush: boolean;
  enableEmail: boolean;
  enableSms: boolean;
}

export interface UpdateMyAlertsRequest {
  enableInApp: boolean;
  enableMobilePush: boolean;
  enableEmail: boolean;
  enableSms: boolean;
}

export interface MorningBriefingSettingsResponse {
  enabled: boolean;
  sendHourLocal: number;
  includeEmpty: boolean;
  timeZone: string;
}

export interface UpdateMorningBriefingSettingsRequest {
  enabled: boolean;
  sendHourLocal: number;
  includeEmpty: boolean;
}

export interface LateFeeAutomationSettingsResponse {
  rentChargesAlwaysOn: boolean;
  enableLateFees: boolean;
  lateFeeGraceDays: number;
}

export interface UpdateLateFeeAutomationSettingsRequest {
  enableLateFees: boolean;
  lateFeeGraceDays: number;
}

export type TeamRoutingTopic =
  | "RentAndMoney"
  | "ApplicationsAndLeasing"
  | "WorkOrders"
  | "OwnerStatementsAndDecisions"
  | "AccountAndSecurity"
  | "MorningBriefing";

export interface TeamRoutingRuleResponse {
  id: number;
  topic: TeamRoutingTopic;
  propertyId: number | null;
  scope: string;
  useWorkspaceAdministratorFallback: boolean;
  namedRecipientCount: number;
  namedRecipientSummary: string;
  routingExplanation: string;
  updatedAtUtc: string;
}

export interface TeamRoutingRuleRecipientResponse {
  userId: number;
  displayName: string;
  email: string | null;
  reason: string;
}

export interface TeamRoutingRecipientPreview
  extends TeamRoutingRuleRecipientResponse {
  phoneNumber: string | null;
  enableInApp: boolean;
  enableMobilePush: boolean;
  enableEmail: boolean;
  enableSms: boolean;
  propertyId: number | null;
  scope: string;
  isAdministratorFallback: boolean;
}

export interface UpsertTeamRoutingRuleRequest {
  topic: TeamRoutingTopic;
  propertyId: number | null;
  useWorkspaceAdministratorFallback: boolean;
  recipients: Array<{ userId: number; reason: string }>;
}

export type TenantNoticeMode = "Off" | "Draft" | "Auto";
export type NoticeClassification = "Courtesy" | "Operational" | "Legal";
export type NoticeFailureBehavior =
  | "StopAndRequireReview"
  | "RetryThenDraft"
  | "RetryThenFail";
export type NoticeDeliveryChannel =
  | "TenantPortal"
  | "MobilePush"
  | "Email"
  | "Sms";

export interface TenantNoticePolicyResponse {
  id: number;
  automationKey: string;
  mode: TenantNoticeMode;
  classification: NoticeClassification;
  leadDays: number;
  sendHourLocal: number;
  sendTenantPortal: boolean;
  sendMobilePush: boolean;
  sendEmail: boolean;
  sendSms: boolean;
  includePrimaryTenant: boolean;
  includeCoTenant: boolean;
  includeEligibleGuarantor: boolean;
  includeOccupant: boolean;
  failureBehavior: NoticeFailureBehavior;
  workspaceNoticeTemplateVersionId: number;
  templateSystemKey: string;
  templateVersion: number;
  templateSubject: string;
  templateBody: string;
  templateBasedOnSystemTemplateVersionId: number;
  templateProvenance: string;
  templateIsCustomized: boolean;
  templateUpdateAvailable: boolean;
  templateCreatedAtUtc: string;
  templateJurisdictionCode: string | null;
  templateJurisdictionReviewedAtUtc: string | null;
  reviewedJurisdictionCode: string | null;
  jurisdictionReviewedAtUtc: string | null;
  canAutoSend: boolean;
  updatedAtUtc: string;
}

export interface UpsertTenantNoticePolicyRequest {
  automationKey: string;
  mode: TenantNoticeMode;
  classification: NoticeClassification;
  leadDays: number;
  sendHourLocal: number;
  sendTenantPortal: boolean;
  sendMobilePush: boolean;
  sendEmail: boolean;
  sendSms: boolean;
  includePrimaryTenant: boolean;
  includeCoTenant: boolean;
  includeEligibleGuarantor: boolean;
  includeOccupant: boolean;
  failureBehavior: NoticeFailureBehavior;
  workspaceNoticeTemplateVersionId: number;
  reviewedJurisdictionCode: string | null;
  confirmJurisdictionReviewed: boolean;
}

export interface WorkspaceNoticeTemplateResponse {
  id: number;
  systemKey: string;
  version: number;
  basedOnSystemTemplateVersionId: number;
  isCustomized: boolean;
  subject: string;
  body: string;
  classification: NoticeClassification;
  jurisdictionCode: string | null;
  jurisdictionReviewedAtUtc: string | null;
  updateAvailable: boolean;
  createdAtUtc: string;
}

export interface CreateWorkspaceNoticeTemplateVersionRequest {
  subject: string;
  body: string;
  jurisdictionCode: string | null;
  confirmJurisdictionReviewed: boolean;
}

export interface NoticeMergeFieldHelpResponse {
  key: string;
  token: string;
  label: string;
  description: string;
  example: string;
}

export interface TenantNoticeRecipientPreviewResponse {
  leaseManagementPartyId: number;
  tenantId: number;
  displayName: string;
  role: "PrimaryTenant" | "CoTenant" | "Guarantor" | "Occupant";
  eligible: boolean;
  availableChannels: NoticeDeliveryChannel[];
  email: string | null;
  phone: string | null;
  reason: string;
}

export type NoticeDeliveryStatus =
  | "Queued"
  | "Accepted"
  | "Retrying"
  | "Sent"
  | "PermanentlyFailed";

export interface NoticeDeliveryStatusResponse {
  evidenceId: number;
  renderedNoticeId: number;
  noticeDraftId: number;
  subject: string;
  leaseManagementId: number;
  recipientRole: "PrimaryTenant" | "CoTenant" | "Guarantor" | "Occupant";
  channel: NoticeDeliveryChannel;
  destination: string;
  status: NoticeDeliveryStatus;
  attemptCount: number;
  createdAtUtc: string;
  lastAttemptAtUtc: string | null;
  nextAttemptAtUtc: string | null;
  acceptedAtUtc: string | null;
  deliveredAtUtc: string | null;
  failedAtUtc: string | null;
  provider: string | null;
  providerMessageId: string | null;
  lastError: string | null;
}
