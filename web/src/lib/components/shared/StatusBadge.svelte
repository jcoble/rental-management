<script lang="ts">
	let { status, class: className = '' }: { status: string; class?: string } = $props();

	const colors: Record<string, string> = {
		// Task statuses
		Backlog: 'bg-muted-foreground/20 text-muted-foreground',
		Todo: 'bg-muted-foreground/20 text-muted-foreground',
		InProgress: 'bg-primary/20 text-primary',
		Review: 'bg-warning/20 text-warning',
		Blocked: 'bg-destructive/20 text-destructive',
		Done: 'bg-success/20 text-success',
		Cancelled: 'bg-muted-foreground/20 text-muted-foreground',
		// Phase statuses
		NotStarted: 'bg-muted-foreground/20 text-muted-foreground',
		Planning: 'bg-purple-500/20 text-purple-400',
		Completed: 'bg-success/20 text-success',
		// Project/Milestone statuses
		Active: 'bg-success/20 text-success',
		OnHold: 'bg-warning/20 text-warning',
		Archived: 'bg-muted-foreground/20 text-muted-foreground',
	};

	const displayLabels: Record<string, string> = {
		InProgress: 'In Progress',
		NotStarted: 'Not Started',
		OnHold: 'On Hold',
	};
</script>

<span
	class="inline-flex items-center rounded-full px-2 py-0.5 text-[11px] font-medium {colors[status] || 'bg-muted-foreground/20 text-muted-foreground'} {className}"
>
	{displayLabels[status] || status}
</span>
