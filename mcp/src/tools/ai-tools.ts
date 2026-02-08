import { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js';
import { z } from 'zod';
import { api, getActivePortfolioId } from '../api-client.js';

export function registerAiTools(server: McpServer) {
  server.tool(
    'ai_intake_triage',
    'Parse free-form operational notes into suggested rental records/actions.',
    {
      message: z.string(),
      portfolioId: z.number().optional(),
    },
    async ({ message, portfolioId }) => {
      const pid = portfolioId ?? getActivePortfolioId();
      const result = await api.post('/ai/intake', { portfolioId: pid, message });
      return { content: [{ type: 'text' as const, text: JSON.stringify(result, null, 2) }] };
    }
  );

  server.tool(
    'ai_portfolio_risk_summary',
    'Get AI-generated risk/opportunity summary for a portfolio.',
    {
      portfolioId: z.number().optional(),
    },
    async ({ portfolioId }) => {
      const pid = portfolioId ?? getActivePortfolioId();
      const result = await api.get(`/ai/portfolio-summary/${pid}`);
      return { content: [{ type: 'text' as const, text: JSON.stringify(result, null, 2) }] };
    }
  );

  server.tool(
    'ai_generate_notice',
    'Generate resident notice drafts (late rent, renewal, inspection, general).',
    {
      noticeType: z.enum(['LateRent', 'Renewal', 'Inspection', 'General']),
      recipientName: z.string(),
      senderName: z.string(),
      dueDate: z.string().describe('ISO date'),
      amount: z.number().optional(),
      context: z.string().optional(),
    },
    async ({ noticeType, recipientName, senderName, dueDate, amount, context }) => {
      const result = await api.post('/ai/generate-notice', {
        noticeType,
        recipientName,
        senderName,
        dueDate,
        amount,
        context,
      });
      return { content: [{ type: 'text' as const, text: JSON.stringify(result, null, 2) }] };
    }
  );
}
