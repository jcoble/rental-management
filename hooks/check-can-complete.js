#!/usr/bin/env node

/**
 * PreToolUse hook for complete_task enforcement.
 *
 * Checks /api/tasks/{taskId}/can-complete before allowing complete_task to run.
 * Exit code 2 = block the tool call (with reason on stderr).
 * Exit code 0 = allow the tool call.
 */

const fs = require('fs');
const path = require('path');

const API_URL = process.env.LIFECYCLE_API_URL || 'http://localhost:5665';

function getApiKey() {
  // Try env var first, then key file
  if (process.env.LIFECYCLE_API_KEY) return process.env.LIFECYCLE_API_KEY;
  try {
    const keyPath = path.join(process.env.HOME || '', '.lifecycle-api-key');
    return fs.readFileSync(keyPath, 'utf-8').trim();
  } catch {
    return '';
  }
}

async function main() {
  // Read hook input from stdin
  let input = '';
  for await (const chunk of process.stdin) {
    input += chunk;
  }

  let hookData;
  try {
    hookData = JSON.parse(input);
  } catch {
    // Can't parse input, allow through
    process.exit(0);
  }

  const taskId = hookData?.tool_input?.taskId;
  if (!taskId) {
    // No taskId, allow through
    process.exit(0);
  }

  const apiKey = getApiKey();

  try {
    const res = await fetch(`${API_URL}/api/tasks/${taskId}/can-complete`, {
      headers: {
        'Content-Type': 'application/json',
        'X-API-Key': apiKey,
      },
    });

    if (!res.ok) {
      // API error — allow through (don't block on API issues)
      process.exit(0);
    }

    const result = await res.json();

    if (result.canComplete === false) {
      // Block the tool call
      const reason = result.reason || 'UI test plan requirements not met';
      process.stderr.write(
        `BLOCKED: ${reason}\n\n` +
        `Task ${taskId} has a RequiredTestLevel but no passing test plan execution.\n` +
        `Use create_test_plan to create a UI test plan, then execute it with agent-browser.\n`
      );
      process.exit(2);
    }

    // All good, allow
    process.exit(0);
  } catch {
    // Network error — allow through (don't block if API is down)
    process.exit(0);
  }
}

main();
