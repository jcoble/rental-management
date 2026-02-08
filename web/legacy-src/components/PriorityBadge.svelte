<script lang="ts">
	import type { TaskPriority } from '$lib/types';
	import { cn } from '$lib/utils/cn';

	let { priority, size = 'sm' }: { priority: TaskPriority; size?: 'sm' | 'md' } = $props();

	const colors: Record<TaskPriority, string> = {
		P1: 'bg-p1',
		P2: 'bg-p2',
		P3: 'bg-p3',
		P4: 'bg-p4',
	};

	const sizes = {
		sm: 'h-2 w-2',
		md: 'h-2.5 w-2.5',
	};
</script>

<span
	class={cn('inline-block rounded-full shrink-0', colors[priority], sizes[size])}
	title={priority}
></span>
