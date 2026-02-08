import { writable, derived } from 'svelte/store';

export interface AgentActivityEntry {
	teamMemberId: number;
	agentName: string;
	role: string;
	status: 'active' | 'idle' | 'stale';
	currentActivity: string | null;
	taskId: number | null;
	taskTitle: string | null;
	sessionSpawnedAt: string | null;
	lastHeartbeat: string | null;
}

export const agentActivityMap = writable<Map<number, AgentActivityEntry>>(new Map());

export const activeAgents = derived(agentActivityMap, ($map) =>
	Array.from($map.values())
		.filter((a) => a.status === 'active')
		.sort((a, b) => (a.sessionSpawnedAt || '').localeCompare(b.sessionSpawnedAt || ''))
);

export const activeAgentCount = derived(activeAgents, ($agents) => $agents.length);

// Lookup: taskId -> assigned agent (for board cards)
export const taskAgentMap = derived(agentActivityMap, ($map) => {
	const result = new Map<number, AgentActivityEntry>();
	for (const entry of $map.values()) {
		if (entry.taskId && entry.status === 'active') {
			result.set(entry.taskId, entry);
		}
	}
	return result;
});
