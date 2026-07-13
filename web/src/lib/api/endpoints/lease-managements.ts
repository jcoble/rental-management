import type {
  LeaseAgreementSummary,
  LeaseManagementDetail,
  LeaseManagementLedger,
  LeaseManagementParty,
  LeaseManagementSummary,
  LeaseQuestionResponse,
} from "$lib/types";
import { api, downloadFile, fetchApi } from "../client";

export type LeaseManagementPartyRole =
  | "PrimaryTenant"
  | "CoTenant"
  | "Guarantor"
  | "Occupant";
export type LeaseAgreementTermType = "FixedTerm" | "MonthToMonth";
export type LeaseAgreementChangeType =
  | "Initial"
  | "Transfer"
  | "Correction"
  | "Renewal"
  | "MonthToMonth"
  | "Restatement";
export type LeaseLegalSignerRole =
  | "PrimaryTenant"
  | "CoTenant"
  | "Guarantor"
  | "Manager"
  | "Owner"
  | "Other";
export type LeaseRenewalAddendumDecisionType =
  | "End"
  | "IncorporateIntoBase"
  | "ReissueAsAddendum";
export type LeaseAddendumPurpose =
  | "Financial"
  | "Pet"
  | "Occupancy"
  | "Rules"
  | "Other";
export type LeaseAddendumFinancialEffectType =
  | "RecurringRentDelta"
  | "OneTimeCharge"
  | "DepositObligationDelta";

export interface LeaseAgreementRenewalFinancialEffectSummary {
  leaseAddendumFinancialEffectId: number;
  effectType: LeaseAddendumFinancialEffectType;
  amount: number;
  currency: string;
  chargeCode: string;
  effectiveFromOn: string | null;
  effectiveThroughOn: string | null;
  dueOn: string | null;
  description: string;
}

export interface LeaseAgreementEffectiveAddendumSeriesItem {
  seriesPublicId: string;
  currentLeaseAddendumId: number;
  currentLeaseAddendumPublicId: string;
  currentVersionNumber: number;
  baseAgreementId: number;
  baseAgreementNumber: string;
  baseAgreementTermStartOn: string;
  baseAgreementTermEndOn: string | null;
  purpose: LeaseAddendumPurpose;
  title: string;
  effectiveFromOn: string;
  effectiveThroughOn: string | null;
  decisionRequired: boolean;
  financialEffectCount: number;
  financialEffects: LeaseAgreementRenewalFinancialEffectSummary[];
}

export interface LeaseAgreementEffectiveAddendumSeries {
  leaseManagementId: number;
  sourceAgreementId: number;
  sourceAgreementNumber: string;
  sourceTermStartOn: string;
  sourceTermEndOn: string | null;
  sourceGoverningFromOn: string;
  businessDate: string;
  decisionRequired: boolean;
  requiredDecisionCount: number;
  series: LeaseAgreementEffectiveAddendumSeriesItem[];
}

export interface LeaseAgreementDraftSigner {
  leaseAgreementSignerId: number;
  leaseManagementPartyId: number | null;
  tenantId: number | null;
  signerRole: LeaseLegalSignerRole;
  nameSnapshot: string;
  emailSnapshot: string;
  signingOrder: number;
  isRequired: boolean;
}

export interface LeaseAgreementDraftDetail {
  leaseManagementId: number;
  leaseAgreementId: number;
  publicId: string;
  versionNumber: number;
  draftRevision: number;
  agreementNumber: string;
  changeType: LeaseAgreementChangeType;
  termType: LeaseAgreementTermType;
  termStartOn: string;
  termEndOn: string | null;
  governingFromOn: string;
  baseRentAmount: number;
  rentDueDay: number;
  securityDepositObligation: number;
  lateFeeAmount: number;
  gracePeriodDays: number;
  currency: string;
  termsSchemaVersion: number;
  termsPayload: Record<string, unknown>;
  documentSourceVersionId: number;
  documentTemplateId: number | null;
  documentTemplateVersion: number | null;
  signers: LeaseAgreementDraftSigner[];
  createdAtUtc: string;
  updatedAtUtc: string;
}

