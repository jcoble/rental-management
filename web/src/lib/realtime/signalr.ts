/**
 * SignalR realtime connection service for RentalCommand.
 *
 * Connects to the backend DataUpdateHub at `/api/v1/hubs/updates` and surfaces
 * the two events it broadcasts to the caller's portfolio group:
 *   - `EntityUpdated` → { entityType, entityId, data, timestamp }
 *   - `EntityDeleted` → { entityType, entityId, timestamp }
 * (PascalCase C# properties serialize to camelCase via System.Text.Json.)
 *
 * Mirrors EdiPlatform's signalr service:
 *   - `accessTokenFactory` proactively refreshes a near-expired JWT before
 *     handing it to the hub (websocket transports pass it via ?access_token=),
 *   - built-in automatic reconnect with exponential backoff, then a background
 *     retry loop so transient outages self-heal,
 *   - a `connectionStatus` store for an optional connection banner,
 *   - a thin pub/sub `subscribe()` API so the query bridge can react to events.
 *
 * Adapted to Rental Command's INT user keys (entityId is a number) and its
 * single data-update hub (EdiPlatform fans out per-entity `On*` events; the
 * rental backend uses two generic events keyed by `entityType`).
 */

import { browser } from '$app/environment';
import { writable } from 'svelte/store';
import type { HubConnection } from '@microsoft/signalr';
import { getAuthState, isTokenExpired } from '$lib/stores/auth.svelte';
import { refreshToken } from '$lib/api/client';

/** Mirrors the backend `EntityUpdatePayload` (camelCase over the wire). */
export interface EntityUpdatePayload {
	entityType: string;
	entityId: number;
	data?: unknown;
	timestamp: string;
}

/** Mirrors the backend `EntityDeletePayload` (camelCase over the wire). */
export interface EntityDeletePayload {
	entityType: string;
	entityId: number;
	timestamp: string;
}

export type DataUpdateEvent = 'EntityUpdated' | 'EntityDeleted';

export type DataUpdateHandler = (
	event: DataUpdateEvent,
	payload: EntityUpdatePayload | EntityDeletePayload
) => void;

export type ConnectionStatus = 'pending' | 'connected' | 'reconnecting' | 'disconnected';

export const connectionStatus = writable<ConnectionStatus>('pending');
export const disconnectedSince = writable<number | null>(null);

class SignalRService {
	private connection: HubConnection | null = null;
	private handlers = new Set<DataUpdateHandler>();
	private isConnecting = false;
	private lastHubUrl: string | undefined;

	// Background retry (Tier 2/3) state — picks up where built-in reconnect leaves off.
	private backgroundRetryTimer: ReturnType<typeof setTimeout> | null = null;
	private backgroundRetryCount = 0;
	private readonly maxBackgroundRetries = 10;
	private isBackgroundRetrying = false;

	/**
	 * The dev simulation clock replaces global Date so normal app display follows the simulated
	 * business date. The SignalR JS client also uses global Date internally to schedule client
	 * keepalive pings; under a frozen/offset sim clock that scheduler can drift from real timers and
	 * the API closes the websocket after its ~30s client-timeout window. Keep the hub transport on
	 * real time without weakening the app-wide simulation clock.
	 */
	private runWithRealDate<T>(operation: () => T): T {
		const realDate = browser ? window.__RealDate : undefined;
		if (!realDate || window.Date === realDate) {
			return operation();
		}

		const simulatedDate = window.Date;
		window.Date = realDate;
		try {
			return operation();
		} finally {
			window.Date = simulatedDate;
		}
	}

	private bindSignalRTimersToRealDate(connection: HubConnection): void {
		if (!browser || !window.__RealDate) return;

		const internal = connection as unknown as Record<string, unknown>;
		for (const methodName of ['_resetKeepAliveInterval', '_resetTimeoutPeriod']) {
			const original = internal[methodName];
			if (typeof original !== 'function') continue;

			internal[methodName] = (...args: unknown[]) =>
				this.runWithRealDate(() => original.apply(connection, args));
		}
	}

	/**
	 * Build a fresh HubConnection with all event handlers wired up. Shared by
	 * the initial connect and every background reconnect so behaviour stays
	 * identical across retries.
	 */
	private async buildConnection(hubUrl: string): Promise<HubConnection> {
		const signalR = await import('@microsoft/signalr');

		const connection = new signalR.HubConnectionBuilder()
			.withUrl(hubUrl, {
				accessTokenFactory: async () => {
					// Refresh a near-expired token before the hub reads it, so the
					// websocket handshake doesn't fail auth on a stale JWT.
					if (browser && isTokenExpired(60)) {
						try {
							await refreshToken('signalr');
						} catch {
							/* fall back to the current token */
						}
					}
					return getAuthState().accessToken ?? '';
				}
			})
			.withAutomaticReconnect({
				nextRetryDelayInMilliseconds: (retryContext) => {
					// Tier 1: 8 attempts with exponential backoff (~2 min total).
					const delays = [0, 1000, 2000, 4000, 8000, 16000, 30000, 60000];
					if (retryContext.previousRetryCount >= delays.length) {
						return null; // exhausted — onclose starts the Tier 2 background loop
					}
					return delays[retryContext.previousRetryCount];
				}
			})
			.configureLogging(signalR.LogLevel.Warning)
			.build();
		this.bindSignalRTimersToRealDate(connection);

		connection.onreconnecting(() => {
			connectionStatus.set('reconnecting');
			this.setDisconnectedSince();
		});

		connection.onreconnected(() => {
			this.onConnected();
		});

		connection.onclose(() => {
			this.connection = null;
			connectionStatus.set('reconnecting');
			this.setDisconnectedSince();
			this.startBackgroundReconnect();
		});

		connection.on('EntityUpdated', (payload: EntityUpdatePayload) => {
			this.notifyHandlers('EntityUpdated', payload);
		});

		connection.on('EntityDeleted', (payload: EntityDeletePayload) => {
			this.notifyHandlers('EntityDeleted', payload);
		});

		return connection;
	}

