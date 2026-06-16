import { api } from '$lib/api/client';

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
	createdAt: string;
	reviewedAt: string | null;
	confirmedAt: string | null;
	createdEntityType: string | null;
	createdEntityId: number | null;
	/**
	 * Lease-import property/unit proposal (link-existing vs create-new). Populated by the API only for
	 * Lease drafts; null otherwise. The review screen renders this so the empty-portfolio create path is
	 * visible and the user can correct it before confirming.
	 */
	leaseProposal?: LeaseImportProposal | null;
}

export interface ScanCreatedResponse {
	draftId: number;
	status: string;
	fileUrl: string;
}

export interface ScanConfirmResponse {
	expenseId?: number | null;
	paymentId?: number | null;
	workOrderId?: number | null;
	leaseId?: number | null;
	applicationId?: number | null;
	entityType?: string | null;
	entityId?: number | null;
}

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
}

/** Full batch detail with its drafts. */
export interface ScanBatchDetail extends ScanBatchSummary {
	drafts: ScanBatchDraft[];
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

	get: (id: number): Promise<ScanDraftResponse> => api.get<ScanDraftResponse>(`/scans/${id}`),

	upload: (file: File, targetEntityType: string): Promise<ScanCreatedResponse> => {
		const fd = new FormData();
		fd.append('file', file);
		fd.append('targetEntityType', targetEntityType);
		return api.upload<ScanCreatedResponse>('/scans', fd);
	},

	confirm: (id: number, overridesJson: string): Promise<ScanConfirmResponse> =>
		api.post<ScanConfirmResponse>(`/scans/${id}/confirm`, { overridesJson }),

	createVoiceDraft: (audio: Blob): Promise<ScanDraftResponse> => {
		const fd = new FormData();
		fd.append('audio', audio, 'voice.webm');
		return api.upload<ScanDraftResponse>('/voice/drafts', fd);
	},

	reject: (id: number, reason: string): Promise<unknown> =>
		api.post(`/scans/${id}/reject`, { reason }),

	// ---- Bulk batch helpers ----

	/** Upload many files as one batch of scan drafts (defaults to Lease). */
	uploadBatch: (
		files: File[],
		options: UploadBatchOptions = {}
	): Promise<ScanBatchCreatedResponse> => {
		const fd = new FormData();
		for (const file of files) {
			fd.append('files', file);
		}
		fd.append('targetEntityType', options.targetEntityType ?? 'Lease');
		if (options.name) fd.append('name', options.name);
		return api.upload<ScanBatchCreatedResponse>('/scans/batch', fd);
	},

	listBatches: (): Promise<ScanBatchSummary[]> =>
		api.get<ScanBatchSummary[]>('/scans/batches'),

	getBatch: (id: number): Promise<ScanBatchDetail> =>
		api.get<ScanBatchDetail>(`/scans/batches/${id}`)
};
