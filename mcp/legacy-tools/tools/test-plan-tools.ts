import { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js';
import { z } from 'zod';
import { api } from '../api-client.js';

export function registerTestPlanTools(server: McpServer) {
  server.tool(
    'create_test_plan',
    'Create a UI test plan with steps for a task. Used for agent-browser testing at Smoke/Comprehensive/FullE2E level.',
    {
      taskId: z.number().describe('Task ID to create test plan for'),
      name: z.string().describe('Test plan name (e.g., "Carrier Required - Smoke Test")'),
      testLevel: z.enum(['Smoke', 'Comprehensive', 'FullE2E']).describe('UI testing depth level'),
      description: z.string().optional().describe('Plan description'),
      tests: z.array(z.object({
        name: z.string().describe('Test name'),
        type: z.enum(['Unit', 'Integration', 'UI', 'Manual']).describe('Test type'),
        description: z.string().optional(),
        testFile: z.string().optional().describe('Path to test file'),
        framework: z.string().optional().describe('Test framework'),
        steps: z.array(z.object({
          stepType: z.enum(['Setup', 'Action', 'Assertion', 'Teardown']).default('Action'),
          description: z.string().describe('What to do in this step'),
          expectedResult: z.string().optional().describe('Expected outcome'),
          automationCommand: z.string().optional().describe('agent-browser command for this step'),
          requiresManualVerification: z.boolean().default(false),
        })).optional().describe('Test steps'),
      })).optional().describe('Tests with nested steps'),
    },
    async ({ taskId, name, testLevel, description, tests }) => {
      const body: Record<string, unknown> = {
        name,
        requiredLevel: testLevel,
        description,
        source: 'AI_Generated',
      };

      if (tests && tests.length > 0) {
        body.tests = tests.map(t => ({
          name: t.name,
          type: t.type,
          description: t.description,
          testFile: t.testFile,
          framework: t.framework,
          steps: t.steps?.map(s => ({
            stepType: s.stepType,
            description: s.description,
            expectedResult: s.expectedResult,
            automationCommand: s.automationCommand,
            requiresManualVerification: s.requiresManualVerification,
          })),
        }));
      }

      const result = await api.post(`/tasks/${taskId}/test-plans`, body);
      return { content: [{ type: 'text' as const, text: JSON.stringify(result, null, 2) }] };
    }
  );

  server.tool(
    'record_test',
    'Record that a test file was created for a task. Creates a Test under an auto-generated plan.',
    {
      taskId: z.number().describe('Task ID'),
      testType: z.enum(['Unit', 'Integration', 'UI', 'Manual']).describe('Type of test'),
      testName: z.string().describe('Test name or description'),
      testFile: z.string().optional().describe('Path to test file'),
      framework: z.string().optional().describe('Test framework (e.g., xUnit, Playwright)'),
    },
    async (args) => {
      const result = await api.post('/ai/tests/record', args);
      return { content: [{ type: 'text' as const, text: JSON.stringify(result, null, 2) }] };
    }
  );

  server.tool(
    'record_test_result',
    'Record the result of running tests',
    {
      testId: z.number().describe('Test ID'),
      passed: z.boolean().describe('Whether the test passed'),
      output: z.string().optional().describe('Test output/log'),
    },
    async (args) => {
      const result = await api.post('/ai/tests/result', args);
      return { content: [{ type: 'text' as const, text: JSON.stringify(result, null, 2) }] };
    }
  );

  server.tool(
    'add_test_steps',
    'Add steps to an existing test',
    {
      testId: z.number().describe('Test ID'),
      steps: z.array(z.object({
        stepType: z.enum(['Setup', 'Action', 'Assertion', 'Teardown']).default('Action'),
        description: z.string().describe('What to do in this step'),
        expectedResult: z.string().optional().describe('Expected outcome'),
        automationCommand: z.string().optional().describe('agent-browser command'),
        requiresManualVerification: z.boolean().default(false),
      })).describe('Steps to add'),
    },
    async ({ testId, steps }) => {
      const results = [];
      for (const s of steps) {
        const result = await api.post(`/tests/${testId}/steps`, {
          stepType: s.stepType,
          description: s.description,
          expectedResult: s.expectedResult,
          automationCommand: s.automationCommand,
          requiresManualVerification: s.requiresManualVerification,
        });
        results.push(result);
      }
      return { content: [{ type: 'text' as const, text: JSON.stringify(results, null, 2) }] };
    }
  );

  server.tool(
    'start_test_execution',
    'Start executing a test plan. Returns execution ID to use with record_step_result.',
    {
      testPlanId: z.number().describe('Test plan ID to execute'),
      executionMode: z.enum(['Manual', 'AI_AgentBrowser', 'Playwright', 'Unit_xUnit', 'Unit_Vitest']).default('AI_AgentBrowser').describe('How the test is being executed'),
      executedBy: z.string().optional().describe('Who is running the test (e.g., "claude-code")'),
    },
    async ({ testPlanId, executionMode, executedBy }) => {
      const result = await api.post(`/test-plans/${testPlanId}/execute`, {
        executionMode,
        executedBy: executedBy ?? 'claude-code',
      });
      return { content: [{ type: 'text' as const, text: JSON.stringify(result, null, 2) }] };
    }
  );

  server.tool(
    'record_step_result',
    'Record the result of a test step during execution',
    {
      executionId: z.number().describe('Test execution ID'),
      stepId: z.number().describe('Test step ID'),
      status: z.enum(['Passed', 'Failed', 'Skipped', 'Blocked']).describe('Step result'),
      actualResult: z.string().optional().describe('What actually happened'),
      errorMessage: z.string().optional().describe('Error details if failed'),
      screenshot: z.string().optional().describe('Base64 screenshot from agent-browser'),
      durationMs: z.number().default(0).describe('Step duration in milliseconds'),
    },
    async ({ executionId, stepId, status, actualResult, errorMessage, screenshot, durationMs }) => {
      const result = await api.post(`/test-executions/${executionId}/step-results`, {
        testStepId: stepId,
        status,
        actualResult,
        errorMessage,
        screenshot,
        durationMs,
      });
      return { content: [{ type: 'text' as const, text: JSON.stringify(result, null, 2) }] };
    }
  );

  server.tool(
    'complete_test_execution',
    'Complete a test execution and mark it as passed or failed',
    {
      executionId: z.number().describe('Test execution ID to complete'),
      status: z.enum(['Passed', 'Failed', 'Cancelled']).describe('Final execution status'),
      failureReason: z.string().optional().describe('Why the test failed (if failed)'),
    },
    async ({ executionId, status, failureReason }) => {
      const result = await api.patch(`/test-executions/${executionId}/complete`, {
        status,
        failureReason,
      });
      return { content: [{ type: 'text' as const, text: JSON.stringify(result, null, 2) }] };
    }
  );
}