	/** Open the connection. No-ops if already connected/connecting or on the server. */
	async connect(hubUrl: string): Promise<void> {
		if (!browser) return;
		if (this.connection || this.isConnecting) return;

		this.isConnecting = true;
		this.lastHubUrl = hubUrl;

		try {
			this.connection = await this.buildConnection(hubUrl);
			await this.connection.start();
			this.onConnected();
		} catch (error) {
			console.error('[SignalR] Connection failed:', error);
			this.connection = null;
			connectionStatus.set('reconnecting');
			this.setDisconnectedSince();
			this.startBackgroundReconnect();
		} finally {
			this.isConnecting = false;
		}
	}

	private onConnected(): void {
		this.clearBackgroundRetry();
		disconnectedSince.set(null);
		connectionStatus.set('connected');
	}

	private setDisconnectedSince(): void {
		disconnectedSince.update((current) => current ?? Date.now());
	}

	/** Tier 2: start the background reconnect loop after built-in reconnect gives up. */
	private startBackgroundReconnect(): void {
		if (this.isBackgroundRetrying) return;
		this.isBackgroundRetrying = true;
		this.backgroundRetryCount = 0;
		this.scheduleBackgroundRetry();
	}

	private scheduleBackgroundRetry(): void {
		// Tier 2: every 30s up to maxBackgroundRetries; Tier 3: every 60s indefinitely.
		const delay = this.backgroundRetryCount < this.maxBackgroundRetries ? 30000 : 60000;

		this.backgroundRetryTimer = setTimeout(async () => {
			this.backgroundRetryCount++;
			try {
				if (this.lastHubUrl) {
					await this.doReconnect();
				}
			} catch {
				if (this.backgroundRetryCount === this.maxBackgroundRetries) {
					// Tier 2 exhausted — surface a disconnected banner, keep retrying lazily.
					connectionStatus.set('disconnected');
				}
				this.scheduleBackgroundRetry();
			}
		}, delay);
	}

	/** Fresh reconnection that bypasses the connect() guards (used by retries). */
	private async doReconnect(): Promise<void> {
		if (!browser || !this.lastHubUrl) return;
		if (this.connection || this.isConnecting) return;

		this.isConnecting = true;
		try {
			this.connection = await this.buildConnection(this.lastHubUrl);
			await this.connection.start();
			this.onConnected();
		} catch (error) {
			this.connection = null;
			throw error; // caller reschedules
		} finally {
			this.isConnecting = false;
		}
	}

	private clearBackgroundRetry(): void {
		if (this.backgroundRetryTimer) {
			clearTimeout(this.backgroundRetryTimer);
			this.backgroundRetryTimer = null;
		}
		this.isBackgroundRetrying = false;
		this.backgroundRetryCount = 0;
	}

	/** Intentional disconnect (logout). Suppresses the onclose retry loop. */
	async disconnect(): Promise<void> {
		this.clearBackgroundRetry();
		if (this.connection) {
			this.connection.onclose(() => {});
			try {
				await this.connection.stop();
			} catch {
				/* best effort */
			}
			this.connection = null;
		}
		connectionStatus.set('pending');
		disconnectedSince.set(null);
	}

	/** User-initiated reconnect after a permanent disconnect (e.g. tab wake). */
	async reconnect(): Promise<void> {
		this.clearBackgroundRetry();
		if (this.lastHubUrl) {
			await this.doReconnect();
		}
	}

	/** Subscribe to all data-update events. Returns an unsubscribe function. */
	subscribe(handler: DataUpdateHandler): () => void {
		this.handlers.add(handler);
		return () => {
			this.handlers.delete(handler);
		};
	}

	private notifyHandlers(
		event: DataUpdateEvent,
		payload: EntityUpdatePayload | EntityDeletePayload
	): void {
		for (const handler of this.handlers) {
			try {
				handler(event, payload);
			} catch (error) {
				console.error('[SignalR] Handler error:', error);
			}
		}
	}

	get isConnected(): boolean {
		return this.connection?.state === 'Connected';
	}
}

/** Singleton — one hub connection shared across the app. */
export const signalRService = new SignalRService();
