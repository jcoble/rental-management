import { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js';
import { z } from 'zod';
import { api } from '../api-client.js';

export function registerContextTools(server: McpServer) {
  server.tool(
    'upload_screenshot',
    'Upload a base64-encoded screenshot to a task',
    {
      taskId: z.number().describe('Task ID'),
      base64Data: z.string().describe('Base64-encoded image data'),
      fileName: z.string().default('screenshot.png').describe('File name'),
      contentType: z.string().default('image/png').describe('MIME type'),
    },
    async ({ taskId, base64Data, fileName, contentType }) => {
      const result = await api.post('/ai/attachments/paste', { taskId, base64Data, fileName, contentType });
      return { content: [{ type: 'text' as const, text: JSON.stringify(result, null, 2) }] };
    }
  );
}
