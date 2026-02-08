import { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js';
import { z } from 'zod';
import { api, getActivePortfolioId, setActivePortfolioId } from '../api-client.js';

export function registerPortfolioTools(server: McpServer) {
  server.tool(
    'list_portfolios',
    'List all rental portfolios. Use set_active_portfolio for follow-up actions.',
    {},
    async () => {
      const portfolios = await api.get('/portfolios');
      const activeId = getActivePortfolioId();
      return {
        content: [{ type: 'text' as const, text: `Active portfolio ID: ${activeId}\n\n${JSON.stringify(portfolios, null, 2)}` }],
      };
    }
  );

  server.tool(
    'set_active_portfolio',
    'Set active portfolio for this MCP session.',
    {
      portfolioId: z.number().describe('Portfolio ID'),
    },
    async ({ portfolioId }) => {
      setActivePortfolioId(portfolioId);
      const portfolio = await api.get(`/portfolios/${portfolioId}`);
      return {
        content: [{ type: 'text' as const, text: `Active portfolio set to ${portfolioId}\n\n${JSON.stringify(portfolio, null, 2)}` }],
      };
    }
  );

  server.tool(
    'get_portfolio_dashboard',
    'Get occupancy, leasing, maintenance, accounting, and activity dashboard.',
    {
      portfolioId: z.number().optional().describe('Portfolio ID; defaults to active portfolio'),
    },
    async ({ portfolioId }) => {
      const pid = portfolioId ?? getActivePortfolioId();
      const dashboard = await api.get(`/portfolios/${pid}/dashboard`);
      return { content: [{ type: 'text' as const, text: JSON.stringify(dashboard, null, 2) }] };
    }
  );
}
