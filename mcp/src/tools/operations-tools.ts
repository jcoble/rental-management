import { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js';
import { z } from 'zod';
import { api, getActivePortfolioId } from '../api-client.js';

export function registerOperationsTools(server: McpServer) {
  server.tool(
    'create_work_order',
    'Create maintenance work order with priority/category.',
    {
      propertyId: z.number(),
      title: z.string(),
      description: z.string(),
      unitId: z.number().optional(),
      tenantId: z.number().optional(),
      vendorId: z.number().optional(),
      category: z.string().default('General'),
      priority: z.enum(['Low', 'Normal', 'High', 'Emergency']).default('Normal'),
      status: z.enum(['New', 'Scheduled', 'InProgress', 'WaitingParts', 'Completed', 'Cancelled']).default('New'),
      portfolioId: z.number().optional(),
    },
    async ({ portfolioId, ...payload }) => {
      const pid = portfolioId ?? getActivePortfolioId();
      const workOrder = await api.post('/work-orders', { portfolioId: pid, ...payload });
      return { content: [{ type: 'text' as const, text: JSON.stringify(workOrder, null, 2) }] };
    }
  );

  server.tool(
    'update_work_order_status',
    'Transition work-order status (e.g., InProgress, Completed).',
    {
      workOrderId: z.number(),
      status: z.enum(['New', 'Scheduled', 'InProgress', 'WaitingParts', 'Completed', 'Cancelled']),
    },
    async ({ workOrderId, status }) => {
      const result = await api.post(`/work-orders/${workOrderId}/status`, { status });
      return { content: [{ type: 'text' as const, text: JSON.stringify(result, null, 2) }] };
    }
  );

  server.tool(
    'schedule_appointment',
    'Schedule showing/inspection/maintenance/owner appointments.',
    {
      title: z.string(),
      type: z.enum(['Showing', 'MoveIn', 'MoveOut', 'Inspection', 'MaintenanceVisit', 'OwnerMeeting']),
      scheduledStart: z.string().describe('ISO datetime'),
      propertyId: z.number().optional(),
      unitId: z.number().optional(),
      tenantId: z.number().optional(),
      prospectName: z.string().optional(),
      prospectEmail: z.string().optional(),
      assignedTo: z.string().optional(),
      status: z.enum(['Scheduled', 'Confirmed', 'Completed', 'Cancelled', 'NoShow']).default('Scheduled'),
      portfolioId: z.number().optional(),
    },
    async ({ portfolioId, ...payload }) => {
      const pid = portfolioId ?? getActivePortfolioId();
      const appt = await api.post('/appointments', { portfolioId: pid, ...payload });
      return { content: [{ type: 'text' as const, text: JSON.stringify(appt, null, 2) }] };
    }
  );

  server.tool(
    'record_expense',
    'Record an expense for accounting and owner statements.',
    {
      category: z.string(),
      description: z.string(),
      amount: z.number(),
      incurredAt: z.string().describe('ISO date'),
      propertyId: z.number().optional(),
      vendorId: z.number().optional(),
      status: z.enum(['Pending', 'Approved', 'Paid']).default('Pending'),
      billableToOwner: z.boolean().default(false),
      portfolioId: z.number().optional(),
    },
    async ({ portfolioId, ...payload }) => {
      const pid = portfolioId ?? getActivePortfolioId();
      const expense = await api.post('/expenses', { portfolioId: pid, ...payload });
      return { content: [{ type: 'text' as const, text: JSON.stringify(expense, null, 2) }] };
    }
  );
}
