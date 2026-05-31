import { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js';
import { z } from 'zod';
import { api, getActivePortfolioId } from '../api-client.js';

interface ScanCreatedResponse {
  draftId: number;
  status: string;
  fileUrl: string;
}

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

  server.tool(
    'scan_document',
    'Upload a receipt/invoice (base64) to create a scan draft for human review.',
    {
      fileBase64: z.string().describe('Base64-encoded file contents'),
      fileName: z.string().describe('Original file name, e.g. receipt.jpg'),
      contentType: z.string().describe('MIME type, e.g. image/jpeg or application/pdf'),
      targetEntityType: z.enum(['Expense']).default('Expense'),
      portfolioId: z.number().optional(),
    },
    async ({ fileBase64, fileName, contentType, targetEntityType, portfolioId: _portfolioId }) => {
      // Decode base64 to Buffer, wrap in Blob, add to FormData
      const buffer = Buffer.from(fileBase64, 'base64');
      const blob = new Blob([buffer], { type: contentType });
      const fd = new FormData();
      fd.append('file', blob, fileName);
      fd.append('targetEntityType', targetEntityType);

      const result = await api.upload<ScanCreatedResponse>('/v1/scans', fd);
      return {
        content: [
          {
            type: 'text' as const,
            text: JSON.stringify(
              { draftId: result.draftId, status: result.status, fileUrl: result.fileUrl },
              null,
              2
            ),
          },
        ],
      };
    }
  );
}
