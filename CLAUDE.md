# Lifecycle Tracker

A project management tool with kanban board, test plans, and team orchestration. Running on Hetzner via Docker.

## Access

| Service | URL (Tailscale) | URL (Local Dev) |
|---------|----------------|-----------------|
| Web UI  | https://redacted-host.example.invalid | https://localhost:5667 |
| API     | https://redacted-host.example.invalid/api | https://localhost:5666 |

## MCP Server

The MCP server lets any Claude session manage tasks, phases, and tests programmatically.

### Setup (Claude Code CLI)

```bash
# Build first
cd mcp && npm install && npm run build && cd ..

# Register with Claude Code (points at Hetzner shared instance)
claude mcp add lifecycle -s user \
  -e LIFECYCLE_API_URL=https://redacted-host.example.invalid/api \
  -e LIFECYCLE_API_KEY=<your-api-key> \
  -- node /path/to/lifecycle-tracker/mcp/build/index.js
```

For local dev:
```bash
claude mcp add lifecycle -s user \
  -e LIFECYCLE_API_URL=http://localhost:5665 \
  -e LIFECYCLE_API_KEY=<your-api-key> \
  -- node /path/to/lifecycle-tracker/mcp/build/index.js
```

### Available MCP Tools

| Tool | Description |
|------|-------------|
| `get_project_context` | Load full project state: milestone, phases, tasks, activity |
| `search_tasks` | Search tasks by title/description, filter by status/priority/phase |
| `get_activity` | Get recent activity feed |
| `get_metrics` | Dashboard metrics: task counts, test stats, phase progress |
| `create_tasks` | Bulk-create tasks from a conversation breakdown |
| `start_task` | Move a task to InProgress |
| `complete_task` | Move a task to Done (optionally with commit SHA) |
| `create_phase` | Create a new phase within a milestone |
| `breakdown_phase` | Create a phase + all its tasks in one call |
| `record_test` | Record that a test was created for a task |
| `record_test_result` | Record pass/fail result of running a test |
| `upload_screenshot` | Upload a base64 screenshot to a task |

### Example Workflows

**Starting work on a task:**
```
1. get_project_context → see what needs doing
2. start_task(taskId: 5) → mark it InProgress
3. ... do the work ...
4. complete_task(taskId: 5, commitSha: "abc123", gitBranch: "feature/x")
```

**Planning a new phase:**
```
1. breakdown_phase(milestoneId: 1, phase: { name: "Auth System", ... }, tasks: [...])
   → creates phase + all tasks in one call
```

**After writing tests:**
```
1. record_test(taskId: 5, testType: "Unit", testName: "OrderTests", testFile: "tests/OrderTests.cs")
2. record_test_result(testId: 1, passed: true, output: "3 passed, 0 failed")
```

## API Quick Reference

All endpoints are under `/api`. Use `Origin: https://localhost:5667` header to bypass API key auth, or set `X-API-Key` header.

### Tasks
```
GET    /api/tasks?status=Todo&priority=P1&search=auth
POST   /api/tasks                    { title, phaseId?, status?, priority?, type? }
GET    /api/tasks/{id}               Full detail with comments, attachments, tests
PATCH  /api/tasks/{id}               Update any field
DELETE /api/tasks/{id}
POST   /api/tasks/{id}/move          { status, orderInColumn }
```

### Phases & Milestones
```
GET    /api/milestones/{id}/phases
POST   /api/milestones/{id}/phases   { name, goal?, phaseNumber, orderIndex }
PATCH  /api/phases/{id}              { status?, name?, goal? }
```

### Test Plans
```
POST   /api/tasks/{id}/test-plans    { name, requiredLevel, steps: [...] }
GET    /api/tasks/{id}/test-plans
POST   /api/test-plans/{id}/execute  { executionMode }
GET    /api/tasks/{id}/can-complete  Check if testing requirements met
```

### Team
```
GET    /api/teams?projectId=1
POST   /api/teams                    { projectId, role, agentName, modelName }
POST   /api/teams/{id}/spawn
POST   /api/teams/{id}/shutdown
POST   /api/tasks/{id}/assign        { teamMemberId }
POST   /api/agents/escalations       { projectId, description, taskId? }
```

### Real-time
```
GET    /api/events?projectId=1       SSE stream (task:created, task:updated, etc.)
```

## Server Management

```bash
# SSH to Hetzner
ssh hetzner-claude

# Check status
docker ps | grep lifecycle

# View logs
docker logs lifecycle-tracker-api-1 --tail 20
docker logs lifecycle-tracker-web-1 --tail 20

# Restart
cd ~/lifecycle-tracker && docker compose restart

# Update from repo
cd ~/lifecycle-tracker && git pull && docker compose up -d --build

# Start fresh (wipes data)
cd ~/lifecycle-tracker && docker compose down -v && docker compose up -d
```