export interface EditLeaseAgreementDraftRequest {
  draftRevision: number;
  agreementNumber: string;
  termType: LeaseAgreementTermType;
  termStartOn: string;
  termEndOn: string | null;
  governingFromOn: string;
  baseRentAmount: number;
  rentDueDay: number;
  securityDepositObligation: number;
  lateFeeAmount: number;
  gracePeriodDays: number;
  termsSchemaVersion: number;
  termsPayload: Record<string, unknown>;
  documentTemplateId: number;
  signers: Array<{
    leaseManagementPartyId: number | null;
    tenantId: number | null;
    signerRole: LeaseLegalSignerRole;
    nameSnapshot: string;
    emailSnapshot: string;
    signingOrder: number;
    isRequired: boolean;
  }>;
}

export interface CreateLeaseAgreementSuccessorDraftRequest {
  changeType: Extract<
    LeaseAgreementChangeType,
    "Correction" | "Restatement" | "Renewal" | "MonthToMonth"
  >;
  termStartOn: string;
  termEndOn: string | null;
  governingFromOn: string;
  addendumDecisions: Array<{
    sourceAddendumSeriesPublicId: string;
    decision: LeaseRenewalAddendumDecisionType;
  }>;
}

export interface LeaseAgreementDraftMutationResponse {
  leaseManagementId: number;
  leaseAgreementId: number;
  versionNumber: number;
  draftRevision: number;
  sourceAgreementId: number | null;
  leaseAgreementSignerIds: number[];
  addendumDecisionIds: number[];
  replacementAddendumIds: number[];
  replayed: boolean;
}

export interface LegalDocumentIssuancePreparation {
  pendingUploadId: string;
  draftRevision: number;
  documentSourceVersionId: number;
  issuanceFingerprint: string;
  storageKey: string;
  fileName: string;
  fileSize: number;
  contentSha256: string;
}

export interface IssueLeaseAgreementRequest
  extends LegalDocumentIssuancePreparation {
  subject: string;
}

export interface IssueLeaseAgreementResponse {
  signatureRequestPublicId: string;
  leaseManagementId: number;
  leaseAgreementId: number;
  signatureRequestId: number;
  issuedArtifactId: number;
  replayed: boolean;
}

export type SignatureRequestStatus =
  | "Prepared"
  | "Dispatching"
  | "AwaitingSignatures"
  | "Viewed"
  | "PartiallySigned"
  | "ExecutionPending"
  | "Completed"
  | "Declined"
  | "Voided"
  | "DeliveryFailed";

export type SignatureSignerStatus = "Pending" | "Viewed" | "Signed" | "Declined";

export interface LeaseAgreementSignatureProgressSigner {
  leaseAgreementSignerId: number;
  signerRole: LeaseLegalSignerRole;
  nameSnapshot: string;
  emailSnapshot: string;
  signingOrder: number;
  isRequired: boolean;
  status: SignatureSignerStatus;
  deliveryQueuedAtUtc: string;
  viewedAtUtc: string | null;
  consentGivenAtUtc: string | null;
  signedAtUtc: string | null;
  declinedAtUtc: string | null;
}

export interface LeaseAgreementSignatureProgress {
  leaseManagementId: number;
  leaseAgreementId: number;
  subject: string;
  status: SignatureRequestStatus;
  totalSignerCount: number;
  requiredSignerCount: number;
  signedSignerCount: number;
  declinedSignerCount: number;
  issuedArtifactReady: boolean;
  executedArtifactReady: boolean;
  preparedAtUtc: string;
  providerAcceptedAtUtc: string | null;
  completedAtUtc: string | null;
  declinedAtUtc: string | null;
  voidedAtUtc: string | null;
  failureCode: string | null;
  signers: LeaseAgreementSignatureProgressSigner[];
}

export interface PrepareMoveInPartyRequest {
  tenantId: number;
  role: LeaseManagementPartyRole;
  guarantorLegalNoticeEligible: boolean;
  changeReason: string;
  isAgreementSigner: boolean;
  signingOrder: number | null;
  isRequiredSigner: boolean;
}

export interface PrepareMoveInRequest {
  applicationId: number;
  unitId: number;
  plannedPossessionAtUtc: string | null;
  partyEffectiveFrom: string;
  parties: PrepareMoveInPartyRequest[];
  documentTemplateId: number;
  termType: LeaseAgreementTermType;
  termStartOn: string;
  termEndOn: string | null;
  baseRentAmount: number;
  rentDueDay: number;
  securityDepositObligation: number;
  lateFeeAmount: number;
  gracePeriodDays: number;
  termsSchemaVersion: number;
  termsPayload: Record<string, unknown>;
  createSecurityDepositAccount: boolean;
  openingBalanceAmount: number | null;
  openingBalanceEffectiveOn: string | null;
  openingBalanceNote: string | null;
}

