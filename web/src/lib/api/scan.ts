import { api } from '$lib/api/client';
import { buildListQuery, type ListParams } from '$lib/api/list-params';
import { voiceUploadFileName } from '$lib/scan/voice-capture';
import type { ScanContext } from '$lib/scan/scan-context';

const scanUploadOperationIds = new WeakMap<File, Map<string, string>>();
const scanBatchUploadOperationIds = new WeakMap<File[], string>();
const scanRetryOperationIds = new Map<number, string>();
const voiceDraftOperationIds = new WeakMap<Blob, string>();

function singleUploadOperationId(file: File, targetEntityType?: string): string {
	const targetKey = targetEntityType ?? 'Auto';
	let byTarget = scanUploadOperationIds.get(file);
	if (!byTarget) {
		byTarget = new Map();
		scanUploadOperationIds.set(file, byTarget);
	}
	let operationId = byTarget.get(targetKey);
	if (!operationId) {
		operationId = crypto.randomUUID();
		byTarget.set(targetKey, operationId);
	}
	return operationId;
}

// ---- TypeScript types mirroring ScanDtos.cs ----

export interface ScanFieldDto {
	name: string;
	value: string;
	confidence: number;
}

/**
 * One proposed entity in a {@link LeaseImportProposal} (mirrors RentalCommand.Core's ProposedRecord).
 * `action` is "link" when an existing in-portfolio record was matched (`existingId` set), "create" when
 * one would be created from the extracted document (`label`/`detail` describe it), or "select" when
 * there isn't enough on the document to match or create and the reviewer must choose.
 */
export interface ProposedRecord {
	action: 'link' | 'create' | 'select';
	existingId: number | null;
	label: string | null;
	detail: string | null;
}

/**
 * Lease-import preview for a Lease draft: what confirming would do with the property + unit
 * (link an existing record vs create one from the scanned document). Lets a brand-new landlord with an
 * empty portfolio SEE that a Property/Unit will be created — and proceed — rather than being blocked on
 * an empty property dropdown. Null for non-lease drafts.
 */
export interface LeaseImportProposal {
	property: ProposedRecord;
	unit: ProposedRecord;
}

export interface ScanDraftResponse {
	id: number;
	portfolioId: number;
	targetEntityType: string;
	status: string; // Pending | Reviewing | Confirmed | Failed | Rejected
	fileUrl: string;
	fields: ScanFieldDto[];
	modelId: string | null;
	tokensUsed: number | null;
	costUsd: number | null;
	failureReason: string | null;
	createdAt: string;
	reviewedAt: string | null;
	confirmedAt: string | null;
	createdEntityType: string | null;
	createdEntityId: number | null;
	createdUnitId: number | null;
	missingRequired?: string[] | null;
	nextPrompt?: string | null;
	complete?: boolean;
	ambiguous?: boolean;
	/**
	 * Lease-import property/unit proposal (link-existing vs create-new). Populated by the API only for
	 * Lease drafts; null otherwise. The review screen renders this so the empty-portfolio create path is
	 * visible and the user can correct it before confirming.
	 */
	leaseProposal?: LeaseImportProposal | null;
	captureContext?: {
		experience?: string | null;
		accessContextId?: number | null;
		accessRevision?: number | null;
		propertyId?: number | null;
		unitId?: number | null;
		leaseManagementId?: number | null;
		leaseAgreementId?: number | null;
		tenantAccountId?: number | null;
		tenantLedgerEntryId?: number | null;
		workOrderId?: number | null;
		applicationId?: number | null;
		rentalListingId?: number | null;
		sourceLabel?: string | null;
	} | null;
	sourceContentSha256?: string | null;
}

export interface ScanCreatedResponse {
	draftId: number;
	status: string;
	fileUrl: string;
}

export interface ScanConfirmResponse {
	expenseId?: number | null;
	receiptId?: number | null;
	tenantAccountId?: number | null;
	workOrderId?: number | null;
	leaseManagementId?: number | null;
	agreementId?: number | null;
	applicationId?: number | null;
	loanId?: number | null;
	loanPaymentId?: number | null;
	unitId?: number | null;
	entityType?: string | null;
	entityId?: number | null;
	status: 'confirmed' | 'alreadyConfirmed';
	replayed: boolean;
	atomicDisposition: 'Executed' | 'Replayed' | 'Joined';
}

export interface ScanDraftListResponse {
	items: ScanDraftResponse[];
	totalCount: number;
	skip: number;
	take: number;
}

const scanConfirmOperationIds = new Map<number, string>();

// ---- Bulk scan batches (e.g. importing many leases at once) ----

/** Per-status tallies for the drafts in a batch. */
export interface ScanBatchCounts {
	total: number;
	pending: number;
	reviewing: number;
	confirmed: number;
	rejected: number;
	failed: number;
}

/** Summary row for a batch (list view). ScanBatchStatus: Processing | Reviewing | Completed. */
export interface ScanBatchSummary {
	id: number;
	name: string | null;
	targetEntityType: string;
	status: string;
	fileCount: number;
	createdAtUtc: string;
	counts: ScanBatchCounts;
}

/** A single draft within a batch detail view. */
export interface ScanBatchDraft {
	id: number;
	status: string; // Pending | Processing | Reviewing | Confirmed | Rejected | Failed
	targetEntityType: string;
	fileUrl: string;
	tenant: string | null;
	unit: string | null;
	term: string | null;
	/** Id of the entity (e.g. Lease) created once the draft is confirmed. */
	createdEntityId: number | null;
	createdAt: string;
	failureReason: string | null;
}

