import { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js';
import { z } from 'zod';
import { api } from '../api-client.js';

export function registerPhaseTools(server: McpServer) {
  server.tool(
    'create_phase',
    'Create a new phase within a milestone',
    {
      milestoneId: z.number().describe('Milestone ID'),
      name: z.string().describe('Phase name'),
      description: z.string().optional(),
      goal: z.string().optional().describe('Phase goal'),
      successCriteria: z.string().optional().describe('Success criteria in markdown'),
      phaseNumber: z.number().describe('Phase number for ordering'),
    },
    async ({ milestoneId, ...data }) => {
      const result = await api.post(`/milestones/${milestoneId}/phases`, data);
      return { content: [{ type: 'text' as const, text: JSON.stringify(result, null, 2) }] };
    }
  );

  server.tool(
    'get_phase',
    'Get detailed information about a specific phase including its tasks',
    {
      phaseId: z.number().describe('Phase ID'),
    },
    async ({ phaseId }) => {
      const result = await api.get(`/phases/${phaseId}`);
      return { content: [{ type: 'text' as const, text: JSON.stringify(result, null, 2) }] };
    }
  );

  server.tool(
    'list_phases',
    'List all phases for a milestone, ordered by phase number',
    {
      milestoneId: z.number().describe('Milestone ID'),
    },
    async ({ milestoneId }) => {
      const result = await api.get(`/milestones/${milestoneId}/phases`);
      return { content: [{ type: 'text' as const, text: JSON.stringify(result, null, 2) }] };
    }
  );

  server.tool(
    'update_phase',
    'Update phase properties (name, description, goal, success criteria)',
    {
      phaseId: z.number().describe('Phase ID'),
      name: z.string().optional().describe('Phase name'),
      description: z.string().optional().describe('Phase description'),
      goal: z.string().optional().describe('Phase goal'),
      successCriteria: z.string().optional().describe('Success criteria in markdown'),
      phaseNumber: z.number().optional().describe('Phase number for ordering'),
      dependsOnPhaseIds: z.string().optional().describe('Comma-separated phase IDs this phase depends on'),
    },
    async ({ phaseId, ...data }) => {
      const body: Record<string, unknown> = {};
      if (data.name !== undefined) body.name = data.name;
      if (data.description !== undefined) body.description = data.description;
      if (data.goal !== undefined) body.goal = data.goal;
      if (data.successCriteria !== undefined) body.successCriteria = data.successCriteria;
      if (data.phaseNumber !== undefined) body.phaseNumber = data.phaseNumber;
      if (data.dependsOnPhaseIds !== undefined) body.dependsOnPhaseIds = data.dependsOnPhaseIds;
      const result = await api.patch(`/phases/${phaseId}`, body);
      return { content: [{ type: 'text' as const, text: JSON.stringify(result, null, 2) }] };
    }
  );

  server.tool(
    'delete_phase',
    'Delete a phase. Tasks in the phase will be unassigned but not deleted.',
    {
      phaseId: z.number().describe('Phase ID'),
    },
    async ({ phaseId }) => {
      await api.delete(`/phases/${phaseId}`);
      return { content: [{ type: 'text' as const, text: JSON.stringify({ success: true, message: `Phase ${phaseId} deleted` }) }] };
    }
  );

  server.tool(
    'change_phase_status',
    'Change a phase status',
    {
      phaseId: z.number().describe('Phase ID'),
      status: z.enum(['NotStarted', 'Planning', 'InProgress', 'Review', 'Blocked', 'Completed', 'Cancelled']).describe('New phase status'),
    },
    async ({ phaseId, status }) => {
      const result = await api.post(`/phases/${phaseId}/status`, { status });
      return { content: [{ type: 'text' as const, text: JSON.stringify(result, null, 2) }] };
    }
  );

  server.tool(
    'breakdown_phase',
    'Create a phase and all its tasks in one call',
    {
      milestoneId: z.number().describe('Milestone ID'),
      phase: z.object({
        name: z.string(),
        description: z.string().optional(),
        goal: z.string().optional(),
        successCriteria: z.string().optional(),
        phaseNumber: z.number(),
      }),
      tasks: z.array(z.object({
        title: z.string(),
        description: z.string().optional(),
        priority: z.enum(['P1', 'P2', 'P3', 'P4']).default('P3'),
        type: z.enum(['Feature', 'Bug', 'Refactor', 'Docs', 'Test', 'Infra', 'Research']).default('Feature'),
      })),
    },
    async ({ milestoneId, phase, tasks }) => {
      const result = await api.post('/ai/phases/breakdown', { milestoneId, phase, tasks });
      return { content: [{ type: 'text' as const, text: JSON.stringify(result, null, 2) }] };
    }
  );
}
