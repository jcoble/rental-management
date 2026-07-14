import type {
	ApproveNoticeDraftRequest,
	GenerateNoticeDraftsRequest,
	GenerateNoticeDraftsResponse,
	NoticeDraft,
	NoticeTemplateResponse,
	UpdateNoticeDraftRequest,
	UpsertNoticeTemplateRequest
} from '$lib/types';
import { api } from '../client';
import { idempotentMutation } from '../idempotency';

export const notices = {
	list: (status?: string) =>
		api.get<NoticeDraft[]>(`/notices${status ? `?status=${encodeURIComponent(status)}` : ''}`),
	// POST /api/v1/notices/generate. Portfolio-wide with an empty request; otherwise scoped
	// to a recipient, lease relationship, tenant account, or exact ledger charge. Supplying a
	// noticeType generates only that type.
	generate: (request: GenerateNoticeDraftsRequest = {}) =>
		idempotentMutation(`notice-drafts:generate:${JSON.stringify(request)}`, (key) =>
			api.post<GenerateNoticeDraftsResponse>('/notices/generate', request, {
				headers: { 'Idempotency-Key': key }
			})
		),
	update: (id: number, request: UpdateNoticeDraftRequest) =>
		idempotentMutation(`notice-drafts:update:${id}:${JSON.stringify(request)}`, (key) =>
			api.patch<NoticeDraft>(`/notices/${id}`, request, {
				headers: { 'Idempotency-Key': key }
			})
		),
	approve: (id: number, request: ApproveNoticeDraftRequest) =>
		idempotentMutation(`notice-drafts:approve:${id}:${JSON.stringify(request)}`, (key) =>
			api.post<NoticeDraft>(`/notices/${id}/approve`, request, {
				headers: { 'Idempotency-Key': key }
			})
		),
	dismiss: (id: number) =>
		idempotentMutation(`notice-drafts:dismiss:${id}`, (key) =>
			api.post<NoticeDraft>(`/notices/${id}/dismiss`, {}, {
				headers: { 'Idempotency-Key': key }
			})
		),
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
