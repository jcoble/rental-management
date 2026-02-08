const API_BASE = '/api';

async function request<T>(path: string, options: RequestInit = {}): Promise<T> {
	const res = await fetch(`${API_BASE}${path}`, {
		...options,
		headers: {
			'Content-Type': 'application/json',
			...options.headers,
		},
	});
	if (res.status === 204) return undefined as T;
	if (!res.ok) {
		const error = await res.json().catch(() => ({ error: res.statusText }));
		throw new Error(error.error || res.statusText);
	}
	return res.json();
}

export const api = {
	get: <T>(path: string) => request<T>(path),
	post: <T>(path: string, data?: unknown) =>
		request<T>(path, { method: 'POST', body: data ? JSON.stringify(data) : undefined }),
	patch: <T>(path: string, data: unknown) =>
		request<T>(path, { method: 'PATCH', body: JSON.stringify(data) }),
	put: <T>(path: string, data: unknown) =>
		request<T>(path, { method: 'PUT', body: JSON.stringify(data) }),
	delete: (path: string) => request(path, { method: 'DELETE' }),
	upload: <T>(path: string, formData: FormData) =>
		fetch(`${API_BASE}${path}`, { method: 'POST', body: formData }).then(
			(r) => r.json() as Promise<T>
		),
};
