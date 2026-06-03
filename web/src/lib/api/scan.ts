import { api } from '$lib/api/client';

// ---- TypeScript types mirroring ScanDtos.cs ----

export interface ScanFieldDto {
	name: string;
	value: string;
	confidence: number;
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
	entityType?: string | null;
	entityId?: number | null;
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
		api.post(`/scans/${id}/reject`, { reason })
};
