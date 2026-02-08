<script lang="ts">
	let { status, class: className = '' }: { status: string; class?: string } = $props();

	const colors: Record<string, string> = {
		// Task statuses
		Backlog: 'bg-text-tertiary/20 text-text-tertiary',
		Todo: 'bg-text-secondary/20 text-text-secondary',
		InProgress: 'bg-accent/20 text-accent',
		Review: 'bg-warning/20 text-warning',
		Blocked: 'bg-danger/20 text-danger',
		Done: 'bg-success/20 text-success',
		Cancelled: 'bg-text-tertiary/20 text-text-tertiary',
		// Phase statuses
		NotStarted: 'bg-text-tertiary/20 text-text-tertiary',
		Planning: 'bg-purple-500/20 text-purple-400',
		Completed: 'bg-success/20 text-success',
		// Project/Milestone statuses
		Active: 'bg-success/20 text-success',
		OnHold: 'bg-warning/20 text-warning',
		Archived: 'bg-text-tertiary/20 text-text-tertiary',
	};

	const displayLabels: Record<string, string> = {
		InProgress: 'In Progress',
		NotStarted: 'Not Started',
		OnHold: 'On Hold',
	};
</script>

<span
	class="inline-flex items-center rounded-full px-2 py-0.5 text-[11px] font-medium {colors[status] || 'bg-text-tertiary/20 text-text-tertiary'} {className}"
>
	{displayLabels[status] || status}
</span>
