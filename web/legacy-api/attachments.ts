import type { Attachment } from '$lib/types';
import { api } from '../client';

export const attachments = {
	list: (taskId: number) => api.get<Attachment[]>(`/tasks/${taskId}/attachments`),
	upload: (taskId: number, formData: FormData) =>
		api.upload<Attachment>(`/tasks/${taskId}/attachments`, formData),
	delete: (id: number) => api.delete(`/attachments/${id}`),
};