/** Full batch detail with its drafts. */
export interface ScanBatchDetail extends ScanBatchSummary {
	drafts: ScanBatchDraft[];
	draftTotalCount: number;
	skip: number;
	take: number;
}

/** Response from creating a batch. */
export interface ScanBatchCreatedResponse {
	batchId: number;
	name: string | null;
	targetEntityType: string;
	status: string;
	fileCount: number;
	draftIds: number[];
}

export interface UploadBatchOptions {
	targetEntityType?: string;
	name?: string;
}

// ---- Typed API helpers ----

export const scan = {
	list: (status?: string): Promise<ScanDraftResponse[]> =>
		api.get<ScanDraftResponse[]>(`/scans${status ? `?status=${encodeURIComponent(status)}` : ''}`),

	listPage: (status?: string, params?: ListParams): Promise<ScanDraftListResponse> =>
		api.get<ScanDraftListResponse>(`/scans/page${buildListQuery(params, { status: status || undefined })}`),

	get: (id: number): Promise<ScanDraftResponse> => api.get<ScanDraftResponse>(`/scans/${id}`),

	upload: async (
		file: File,
		targetEntityType: string | undefined,
		context: ScanContext = {}
	): Promise<ScanCreatedResponse> => {
		const operationId = singleUploadOperationId(file, targetEntityType);
		const fd = new FormData();
		fd.append('file', file);
		if (targetEntityType) fd.append('targetEntityType', targetEntityType);
		fd.append('clientOperationId', operationId);
		if (context.propertyId) fd.append('propertyId', String(context.propertyId));
		if (context.unitId) fd.append('unitId', String(context.unitId));
		if (context.leaseManagementId) fd.append('leaseManagementId', String(context.leaseManagementId));
		if (context.leaseAgreementId) fd.append('leaseAgreementId', String(context.leaseAgreementId));
		if (context.tenantAccountId) fd.append('tenantAccountId', String(context.tenantAccountId));
		if (context.tenantLedgerEntryId) fd.append('tenantLedgerEntryId', String(context.tenantLedgerEntryId));
		if (context.workOrderId) fd.append('workOrderId', String(context.workOrderId));
		if (context.applicationId) fd.append('applicationId', String(context.applicationId));
		if (context.rentalListingId) fd.append('rentalListingId', String(context.rentalListingId));
		if (context.sourceLabel) fd.append('sourceLabel', context.sourceLabel);
		const response = await api.upload<ScanCreatedResponse>('/scans', fd);
		scanUploadOperationIds.get(file)?.delete(targetEntityType ?? 'Auto');
		return response;
	},

	confirm: async (id: number, overridesJson: string): Promise<ScanConfirmResponse> => {
		const response = await api.post<ScanConfirmResponse>(`/scans/${id}/confirm`, {
			overridesJson,
			clientOperationId:
				scanConfirmOperationIds.get(id) ??
				(() => {
					const operationId = crypto.randomUUID();
					scanConfirmOperationIds.set(id, operationId);
					return operationId;
				})()
		});
		scanConfirmOperationIds.delete(id);
		return response;
	},

	retry: async (id: number): Promise<unknown> => {
		const operationId = scanRetryOperationIds.get(id) ?? crypto.randomUUID();
		scanRetryOperationIds.set(id, operationId);
		const response = await api.post(
			`/scans/${id}/retry`,
			{},
			{
				headers: { 'Idempotency-Key': operationId }
			}
		);
		scanRetryOperationIds.delete(id);
		return response;
	},

	createVoiceDraft: async (audio: Blob, mimeType?: string): Promise<ScanDraftResponse> => {
		const operationId = voiceDraftOperationIds.get(audio) ?? crypto.randomUUID();
		voiceDraftOperationIds.set(audio, operationId);
		const fd = new FormData();
		// Name the part from the real recording format (Safari/iOS records mp4, not
		// webm) so Whisper decodes the right container. The blob's own type carries
		// the matching Content-Type for the multipart part.
		fd.append('audio', audio, voiceUploadFileName(mimeType ?? audio.type));
		const response = await api.upload<ScanDraftResponse>('/voice/drafts', fd, {
			headers: { 'Idempotency-Key': operationId }
		});
		voiceDraftOperationIds.delete(audio);
		return response;
	},

	reject: (id: number, reason: string): Promise<unknown> => api.post(`/scans/${id}/reject`, { reason }),

	// ---- Bulk batch helpers ----

	/** Upload many files as one batch of scan drafts (defaults to LeaseAgreement). */
	uploadBatch: (files: File[], options: UploadBatchOptions = {}): Promise<ScanBatchCreatedResponse> => {
		const operationId = scanBatchUploadOperationIds.get(files) ?? crypto.randomUUID();
		scanBatchUploadOperationIds.set(files, operationId);
		const fd = new FormData();
		for (const file of files) {
			fd.append('files', file);
		}
		fd.append('targetEntityType', options.targetEntityType ?? 'LeaseAgreement');
		if (options.name) fd.append('name', options.name);
		fd.append('clientOperationId', operationId);
		return api.upload<ScanBatchCreatedResponse>('/scans/batch', fd).then((response) => {
			scanBatchUploadOperationIds.delete(files);
			return response;
		});
	},

	listBatches: (): Promise<ScanBatchSummary[]> => api.get<ScanBatchSummary[]>('/scans/batches'),

	getBatch: (
		id: number,
		params: { skip?: number; take?: number } = {}
	): Promise<ScanBatchDetail> =>
		api.get<ScanBatchDetail>(`/scans/batches/${id}${buildListQuery(params)}`)
};
