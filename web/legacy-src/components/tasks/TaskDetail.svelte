<script lang="ts">
	import type { Task, TaskStatus, TaskPriority, TaskType, TestLevel, TestPlan, Comment, Phase, Attachment } from '$lib/types';
	import { tasks as tasksApi } from '$lib/api/endpoints/tasks';
	import { comments as commentsApi } from '$lib/api/endpoints/comments';
	import { testPlans as testPlansApi } from '$lib/api/endpoints/testPlans';
	import StatusBadge from '$lib/components/shared/StatusBadge.svelte';
	import PriorityBadge from '$lib/components/shared/PriorityBadge.svelte';
	import AIBadge from '$lib/components/shared/AIBadge.svelte';
	import TestPlanCard from '$lib/components/testing/TestPlanCard.svelte';
	import TestPlanEditor from '$lib/components/testing/TestPlanEditor.svelte';
	import TestingRequirementBadge from '$lib/components/testing/TestingRequirementBadge.svelte';
	import MarkdownEditor from '$lib/components/shared/MarkdownEditor.svelte';
	import ClipboardDropZone from './ClipboardDropZone.svelte';
	import { formatRelative, formatDate } from '$lib/utils/date';
	import { buildCommitUrl, buildBranchUrl } from '$lib/utils/git';
	import { X, Send, Paperclip, GitBranch, MessageSquare, Trash2, FlaskConical } from '@lucide/svelte';

	let {
		task,
		phases = [] as Phase[],
		repositoryUrl,
		onclose,
		onupdate,
		ondelete,
	}: {
		task: Task;
		phases?: Phase[];
		repositoryUrl?: string;
		onclose?: () => void;
		onupdate?: (task: Task) => void;
		ondelete?: (taskId: number) => void;
	} = $props();

	async function deleteTask() {
		if (!confirm(`Delete task "${task.title}"? This cannot be undone.`)) return;
		await tasksApi.delete(task.id);
		ondelete?.(task.id);
		onclose?.();
	}

	let editingTitle = $state(false);
	let titleInput = $state(task.title);
	let description = $state(task.description || '');
	let commentText = $state('');
	let taskComments = $state<Comment[]>(task.comments || []);
	let taskAttachments = $state<Attachment[]>(task.attachments || []);
	let taskTestPlans = $state<TestPlan[]>(task.testPlans || []);
	let showTestPlanEditor = $state(false);
	let lightboxUrl = $state<string | null>(null);

	const statuses: TaskStatus[] = ['Backlog', 'Todo', 'InProgress', 'Review', 'Blocked', 'Done', 'Cancelled'];
	const priorities: TaskPriority[] = ['P1', 'P2', 'P3', 'P4'];
	const types: TaskType[] = ['Feature', 'Bug', 'Refactor', 'Docs', 'Test', 'Infra', 'Research'];
	const testLevels: (TestLevel | '')[] = ['', 'Smoke', 'Comprehensive', 'FullE2E'];
	// TestAutonomyLevel is now a project-level setting, not per-task

	async function loadFullTask() {
		const full = await tasksApi.get(task.id);
		taskAttachments = full.attachments || [];
		taskComments = full.comments || [];
	}

	async function loadTestPlans() {
		try {
			taskTestPlans = await testPlansApi.listForTask(task.id) || [];
		} catch {
			taskTestPlans = [];
		}
	}

	async function handleTestPlanCreated() {
		showTestPlanEditor = false;
		await loadTestPlans();
	}

	// Load full task detail + test plans on mount
	$effect(() => { loadFullTask(); loadTestPlans(); });

	async function updateField(field: string, value: unknown) {
		const updated = await tasksApi.update(task.id, { [field]: value });
		onupdate?.(updated);
	}

	async function saveTitle() {
		if (titleInput.trim() && titleInput !== task.title) {
			await updateField('title', titleInput.trim());
		}
		editingTitle = false;
	}

	async function saveDescription() {
		if (description !== task.description) {
			await updateField('description', description);
		}
	}

	async function addComment() {
		if (!commentText.trim()) return;
		const comment = await commentsApi.create(task.id, {
			content: commentText.trim(),
			source: 'Manual',
			author: 'User',
		});
		taskComments = [...taskComments, comment];
		commentText = '';
	}

	function handleAttachmentUpload(attachment: Attachment) {
		taskAttachments = [...taskAttachments, attachment];
	}

	function handleKeydown(e: KeyboardEvent) {
		if (e.key === 'Escape') {
			if (lightboxUrl) { lightboxUrl = null; return; }
			onclose?.();
		}
	}