export interface PrepareMoveInResponse {
  applicationId: number;
  leaseManagementId: number;
  tenantAccountId: number;
  leaseAgreementId: number;
  openingBalanceLedgerEntryId: number | null;
  securityDepositAccountId: number | null;
  leaseManagementPartyIds: number[];
  leaseAgreementSignerIds: number[];
  replayed: boolean;
}

export interface LeaseManagementCurrentPartiesContext {
  parties: LeaseManagementParty[];
  activeTenantUserAccesses: ReturnPossessionActiveTenantUserAccess[];
}

export interface GivePossessionRequest {
  unitId: number;
}

export interface GivePossessionResponse {
  leaseManagementId: number;
  unitId: number;
  possessionGivenAtUtc: string;
  replayed: boolean;
}

export type ReturnPossessionPartyDisposition =
  | "EndMembership"
  | "RetainGuarantor";
export type ReturnPossessionAccessDisposition =
  | "RevokeNow"
  | "RetainHistorical";

export interface ReturnPossessionRequest {
  unitId: number;
  parties: Array<{
    leaseManagementPartyId: number;
    disposition: ReturnPossessionPartyDisposition;
  }>;
  accesses: Array<{
    tenantUserAccessId: number;
    disposition: ReturnPossessionAccessDisposition;
  }>;
  turnoverReason: string;
}

export interface ReturnPossessionResponse {
  leaseManagementId: number;
  unitId: number;
  turnoverPeriodId: number;
  possessionReturnedAtUtc: string;
  replayed: boolean;
}

export interface ReturnPossessionActiveTenantUserAccess {
  tenantUserAccessId: number;
  publicId: string;
  leaseManagementPartyId: number;
  accessContextId: number;
  applicationUserId: number;
  tenantName: string;
  userDisplayName: string;
  userEmail: string;
  grantedAtUtc: string;
  reason: string;
}

export interface LeaseManagementPageParams {
  skip?: number;
  take?: number;
  search?: string;
  sort?: string;
  propertyId?: number;
  unitId?: number;
  tenantId?: number;
  lifecycle?: string;
  hasReconciliationException?: boolean;
}

export interface LeaseManagementPage {
  items: LeaseManagementSummary[];
  totalCount: number;
  skip: number;
  take: number;
}

export interface LeaseAgreementHistoryPage {
  items: LeaseAgreementSummary[];
  totalCount: number;
  skip: number;
  take: number;
}

function queryString<T extends object>(params: T): string {
  const query = new URLSearchParams();
  for (const [key, value] of Object.entries(params)) {
    if (value !== undefined && value !== null && value !== "")
      query.set(key, String(value));
  }
  const text = query.toString();
  return text ? `?${text}` : "";
}

function idempotentJson<T>(
  path: string,
  method: "POST" | "PATCH",
  request: unknown,
  operationKey: string
) {
  return fetchApi<T>(path, {
    method,
    headers: { "Idempotency-Key": operationKey },
    body: JSON.stringify(request),
  });
}

