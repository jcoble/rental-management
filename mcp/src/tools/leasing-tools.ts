import { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js';
import { z } from 'zod';
import { api, getActivePortfolioId } from '../api-client.js';

export function registerLeasingTools(server: McpServer) {
  server.tool(
    'create_tenant',
    'Create a tenant record for the active portfolio.',
    {
      firstName: z.string(),
      lastName: z.string(),
      email: z.string().optional(),
      phone: z.string().optional(),
      emergencyContact: z.string().optional(),
      portfolioId: z.number().optional(),
    },
    async ({ firstName, lastName, email, phone, emergencyContact, portfolioId }) => {
      const pid = portfolioId ?? getActivePortfolioId();
      const tenant = await api.post('/tenants', { portfolioId: pid, firstName, lastName, email, phone, emergencyContact });
      return { content: [{ type: 'text' as const, text: JSON.stringify(tenant, null, 2) }] };
    }
  );

  server.tool(
    'create_lease',
    'Create a lease tied to unit and tenant with rent terms.',
    {
      unitId: z.number(),
      tenantId: z.number(),
      startDate: z.string().describe('ISO date'),
      endDate: z.string().describe('ISO date'),
      monthlyRent: z.number(),
      securityDeposit: z.number(),
      lateFeeAmount: z.number().default(75),
      rentDueDay: z.number().default(1),
      status: z.enum(['Draft', 'Active', 'NoticeGiven', 'Expired', 'Terminated']).default('Draft'),
      portfolioId: z.number().optional(),
    },
    async ({ portfolioId, ...payload }) => {
      const pid = portfolioId ?? getActivePortfolioId();
      const lease = await api.post('/leases', { portfolioId: pid, ...payload });
      return { content: [{ type: 'text' as const, text: JSON.stringify(lease, null, 2) }] };
    }
  );

  server.tool(
    'record_payment_charge',
    'Create a payment ledger charge for a lease.',
    {
      leaseId: z.number(),
      amount: z.number(),
      dueDate: z.string().describe('ISO date'),
      type: z.enum(['Rent', 'SecurityDeposit', 'LateFee', 'Utility', 'Other']).default('Rent'),
      status: z.enum(['Scheduled', 'Paid', 'Partial', 'Late', 'Waived']).default('Scheduled'),
      portfolioId: z.number().optional(),
    },
    async ({ portfolioId, ...payload }) => {
      const pid = portfolioId ?? getActivePortfolioId();
      const payment = await api.post('/payments', { portfolioId: pid, ...payload });
      return { content: [{ type: 'text' as const, text: JSON.stringify(payment, null, 2) }] };
    }
  );

  server.tool(
    'mark_payment_paid',
    'Mark a payment record as paid.',
    {
      paymentId: z.number(),
      paidDate: z.string().optional().describe('ISO timestamp, default now'),
      method: z.string().optional(),
      externalReference: z.string().optional(),
    },
    async ({ paymentId, paidDate, method, externalReference }) => {
      const payment = await api.post(`/payments/${paymentId}/mark-paid`, { paidDate, method, externalReference });
      return { content: [{ type: 'text' as const, text: JSON.stringify(payment, null, 2) }] };
    }
  );
}
