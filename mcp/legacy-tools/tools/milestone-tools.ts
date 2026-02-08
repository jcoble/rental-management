import { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js';
import { z } from 'zod';
import { api, getActiveProjectId } from '../api-client.js';

export function registerMilestoneTools(server: McpServer) {
  server.tool(
    'list_milestones',
    'List all milestones for the active project, optionally filtered by status',
    {
      projectId: z.number().optional().default(1).describe('Project ID (defaults to 1)'),
      status: z.enum(['Planning', 'InProgress', 'OnHold', 'Completed', 'Cancelled']).optional().describe('Filter by milestone status'),
    },
    async ({ projectId, status }) => {
      const pid = projectId ?? getActiveProjectId();
      let result = await api.get<any[]>(`/projects/${pid}/milestones`);
      if (status) {
        result = result.filter((m: any) => m.status === status);
      }
      return { content: [{ type: 'text' as const, text: JSON.stringify(result, null, 2) }] };
    }
  );

  server.tool(
    'get_milestone',
    'Get detailed milestone info including phases',
    {
      milestoneId: z.number().describe('Milestone ID'),
    },
    async ({ milestoneId }) => {
      const result = await api.get(`/milestones/${milestoneId}`);
      return { content: [{ type: 'text' as const, text: JSON.stringify(result, null, 2) }] };
    }
  );

  server.tool(
    'create_milestone',
    'Create a new milestone for a project',
    {
      projectId: z.number().optional().default(1).describe('Project ID (defaults to 1)'),
      name: z.string().describe('Milestone name'),
      description: z.string().optional().describe('Milestone description'),
      version: z.string().optional().describe('Version string (e.g., "v1.0")'),
      targetDate: z.string().optional().describe('Target date (ISO 8601)'),
    },
    async ({ projectId, ...data }) => {
      const pid = projectId ?? getActiveProjectId();
      const result = await api.post(`/projects/${pid}/milestones`, data);
      return { content: [{ type: 'text' as const, text: JSON.stringify(result, null, 2) }] };
    }
  );

  server.tool(
    'update_milestone',
    'Update milestone fields',
    {
      milestoneId: z.number().describe('Milestone ID'),
      name: z.string().optional().describe('Milestone name'),
      description: z.string().optional().describe('Milestone description'),
      version: z.string().optional().describe('Version string'),
      targetDate: z.string().optional().describe('Target date (ISO 8601)'),
    },
    async ({ milestoneId, ...data }) => {
      const body: Record<string, unknown> = {};
      if (data.name !== undefined) body.name = data.name;
      if (data.description !== undefined) body.description = data.description;
      if (data.version !== undefined) body.version = data.version;
      if (data.targetDate !== undefined) body.targetDate = data.targetDate;
      const result = await api.patch(`/milestones/${milestoneId}`, body);
      return { content: [{ type: 'text' as const, text: JSON.stringify(result, null, 2) }] };
    }
  );

  server.tool(
    'change_milestone_status',
    'Change milestone status',
    {
      milestoneId: z.number().describe('Milestone ID'),
      status: z.enum(['Planning', 'InProgress', 'OnHold', 'Completed', 'Cancelled']).describe('New milestone status'),
    },
    async ({ milestoneId, status }) => {
      const result = await api.post(`/milestones/${milestoneId}/status`, { status });
      return { content: [{ type: 'text' as const, text: JSON.stringify(result, null, 2) }] };
    }
  );

  server.tool(
    'delete_milestone',
    'Delete a milestone (must have no phases)',
    {
      milestoneId: z.number().describe('Milestone ID'),
    },
    async ({ milestoneId }) => {
      await api.delete(`/milestones/${milestoneId}`);
      return { content: [{ type: 'text' as const, text: JSON.stringify({ success: true, message: `Milestone ${milestoneId} deleted` }) }] };
    }
  );
}
