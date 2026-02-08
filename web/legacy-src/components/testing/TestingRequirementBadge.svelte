<script lang="ts">
	import type { TestLevel, TestPlan } from '$lib/types';
	import { CheckCircle, AlertTriangle, Clock, XCircle } from '@lucide/svelte';

	let {
		requiredLevel,
		testPlans = [],
		compact = false,
	}: {
		requiredLevel?: TestLevel | null;
		testPlans?: TestPlan[];
		compact?: boolean;
	} = $props();

	const levelLabels: Record<string, string> = {
		Smoke: 'L1',
		Comprehensive: 'L2',
		FullE2E: 'L3',
	};

	let status = $derived.by(() => {
		if (!requiredLevel) return 'none';
		const qualifying = testPlans.filter(
			(tp) => tp.requiredLevel === requiredLevel || levelOrder(tp.requiredLevel) >= levelOrder(requiredLevel!)
		);
		if (qualifying.length === 0) return 'no-plan';
		const passing = qualifying.some((tp) => tp.status === 'Passing');
		if (passing) return 'passing';
		const failing = qualifying.some((tp) => tp.status === 'Failing');
		if (failing) return 'failing';
		return 'pending';
	});

	function levelOrder(level: TestLevel): number {
		const order: Record<string, number> = { Smoke: 1, Comprehensive: 2, FullE2E: 3 };
		return order[level] ?? 0;
	}

	const statusConfig: Record<string, { color: string; bg: string; icon: typeof CheckCircle | null; label: string }> = {
		none: { color: 'text-text-tertiary', bg: 'bg-text-tertiary/10', icon: null, label: '' },
		'no-plan': { color: 'text-danger', bg: 'bg-danger/10', icon: XCircle, label: 'No plan' },
		pending: { color: 'text-warning', bg: 'bg-warning/10', icon: Clock, label: 'Pending' },
		failing: { color: 'text-danger', bg: 'bg-danger/10', icon: AlertTriangle, label: 'Failing' },
		passing: { color: 'text-success', bg: 'bg-success/10', icon: CheckCircle, label: 'Passing' },
	};
</script>

{#if requiredLevel && status !== 'none'}
	{@const config = statusConfig[status]}
	{@const Icon = config.icon}
	{#if compact}
		<span class="inline-flex items-center gap-0.5 rounded-full px-1.5 py-0.5 text-[10px] font-medium {config.color} {config.bg}">
			{#if Icon}<Icon class="h-2.5 w-2.5" />{/if}
			{levelLabels[requiredLevel]}
		</span>
	{:else}
		<div class="flex items-center gap-1.5 rounded-md px-2 py-1 text-xs {config.color} {config.bg}">
			{#if Icon}<Icon class="h-3.5 w-3.5" />{/if}
			<span class="font-medium">{levelLabels[requiredLevel]} {requiredLevel}</span>
			<span class="opacity-75">- {config.label}</span>
		</div>
	{/if}
{/if}
