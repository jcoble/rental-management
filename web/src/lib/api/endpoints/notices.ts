import type {
	ApproveNoticeDraftRequest,
	GenerateNoticeDraftsResponse,
	NoticeDraft,
	UpdateNoticeDraftRequest
} from '$lib/types';
import { api } from '../client';

export const notices = {
	list: (status?: string) =>
		api.get<NoticeDraft[]>(`/notices${status ? `?status=${encodeURIComponent(status)}` : ''}`),
	// POST /api/v1/notices/generate. Portfolio-wide when called with no argument (scans every lease);
	// scoped to a single tenant's lease(s) when a tenantId is supplied (the per-tenant "Create notice"
	// action on the tenant page). Supplying a noticeType generates only that type AND forces it even
	// outside the usual trigger window (e.g. an early renewal offer) — matching the mobile type-first
	// flow. Returns the created draft(s) either way.
	generate: (tenantId?: number, noticeType?: string) =>
		api.post<GenerateNoticeDraftsResponse>('/notices/generate', {
			...(tenantId != null ? { tenantId } : {}),
			...(noticeType ? { noticeType } : {})
		}),
	update: (id: number, request: UpdateNoticeDraftRequest) =>
		api.patch<NoticeDraft>(`/notices/${id}`, request),
	approve: (id: number, request: ApproveNoticeDraftRequest) =>
		api.post<NoticeDraft>(`/notices/${id}/approve`, request),
	dismiss: (id: number) => api.post<NoticeDraft>(`/notices/${id}/dismiss`, {})
};
