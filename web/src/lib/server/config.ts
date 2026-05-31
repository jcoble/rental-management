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

const DEFAULT_API_BASE_URL = 'https://localhost:5666/api/v1';

export const SERVER_API_BASE_URL: string =
	env.VITE_API_URL || env.API_URL || DEFAULT_API_BASE_URL;
