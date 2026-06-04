<script lang="ts">
	import { Badge } from '$lib/components/ui/badge/index.js';
	import { cn } from '$lib/utils.js';

	/**
	 * Default semantic color map keyed by status string.
	 * Values are Tailwind class strings applied on top of `variant="outline"`.
	 */
	const DEFAULT_MAP: Record<string, { label?: string; class: string }> = {
		// Positive / active
		Active:    { class: 'bg-green-100 text-green-800 border-green-200 dark:bg-green-900/30 dark:text-green-400 dark:border-green-800' },
		Paid:      { class: 'bg-green-100 text-green-800 border-green-200 dark:bg-green-900/30 dark:text-green-400 dark:border-green-800' },
		Approved:  { class: 'bg-green-100 text-green-800 border-green-200 dark:bg-green-900/30 dark:text-green-400 dark:border-green-800' },
		Completed: { class: 'bg-green-100 text-green-800 border-green-200 dark:bg-green-900/30 dark:text-green-400 dark:border-green-800' },
		Done:      { class: 'bg-green-100 text-green-800 border-green-200 dark:bg-green-900/30 dark:text-green-400 dark:border-green-800' },
		// Informational / in-progress
		Pending:   { class: 'bg-blue-100 text-blue-800 border-blue-200 dark:bg-blue-900/30 dark:text-blue-400 dark:border-blue-800' },
		Scheduled: { class: 'bg-blue-100 text-blue-800 border-blue-200 dark:bg-blue-900/30 dark:text-blue-400 dark:border-blue-800' },
		InProgress: { label: 'In Progress', class: 'bg-blue-100 text-blue-800 border-blue-200 dark:bg-blue-900/30 dark:text-blue-400 dark:border-blue-800' },
		Review:    { class: 'bg-purple-100 text-purple-800 border-purple-200 dark:bg-purple-900/30 dark:text-purple-400 dark:border-purple-800' },
		// Warning / partial
		Partial:   { class: 'bg-yellow-100 text-yellow-800 border-yellow-200 dark:bg-yellow-900/30 dark:text-yellow-500 dark:border-yellow-800' },
		Late:      { class: 'bg-orange-100 text-orange-800 border-orange-200 dark:bg-orange-900/30 dark:text-orange-400 dark:border-orange-800' },
		Waived:    { class: 'bg-yellow-100 text-yellow-800 border-yellow-200 dark:bg-yellow-900/30 dark:text-yellow-500 dark:border-yellow-800' },
		OnHold:    { label: 'On Hold', class: 'bg-yellow-100 text-yellow-800 border-yellow-200 dark:bg-yellow-900/30 dark:text-yellow-500 dark:border-yellow-800' },
		PendingSignature: { label: 'Pending Signature', class: 'bg-yellow-100 text-yellow-800 border-yellow-200 dark:bg-yellow-900/30 dark:text-yellow-500 dark:border-yellow-800' },
		// Negative / alert
		Overdue:   { class: 'bg-red-100 text-red-800 border-red-200 dark:bg-red-900/30 dark:text-red-400 dark:border-red-800' },
		Cancelled: { class: 'bg-red-100 text-red-800 border-red-200 dark:bg-red-900/30 dark:text-red-400 dark:border-red-800' },
		Blocked:   { class: 'bg-red-100 text-red-800 border-red-200 dark:bg-red-900/30 dark:text-red-400 dark:border-red-800' },
		// Neutral
		Draft:     { class: 'bg-muted text-muted-foreground border-border' },
		Archived:  { class: 'bg-muted text-muted-foreground border-border' },
		Inactive:  { class: 'bg-muted text-muted-foreground border-border' },
	};

	let {
		status,
		map,
		class: className
	}: {
		status: string;
		/** Override or extend the default color map. */
		map?: Record<string, { label?: string; class: string }>;
		class?: string;
	} = $props();

	const effectiveMap = $derived({ ...DEFAULT_MAP, ...(map ?? {}) });
	const entry = $derived(effectiveMap[status]);
	const label = $derived(entry?.label ?? status);
	const colorClass = $derived(
		entry?.class ?? 'bg-muted text-muted-foreground border-border'
	);
</script>

<Badge
	variant="outline"
	class={cn('text-[11px] font-medium px-2 py-0.5', colorClass, className)}
	data-testid="status-badge"
	data-status={status}
>
	{label}
</Badge>
