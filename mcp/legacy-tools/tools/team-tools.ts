import { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js';
import { z } from 'zod';
import { api, getActiveProjectId } from '../api-client.js';

const AGENT_NAME = process.env.LIFECYCLE_AGENT_NAME || '';
let _resolvedTeamMemberId: number | null = null;

async function getMyTeamMemberId(): Promise<number | null> {
  if (_resolvedTeamMemberId) return _resolvedTeamMemberId;
  if (!AGENT_NAME) return null;
  const pid = getActiveProjectId();
  const members = await api.get<any[]>(`/teams?projectId=${pid}`);
  const me = members.find((m: any) => m.agentName === AGENT_NAME);
  if (me) _resolvedTeamMemberId = me.id;
  return _resolvedTeamMemberId;
}

export function registerTeamTools(server: McpServer) {
  server.tool(
    'list_team_members',
    'List all AI team members for a project with their roles, triggers, and status',
    {
      projectId: z.number().optional().describe('Project ID (uses active project if omitted)'),
    },
    async ({ projectId }) => {
      const pid = projectId ?? getActiveProjectId();
      const result = await api.get(`/teams?projectId=${pid}`);
      return { content: [{ type: 'text' as const, text: JSON.stringify(result, null, 2) }] };
    }
  );

  server.tool(
    'create_team_member',
    'Create an AI team member with role, model, trigger statuses, and system prompt. Use get_role_templates first for quick setup.',
    {
      projectId: z.number().optional().describe('Project ID (uses active project if omitted)'),
      agentName: z.string().describe('Unique agent name (e.g., "researcher", "test-planner")'),
      role: z.string().describe('Role name (e.g., Researcher, TestPlanner, TestRunner, Reviewer, Custom)'),
      modelName: z.string().default('claude-sonnet-4-5').describe('Claude model to use'),
      isPersistent: z.boolean().default(true).describe('Whether agent persists across sessions'),
      triggerStatuses: z.string().optional().describe('JSON array of task statuses that trigger this agent, e.g. ["Todo","InProgress"]'),
      spawnPromptTemplate: z.string().optional().describe('System prompt for the agent when spawned'),
    },
    async ({ projectId, agentName, role, modelName, isPersistent, triggerStatuses, spawnPromptTemplate }) => {
      const pid = projectId ?? getActiveProjectId();
      const result = await api.post('/teams', {
        projectId: pid,
        agentName,
        role,
        modelName,
        isPersistent,
        triggerStatuses,
        spawnPromptTemplate,
      });
      return { content: [{ type: 'text' as const, text: JSON.stringify(result, null, 2) }] };
    }
  );

  server.tool(
    'get_role_templates',
    'Get predefined role templates with default triggers and system prompts for quick team setup',
    {},
    async () => {
      const result = await api.get('/teams/role-templates');
      return { content: [{ type: 'text' as const, text: JSON.stringify(result, null, 2) }] };
    }
  );

  server.tool(
    'get_triggered_agents',
    'Get team members that should be triggered when a task enters a given status. Returns agents with their system prompts for spawning.',
    {
      projectId: z.number().optional().describe('Project ID (uses active project if omitted)'),
      status: z.string().describe('Task status to check triggers for (e.g., "Todo", "InProgress", "Review")'),
    },
    async ({ projectId, status }) => {
      const pid = projectId ?? getActiveProjectId();
      const result = await api.get(`/teams/triggered?projectId=${pid}&status=${status}`);
      return { content: [{ type: 'text' as const, text: JSON.stringify(result, null, 2) }] };
    }
  );

  server.tool(
    'spawn_agent_session',
    'Record that an agent session has started for a team member',
    {
      teamMemberId: z.number().describe('Team member ID to spawn'),
      sessionId: z.string().optional().describe('Custom session ID (auto-generated if omitted)'),
    },
    async ({ teamMemberId, sessionId }) => {
      const result = await api.post(`/teams/${teamMemberId}/spawn`, { sessionId });
      return { content: [{ type: 'text' as const, text: JSON.stringify(result, null, 2) }] };
    }
  );

  server.tool(
    'complete_agent_session',
    'Record that an agent session has completed for a team member',
    {
      teamMemberId: z.number().describe('Team member ID to shutdown'),
      tokensUsed: z.number().optional().describe('Tokens consumed during session'),
    },
    async ({ teamMemberId, tokensUsed }) => {
      const result = await api.post(`/teams/${teamMemberId}/shutdown`, { tokensUsed });
      return { content: [{ type: 'text' as const, text: JSON.stringify(result, null, 2) }] };
    }
  );

  server.tool(
    'assign_task_to_agent',
    'Assign a task to a team member',
    {
      taskId: z.number().describe('Task ID to assign'),
      teamMemberId: z.number().describe('Team member ID to assign to'),
      assignedBy: z.string().default('automation').describe('Who assigned it (e.g., "automation", "manual")'),
    },
    async ({ taskId, teamMemberId, assignedBy }) => {
      const result = await api.post(`/tasks/${taskId}/assign`, { teamMemberId, assignedBy });
      return { content: [{ type: 'text' as const, text: JSON.stringify(result, null, 2) }] };
    }
  );

  server.tool(
    'report_activity',
    'Report what you are currently doing. Call periodically during long tasks to keep the dashboard updated. Also serves as a heartbeat. Set LIFECYCLE_AGENT_NAME env var for auto-detection of teamMemberId.',
    {
      teamMemberId: z.number().optional()
        .describe('Team member ID (auto-detected from LIFECYCLE_AGENT_NAME if omitted)'),
      activity: z.string()
        .describe('What you are currently doing (e.g., "Investigating auth bug in UserService.cs")'),
      tokensUsed: z.number().optional()
        .describe('Running token count for this session'),
    },
    async ({ teamMemberId, activity, tokensUsed }) => {
      const memberId = teamMemberId ?? await getMyTeamMemberId();
      if (!memberId) {
        return { content: [{ type: 'text' as const,
          text: 'Error: teamMemberId required. Set LIFECYCLE_AGENT_NAME env var for auto-detection, or pass teamMemberId explicitly.' }] };
      }
      const result = await api.post(`/teams/${memberId}/heartbeat`, { activity, tokensUsed });
      return { content: [{ type: 'text' as const, text: JSON.stringify(result, null, 2) }] };
    }
  );

  server.tool(
    'get_project_settings',
    'Get project settings (tech stack, test commands, URLs) used as {{variables}} in agent prompt templates',
    {
      projectId: z.number().optional().describe('Project ID (uses active project if omitted)'),
    },
    async ({ projectId }) => {
      const pid = projectId ?? getActiveProjectId();
      const result = await api.get(`/projects/${pid}/settings`);
      return { content: [{ type: 'text' as const, text: JSON.stringify(result, null, 2) }] };
    }
  );

  server.tool(
    'update_project_settings',
    'Update project settings (language, framework, test commands, URLs). These become {{variables}} in agent prompts.',
    {
      projectId: z.number().optional().describe('Project ID (uses active project if omitted)'),
      settings: z.record(z.string(), z.string()).describe('Key-value settings object, e.g. {"language":"C#","unitTestCommand":"dotnet test"}'),
    },
    async ({ projectId, settings }) => {
      const pid = projectId ?? getActiveProjectId();
      const result = await api.put(`/projects/${pid}/settings`, { settings });
      return { content: [{ type: 'text' as const, text: JSON.stringify(result, null, 2) }] };
    }
  );
}