</script>

<svelte:window onkeydown={handleKeydown} />

<!-- Backdrop -->
<button
	class="fixed inset-0 z-40 bg-black/50"
	onclick={onclose}
	tabindex="-1"
	aria-label="Close task detail"
></button>

<!-- Panel -->
<div class="fixed top-0 right-0 z-50 flex h-full w-full max-w-lg flex-col border-l border-border bg-bg shadow-xl">
	<!-- Header -->
	<div class="flex items-center justify-between border-b border-border px-4 py-3">
		<div class="flex items-center gap-2">
			<span class="font-mono text-xs text-text-tertiary">#{task.id}</span>
			{#if task.source === 'Claude'}
				<AIBadge />
			{/if}
		</div>
		<div class="flex items-center gap-1">
			<button
				onclick={deleteTask}
				class="rounded p-1 text-text-tertiary transition-colors hover:bg-danger/10 hover:text-danger"
				title="Delete task"
			>
				<Trash2 class="h-4 w-4" />
			</button>
			<button
				onclick={onclose}
				class="rounded p-1 text-text-tertiary transition-colors hover:bg-surface-hover hover:text-text-primary"
			>
				<X class="h-4 w-4" />
			</button>
		</div>
	</div>

	<!-- Body -->
	<div class="flex-1 overflow-y-auto px-4 py-4 space-y-5">
		<!-- Title -->
		{#if editingTitle}
			<input
				type="text"
				bind:value={titleInput}
				onblur={saveTitle}
				onkeydown={(e) => { if (e.key === 'Enter') saveTitle(); }}
				class="w-full bg-transparent text-lg font-semibold text-text-primary focus:outline-none"
				autofocus
			/>
		{:else}
			<button
				class="w-full text-left text-lg font-semibold text-text-primary hover:text-accent"
				onclick={() => (editingTitle = true)}
			>
				{task.title}
			</button>
		{/if}

		<!-- Fields grid -->
		<div class="grid grid-cols-2 gap-3">
			<!-- Status -->
			<div>
				<label class="mb-1 block text-xs text-text-tertiary">Status</label>
				<select
					value={task.status}
					onchange={(e) => updateField('status', e.currentTarget.value)}
					class="w-full rounded-md border border-border bg-surface px-2 py-1.5 text-sm text-text-primary focus:border-accent focus:outline-none"
				>
					{#each statuses as s}
						<option value={s}>{s === 'InProgress' ? 'In Progress' : s}</option>
					{/each}
				</select>
			</div>

			<!-- Priority -->
			<div>
				<label class="mb-1 block text-xs text-text-tertiary">Priority</label>
				<select
					value={task.priority}
					onchange={(e) => updateField('priority', e.currentTarget.value)}
					class="w-full rounded-md border border-border bg-surface px-2 py-1.5 text-sm text-text-primary focus:border-accent focus:outline-none"
				>
					{#each priorities as p}
						<option value={p}>{p}</option>
					{/each}
				</select>
			</div>

			<!-- Type -->
			<div>
				<label class="mb-1 block text-xs text-text-tertiary">Type</label>
				<select
					value={task.type}
					onchange={(e) => updateField('type', e.currentTarget.value)}
					class="w-full rounded-md border border-border bg-surface px-2 py-1.5 text-sm text-text-primary focus:border-accent focus:outline-none"
				>
					{#each types as t}
						<option value={t}>{t}</option>
					{/each}
				</select>
			</div>

			<!-- Phase -->
			<div>
				<label class="mb-1 block text-xs text-text-tertiary">Phase</label>
				<select
					value={task.phaseId || ''}
					onchange={(e) => updateField('phaseId', e.currentTarget.value ? Number(e.currentTarget.value) : null)}
					class="w-full rounded-md border border-border bg-surface px-2 py-1.5 text-sm text-text-primary focus:border-accent focus:outline-none"
				>
					<option value="">No Phase</option>
					{#each phases as p}
						<option value={p.id}>{p.name}</option>
					{/each}
				</select>
			</div>
			<!-- Testing Level -->
			<div>
				<label class="mb-1 block text-xs text-text-tertiary">Test Level</label>
				<select
					value={task.requiredTestLevel || ''}
					onchange={(e) => updateField('requiredTestLevel', e.currentTarget.value || null)}
					class="w-full rounded-md border border-border bg-surface px-2 py-1.5 text-sm text-text-primary focus:border-accent focus:outline-none"
				>
					<option value="">None</option>
					<option value="Smoke">Smoke (~30s)</option>
					<option value="Comprehensive">Comprehensive (~5-10m)</option>
					<option value="FullE2E">Full E2E (~15m+)</option>
				</select>
			</div>

			</div>

		<!-- Testing Requirement Badge -->
		{#if task.requiredTestLevel}
			<TestingRequirementBadge requiredLevel={task.requiredTestLevel} testPlans={taskTestPlans} />
		{/if}

		<!-- Git info -->
		{#if task.gitBranch || task.gitCommitSha}
			<div class="flex items-center gap-2 text-xs text-text-tertiary">
				<GitBranch class="h-3 w-3" />
				{#if task.gitBranch}
					{#if repositoryUrl}
						<a href={buildBranchUrl(repositoryUrl, task.gitBranch)} target="_blank" rel="noopener" class="font-mono text-accent hover:underline">{task.gitBranch}</a>
					{:else}
						<span class="font-mono">{task.gitBranch}</span>
					{/if}
				{/if}
				{#if task.gitCommitSha}
					{#if repositoryUrl}
						<a href={buildCommitUrl(repositoryUrl, task.gitCommitSha)} target="_blank" rel="noopener" class="font-mono text-accent hover:underline">{task.gitCommitSha.slice(0, 7)}</a>
					{:else}
						<span class="font-mono">{task.gitCommitSha.slice(0, 7)}</span>
					{/if}
				{/if}
			</div>
		{/if}

		<!-- Description -->
		<div>
			<label class="mb-1 block text-xs text-text-tertiary">Description</label>
			<MarkdownEditor
				value={description}
				placeholder="Add a description..."
				onchange={(v) => { description = v; saveDescription(); }}
			/>
		</div>

		<!-- Labels -->
		{#if task.labels && task.labels.length > 0}
			<div>
				<label class="mb-1 block text-xs text-text-tertiary">Labels</label>
				<div class="flex flex-wrap gap-1">
					{#each task.labels as label}
						<span
							class="inline-flex items-center rounded-full px-2 py-0.5 text-xs font-medium"
							style="background: {label.color}20; color: {label.color};"
						>
							{label.name}
						</span>
					{/each}
				</div>
			</div>
		{/if}

		<!-- Attachments -->
		<div>
			<label class="mb-1 block text-xs text-text-tertiary">
				<Paperclip class="mr-1 inline h-3 w-3" />Attachments
			</label>
			{#if taskAttachments.length > 0}
				<div class="mb-2 grid grid-cols-3 gap-2">
					{#each taskAttachments as attachment}
						{#if attachment.contentType.startsWith('image/')}
							<button
								onclick={() => (lightboxUrl = `/api/attachments/${attachment.id}`)}
								class="cursor-pointer overflow-hidden rounded-md border border-border hover:border-accent transition-colors"
							>
								<img
									src="/api/attachments/{attachment.id}"
									alt={attachment.originalFileName}
									class="object-cover h-20 w-full"
								/>
							</button>
						{:else}
							<div class="flex items-center rounded-md border border-border p-2 text-xs text-text-secondary">
								{attachment.originalFileName}
							</div>
						{/if}
					{/each}
				</div>
			{/if}
			<ClipboardDropZone taskId={task.id} onUpload={handleAttachmentUpload} />
		</div>

		<!-- Test Plans -->
		<div>
			<label class="mb-2 flex items-center gap-1 text-xs text-text-tertiary">
				<FlaskConical class="h-3 w-3" />Test Plans ({taskTestPlans.length})
			</label>

			{#if taskTestPlans.length > 0}
				<div class="space-y-2 mb-2">
					{#each taskTestPlans as plan}
						<TestPlanCard {plan} onview={(id) => window.open(`/api/test-plans/${id}`, '_blank')} />
					{/each}
				</div>
			{/if}

			{#if showTestPlanEditor}
				<TestPlanEditor
					taskId={task.id}
					onsave={handleTestPlanCreated}
					oncancel={() => (showTestPlanEditor = false)}
				/>
			{:else}
				<button
					onclick={() => (showTestPlanEditor = true)}
					class="text-xs text-accent hover:text-accent-hover"
				>
					+ Add Test Plan
				</button>
			{/if}
		</div>

		<!-- Comments -->
		<div>
			<label class="mb-2 flex items-center gap-1 text-xs text-text-tertiary">
				<MessageSquare class="h-3 w-3" />Comments
			</label>

			<div class="space-y-3">
				{#each taskComments as comment}
					<div class="rounded-md border border-border bg-surface px-3 py-2">
						<div class="mb-1 flex items-center gap-2 text-xs">
							<span class="font-medium text-text-secondary">{comment.author || 'System'}</span>
							{#if comment.source === 'Claude'}
								<AIBadge />
							{/if}
							<span class="text-text-tertiary">{formatRelative(comment.createdAt)}</span>
						</div>
						<p class="text-sm text-text-primary">{comment.content}</p>
					</div>
				{/each}
			</div>

			<!-- New comment -->
			<div class="mt-2 flex gap-2">
				<input
					type="text"
					bind:value={commentText}
					onkeydown={(e) => { if (e.key === 'Enter') addComment(); }}
					placeholder="Add a comment..."
					class="flex-1 rounded-md border border-border bg-surface px-3 py-1.5 text-sm text-text-primary placeholder:text-text-tertiary focus:border-accent focus:outline-none"
				/>
				<button
					onclick={addComment}
					disabled={!commentText.trim()}
					class="rounded-md bg-accent px-3 py-1.5 text-sm text-white transition-colors hover:bg-accent-hover disabled:opacity-50"
				>
					<Send class="h-3.5 w-3.5" />
				</button>
			</div>
		</div>

		<!-- Meta -->
		<div class="space-y-1 border-t border-border pt-3 text-xs text-text-tertiary">
			<p>Created {formatDate(task.createdAt)}</p>
			{#if task.startedAt}
				<p>Started {formatDate(task.startedAt)}</p>
			{/if}
			{#if task.completedAt}
				<p>Completed {formatDate(task.completedAt)}</p>
			{/if}
		</div>
	</div>
</div>

<!-- Image Lightbox -->
{#if lightboxUrl}
	<button
		class="fixed inset-0 z-[60] flex items-center justify-center bg-black/80 p-8"
		onclick={() => (lightboxUrl = null)}
		aria-label="Close image"
	>
		<img
			src={lightboxUrl}
			alt="Attachment preview"
			class="max-h-full max-w-full rounded-lg object-contain"
		/>
	</button>
{/if}
