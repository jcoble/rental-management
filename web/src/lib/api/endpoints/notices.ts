import type {
	ApproveNoticeDraftRequest,
	GenerateNoticeDraftsResponse,
	NoticeDraft,
	NoticeTemplateResponse,
	UpdateNoticeDraftRequest,
	UpsertNoticeTemplateRequest
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
	dismiss: (id: number) => api.post<NoticeDraft>(`/notices/${id}/dismiss`, {}),
	// Notice copy templates: one editable subject/body per notice type, with the merge fields the
	// backend will substitute. The list endpoint returns every type (with hasTemplate=false until
	// the landlord saves one), so the settings editor can render a card per type up front.
	templates: {
		list: () => api.get<NoticeTemplateResponse[]>('/notices/templates'),
		get: (type: string) =>
			api.get<NoticeTemplateResponse>(`/notices/templates/${encodeURIComponent(type)}`),
		upsert: (type: string, body: UpsertNoticeTemplateRequest) =>
			api.put<NoticeTemplateResponse>(`/notices/templates/${encodeURIComponent(type)}`, body)
	}
};
