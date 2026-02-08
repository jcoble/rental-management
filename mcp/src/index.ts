import { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js';
import { StdioServerTransport } from '@modelcontextprotocol/sdk/server/stdio.js';
import { registerPortfolioTools } from './tools/portfolio-tools.js';
import { registerLeasingTools } from './tools/leasing-tools.js';
import { registerOperationsTools } from './tools/operations-tools.js';
import { registerAiTools } from './tools/ai-tools.js';

const server = new McpServer({
  name: 'rental-command',
  version: '1.0.0',
});

registerPortfolioTools(server);
registerLeasingTools(server);
registerOperationsTools(server);
registerAiTools(server);

async function main() {
  const transport = new StdioServerTransport();
  await server.connect(transport);
}

main().catch(console.error);
