/**
 * Client-safe API base-URL resolution.
 *
 * In the browser we go through the same-origin proxy (`/api/v1`, forwarded to
 * the backend by the Vite dev proxy / production reverse proxy) so the
 * httpOnly cookies ride along first-party. `import.meta.env.VITE_API_URL` can
 * override this when the API is reachable cross-origin with CORS.
 *
 * Server-side code must import SERVER_API_BASE_URL from
 * `$lib/server/config` instead (it reads private env via $env/dynamic/private
 * and must never be bundled into the client).
 */

export const CLIENT_API_BASE_URL: string =
	(import.meta.env.VITE_API_URL as string | undefined) || '/api/v1';

/** Alias for browser consumers. */
export const API_BASE_URL: string = CLIENT_API_BASE_URL;

/**
 * SignalR DataUpdateHub URL, derived from the API base. The backend mounts the
 * hub at `/api/v1/hubs/updates`, i.e. the API base plus `/hubs/updates`. With
 * the default same-origin base (`/api/v1`) this resolves to a relative path the
 * Vite dev proxy / reverse proxy forwards (and upgrades to websockets); with a
 * cross-origin VITE_API_URL it stays absolute.
 */
export const CLIENT_HUB_URL: string =
	CLIENT_API_BASE_URL.replace(/\/+$/, '') + '/hubs/updates';