export const leaseManagements = {
  prepareMoveIn: (request: PrepareMoveInRequest, operationKey: string) =>
    fetchApi<PrepareMoveInResponse>("/lease-managements/prepare-move-in", {
      method: "POST",
      headers: {
        "Content-Type": "application/json",
        "Idempotency-Key": operationKey,
      },
      body: JSON.stringify(request),
    }),
  listPage: (params: LeaseManagementPageParams = {}) =>
    api.get<LeaseManagementPage>(
      `/lease-managements/page${queryString(params)}`
    ),
  get: (leaseManagementId: number) =>
    api.get<LeaseManagementDetail>(`/lease-managements/${leaseManagementId}`),
  getCurrentPartiesContext: (leaseManagementId: number) =>
    api.get<LeaseManagementCurrentPartiesContext>(
      `/lease-managements/${leaseManagementId}/return-possession-context`
    ),
  getReturnPossessionContext: (leaseManagementId: number) =>
    api.get<LeaseManagementCurrentPartiesContext>(
      `/lease-managements/${leaseManagementId}/return-possession-context`
    ),
  givePossession: (
    leaseManagementId: number,
    request: GivePossessionRequest,
    operationKey: string
  ) =>
    idempotentJson<GivePossessionResponse>(
      `/lease-managements/${leaseManagementId}/give-possession`,
      "POST",
      request,
      operationKey
    ),
  returnPossession: (
    leaseManagementId: number,
    request: ReturnPossessionRequest,
    operationKey: string
  ) =>
    idempotentJson<ReturnPossessionResponse>(
      `/lease-managements/${leaseManagementId}/return-possession`,
      "POST",
      request,
      operationKey
    ),
  ledger: (
    leaseManagementId: number,
    params: { skip?: number; take?: number } = {}
  ) =>
    api.get<LeaseManagementLedger>(
      `/lease-managements/${leaseManagementId}/ledger${queryString(params)}`
    ),
  ask: (leaseManagementId: number, question: string) =>
    api.post<LeaseQuestionResponse>(
      `/lease-managements/${leaseManagementId}/ask`,
      { question }
    ),
  agreements: (
    leaseManagementId: number,
    params: {
      skip?: number;
      take?: number;
      status?: string;
      sort?: string;
    } = {}
  ) =>
    api.get<LeaseAgreementHistoryPage>(
      `/lease-managements/${leaseManagementId}/agreements/page${queryString(
        params
      )}`
    ),
  getAgreementDraft: (leaseManagementId: number, leaseAgreementId: number) =>
    api.get<LeaseAgreementDraftDetail>(
      `/lease-managements/${leaseManagementId}/agreements/${leaseAgreementId}/draft`
    ),
  getAgreementSignatureProgress: (
    leaseManagementId: number,
    leaseAgreementId: number
  ) =>
    api.get<LeaseAgreementSignatureProgress>(
      `/lease-managements/${leaseManagementId}/agreements/${leaseAgreementId}/signature-progress`
    ),
  getEffectiveAddendumSeries: (
    leaseManagementId: number,
    sourceAgreementId: number
  ) =>
    api.get<LeaseAgreementEffectiveAddendumSeries>(
      `/lease-managements/${leaseManagementId}/agreements/${sourceAgreementId}/effective-addendum-series`
    ),
  editAgreementDraft: (
    leaseManagementId: number,
    leaseAgreementId: number,
    request: EditLeaseAgreementDraftRequest,
    operationKey: string
  ) =>
    idempotentJson<LeaseAgreementDraftMutationResponse>(
      `/lease-managements/${leaseManagementId}/agreements/${leaseAgreementId}/draft`,
      "PATCH",
      request,
      operationKey
    ),
  createAgreementSuccessorDraft: (
    leaseManagementId: number,
    sourceAgreementId: number,
    request: CreateLeaseAgreementSuccessorDraftRequest,
    operationKey: string
  ) =>
    idempotentJson<LeaseAgreementDraftMutationResponse>(
      `/lease-managements/${leaseManagementId}/agreements/${sourceAgreementId}/successor-drafts`,
      "POST",
      request,
      operationKey
    ),
  prepareAgreementIssuance: (
    leaseManagementId: number,
    leaseAgreementId: number,
    draftRevision: number,
    operationKey: string
  ) =>
    idempotentJson<LegalDocumentIssuancePreparation>(
      `/lease-managements/${leaseManagementId}/agreements/${leaseAgreementId}/issuance-preparations`,
      "POST",
      { draftRevision },
      operationKey
    ),
  issueAgreement: (
    leaseManagementId: number,
    leaseAgreementId: number,
    request: IssueLeaseAgreementRequest,
    operationKey: string
  ) =>
    idempotentJson<IssueLeaseAgreementResponse>(
      `/lease-managements/${leaseManagementId}/agreements/${leaseAgreementId}/issue`,
      "POST",
      request,
      operationKey
    ),
  downloadArtifact: (
    leaseManagementId: number,
    leaseAgreementId: number,
    artifactId: number
  ) =>
    downloadFile(
      `/lease-managements/${leaseManagementId}/agreements/${leaseAgreementId}/artifacts/${artifactId}`
    ),
  downloadSourceScan: (leaseManagementId: number, leaseAgreementId: number) =>
    downloadFile(
      `/lease-managements/${leaseManagementId}/agreements/${leaseAgreementId}/source-scan`
    ),
};
