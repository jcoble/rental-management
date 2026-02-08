<script lang="ts">
	import type { TestStatus, TestType } from '$lib/types';

	let { testType, status }: { testType: TestType; status: TestStatus } = $props();

	const statusColors: Record<TestStatus, string> = {
		Passing: 'bg-success',
		Failing: 'bg-danger',
		Created: 'bg-warning',
		NotCreated: 'bg-text-tertiary',
		Skipped: 'bg-text-tertiary',
	};

	const typeLabels: Record<TestType, string> = {
		Unit: 'U',
		Integration: 'I',
		UI: 'UI',
		Manual: 'M',
	};
</script>

<span
	class="inline-flex items-center gap-1 text-[10px] text-text-secondary"
	title="{testType}: {status}"
>
	<span class="h-1.5 w-1.5 rounded-full {statusColors[status]}"></span>
	{typeLabels[testType]}
</span>
