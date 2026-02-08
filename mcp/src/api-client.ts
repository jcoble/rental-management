const API_URL = process.env.RENTAL_API_URL || process.env.LIFECYCLE_API_URL || 'http://localhost:5666';
const API_KEY = process.env.RENTAL_API_KEY || process.env.LIFECYCLE_API_KEY || '';
const DEFAULT_PORTFOLIO_ID = process.env.RENTAL_PORTFOLIO_ID
  ? parseInt(process.env.RENTAL_PORTFOLIO_ID, 10)
  : process.env.LIFECYCLE_PROJECT_ID
    ? parseInt(process.env.LIFECYCLE_PROJECT_ID, 10)
    : 1;

let _activePortfolioId: number = DEFAULT_PORTFOLIO_ID;

export function getActivePortfolioId(): number {
  return _activePortfolioId;
}

export function setActivePortfolioId(id: number) {
  _activePortfolioId = id;
}

export async function apiRequest<T>(path: string, options: RequestInit = {}): Promise<T> {
  const res = await fetch(`${API_URL}/api${path}`, {
    ...options,
    headers: {
      'Content-Type': 'application/json',
      'X-API-Key': API_KEY,
      ...options.headers,
    },
  });

  if (res.status === 204) return undefined as T;
  if (!res.ok) {
    const text = await res.text();
    throw new Error(`API error ${res.status}: ${text}`);
  }
  return res.json();
}

export const api = {
  get: <T>(path: string) => apiRequest<T>(path),
  post: <T>(path: string, data?: unknown) =>
    apiRequest<T>(path, { method: 'POST', body: data ? JSON.stringify(data) : undefined }),
  patch: <T>(path: string, data: unknown) =>
    apiRequest<T>(path, { method: 'PATCH', body: JSON.stringify(data) }),
  put: <T>(path: string, data: unknown) =>
    apiRequest<T>(path, { method: 'PUT', body: JSON.stringify(data) }),
  delete: (path: string) => apiRequest(path, { method: 'DELETE' }),
};
