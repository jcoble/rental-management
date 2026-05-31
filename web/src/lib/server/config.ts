/**
 * Server-only API base-URL resolution.
 *
 * Server-side requests (hooks, +page.server.ts, +server.ts) reach the API host
 * directly. Driven by the VITE_API_URL env var (with API_URL as a fallback to
 * match the existing vite.config proxy variable). Defaults to the dev API at
 * https://localhost:5666/api/v1.
 *
 * Uses $env/dynamic/private so the value is read at runtime and never leaks
 * into the client bundle. The .NET API mounts controllers under /api/v1/*, so
 * the base always ends in /api/v1.
 */

import { env } from '$env/dynamic/private';

// VITE_API_URL: an explicit FULL base including /api/v1 (e.g. a cross-origin API
// in prod) — used verbatim. Otherwise treat API_URL as the API ROOT (the same
// variable the Vite proxy uses) and append /api/v1; default to the dev API.
function resolveServerApiBase(): string {
	if (env.VITE_API_URL) return env.VITE_API_URL.replace(/\/+$/, '');
	const root = (env.API_URL || 'https://localhost:5666').replace(/\/+$/, '');
	return `${root}/api/v1`;
}

export const SERVER_API_BASE_URL: string = resolveServerApiBase();
