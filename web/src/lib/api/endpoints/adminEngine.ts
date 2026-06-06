import { api } from '../client';

/** Health of a single Engine background worker, derived from its persisted heartbeat. */
export interface WorkerStatus {
	workerName: string;
	/** Self-reported status: Running | Stopped | Error. */
	status: string;
	/** Liveness derived from heartbeat staleness: Healthy | Degraded | Stale | Down. */
	healthState: string;
	lastHeartbeatUtc: string;
	startedAtUtc: string;
	processedCount: number;
	cycleCount: number;
	lastErrorMessage: string | null;
	lastErrorUtc: string | null;
	heartbeatSecondsAgo: number;
	errorSecondsAgo: number | null;
}

/** The running Engine process, parsed from heartbeat metadata. */
export interface EngineInstance {
	pid: number | null;
	hostname: string | null;
	version: string | null;
	advisoryLockHeld: boolean;
	lockContested: boolean;
	uptimeSeconds: number;
}

/** Active LLM provider + model used for scan extraction. */
export interface LlmProvider {
	provider: string;
	modelId: string;
}

export interface EngineStatusResponse {
	/** Worst-of-all-workers rollup: Healthy | Degraded | Stale | Down. */
	overallStatus: string;
	instance: EngineInstance | null;
	workers: WorkerStatus[];
	llm: LlmProvider;
}

export const adminEngine = {
	status: () => api.get<EngineStatusResponse>('/admin/engine-status')
};
