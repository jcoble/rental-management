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
	generate: () => api.post<GenerateNoticeDraftsResponse>('/notices/generate', {}),
	update: (id: number, request: UpdateNoticeDraftRequest) =>
		api.patch<NoticeDraft>(`/notices/${id}`, request),
	approve: (id: number, request: ApproveNoticeDraftRequest) =>
		api.post<NoticeDraft>(`/notices/${id}/approve`, request),
	dismiss: (id: number) => api.post<NoticeDraft>(`/notices/${id}/dismiss`, {})
};
