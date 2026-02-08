import { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js';
import { z } from 'zod';
import { api, getActiveProjectId, setActiveProjectId } from '../api-client.js';

export function registerProjectTools(server: McpServer) {
  server.tool(
    'list_projects',
    'List all projects. Use set_active_project to switch the default project for subsequent calls.',
    {},
    async () => {
      const projects = await api.get('/projects');
      const activeId = getActiveProjectId();
      return { content: [{ type: 'text' as const, text: `Active project ID: ${activeId}\n\n${JSON.stringify(projects, null, 2)}` }] };
    }
  );

  server.tool(
    'set_active_project',
    'Set the active project ID for this session. All subsequent tools will use this project by default.',
    {
      projectId: z.number().describe('Project ID to set as active'),
    },
    async ({ projectId }) => {
      setActiveProjectId(projectId);
      const project = await api.get(`/projects/${projectId}`);
      return { content: [{ type: 'text' as const, text: `Active project set to ${projectId}\n\n${JSON.stringify(project, null, 2)}` }] };
    }
  );

  server.tool(
    'get_project_context',
    'Load full project state including active milestone, phases, tasks, and recent activity',
    {
      projectId: z.number().optional().describe('Project ID (uses active project if omitted)'),
    },
    async ({ projectId }) => {
      const pid = projectId ?? getActiveProjectId();
      const context = await api.get(`/ai/context?projectId=${pid}`);
      return { content: [{ type: 'text' as const, text: JSON.stringify(context, null, 2) }] };
    }
  );

  server.tool(
    'search_tasks',
    'Search tasks by title/description, optionally filtered by status, priority, or phase',
    {
      query: z.string().optional().describe('Search query for title/description'),
      status: z.string().optional().describe('Filter by status: Backlog, Todo, InProgress, Review, Blocked, Done'),
      priority: z.string().optional().describe('Filter by priority: P1, P2, P3, P4'),
      phaseId: z.number().optional().describe('Filter by phase ID'),
      projectId: z.number().optional().describe('Project ID (uses active project if omitted)'),
    },
    async ({ query, status, priority, phaseId, projectId }) => {
      const params = new URLSearchParams();
      const pid = projectId ?? getActiveProjectId();
      params.set('projectId', pid.toString());
      if (query) params.set('search', query);
      if (status) params.set('status', status);
      if (priority) params.set('priority', priority);
      if (phaseId) params.set('phaseId', phaseId.toString());
      const tasks = await api.get(`/tasks?${params}`);
      return { content: [{ type: 'text' as const, text: JSON.stringify(tasks, null, 2) }] };
    }
  );

  server.tool(
    'get_activity',
    'Get recent activity feed for the project',
    {
      projectId: z.number().optional().describe('Project ID (uses active project if omitted)'),
      limit: z.number().default(20).describe('Number of activity items to return'),
    },
    async ({ projectId, limit }) => {
      const pid = projectId ?? getActiveProjectId();
      const activity = await api.get(`/projects/${pid}/activity?pageSize=${limit}`);
      return { content: [{ type: 'text' as const, text: JSON.stringify(activity, null, 2) }] };
    }
  );

  server.tool(
    'get_metrics',
    'Get project metrics summary including task counts, test stats, and phase progress',
    {
      projectId: z.number().optional().describe('Project ID (uses active project if omitted)'),
    },
    async ({ projectId }) => {
      const pid = projectId ?? getActiveProjectId();
      const dashboard = await api.get(`/projects/${pid}/dashboard`);
      return { content: [{ type: 'text' as const, text: JSON.stringify(dashboard, null, 2) }] };
    }
  );
}
