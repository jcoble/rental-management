<script lang="ts">
	import { createMutation, createQuery } from '@tanstack/svelte-query';
	import { ai } from '$lib/api/endpoints/ai';
	import { getCurrentPortfolioId } from '$lib/stores/portfolio.svelte';

	const portfolioId = $derived(getCurrentPortfolioId());

	const summaryQuery = createQuery(() => ({
		queryKey: ['ai-summary', portfolioId],
		queryFn: () => ai.portfolioSummary(portfolioId),
	}));

	let intakeText = $state('Tenant in unit 204 reported a water leak and late rent from last month.');
	let noticeType = $state('LateRent');
	let recipientName = $state('Resident');
	let senderName = $state('Property Management Team');
	let dueDate = $state(new Date().toISOString().slice(0, 10));
	let amount = $state('0');
	let context = $state('');

	const intakeMutation = createMutation(() => ({
		mutationFn: () => ai.intake(portfolioId, intakeText),
	}));

	const noticeMutation = createMutation(() => ({
		mutationFn: () =>
			ai.generateNotice({
				noticeType,
				recipientName,
				senderName,
				dueDate,
				amount: Number(amount || 0),
				context,
			}),
	}));
</script>

<svelte:head>
	<title>AI Assistant - Rental Command</title>
</svelte:head>

<div class="h-full overflow-y-auto p-6">
	<div class="mb-4">
		<h1 class="text-2xl font-bold">AI Assistant</h1>
		<p class="text-sm text-text-secondary">Use guided AI actions for intake triage, risk summary, and notice drafts.</p>
	</div>

	<div class="mb-5 grid gap-4 lg:grid-cols-2">
		<div class="rounded-lg border border-border bg-surface p-4">
			<h2 class="mb-2 font-semibold">Intake Triage</h2>
			<textarea bind:value={intakeText} rows={6} class="mb-2 w-full rounded border border-border bg-bg px-3 py-2 text-sm"></textarea>
			<button class="rounded bg-accent px-3 py-2 text-sm text-white" onclick={() => intakeMutation.mutate()} disabled={intakeMutation.isPending}>Analyze Intake</button>
			{#if intakeMutation.data}
				{@const data = intakeMutation.data as any}
				<div class="mt-3 space-y-2 text-sm">
					<p class="font-medium">{data.summary}</p>
					<div>
						<p class="mb-1 text-xs uppercase text-text-tertiary">Suggested records</p>
						{#each data.suggestedRecords || [] as suggestion}
							<div class="rounded border border-border bg-bg px-2 py-1 text-xs">{suggestion.entity}: {suggestion.title}</div>
						{/each}
					</div>
					<div>
						<p class="mb-1 text-xs uppercase text-text-tertiary">Recommended actions</p>
						{#each data.recommendedActions || [] as action}
							<div class="rounded border border-border bg-bg px-2 py-1 text-xs">{action}</div>
						{/each}
					</div>
				</div>
			{/if}
		</div>

		<div class="rounded-lg border border-border bg-surface p-4">
			<h2 class="mb-2 font-semibold">Notice Drafting</h2>
			<div class="space-y-2">
				<select bind:value={noticeType} class="w-full rounded border border-border bg-bg px-3 py-2 text-sm"><option>LateRent</option><option>Renewal</option><option>Inspection</option><option>General</option></select>
				<input bind:value={recipientName} class="w-full rounded border border-border bg-bg px-3 py-2 text-sm" placeholder="Recipient name" />
				<input bind:value={senderName} class="w-full rounded border border-border bg-bg px-3 py-2 text-sm" placeholder="Sender name" />
				<div class="grid grid-cols-2 gap-2">
					<input type="date" bind:value={dueDate} class="rounded border border-border bg-bg px-3 py-2 text-sm" />
					<input bind:value={amount} class="rounded border border-border bg-bg px-3 py-2 text-sm" placeholder="Amount" />
				</div>
				<textarea bind:value={context} rows={3} class="w-full rounded border border-border bg-bg px-3 py-2 text-sm" placeholder="Extra context (optional)"></textarea>
				<button class="rounded bg-accent px-3 py-2 text-sm text-white" onclick={() => noticeMutation.mutate()} disabled={noticeMutation.isPending}>Generate Notice</button>
			</div>
			{#if noticeMutation.data}
				{@const notice = noticeMutation.data as any}
				<div class="mt-3 rounded border border-border bg-bg p-3 text-sm">
					<p class="font-medium">{notice.subject}</p>
					<pre class="mt-2 whitespace-pre-wrap font-sans text-xs text-text-secondary">{notice.body}</pre>
				</div>
			{/if}
		</div>
	</div>

	<div class="rounded-lg border border-border bg-surface p-4">
		<h2 class="mb-2 font-semibold">Portfolio AI Summary</h2>
		{#if summaryQuery.data}
			{@const summary = summaryQuery.data as any}
			<div class="grid gap-4 md:grid-cols-2 text-sm">
				<div>
					<p class="mb-1 text-xs uppercase text-text-tertiary">Risks</p>
					{#each summary.risks || [] as risk}
						<div class="mb-1 rounded border border-border bg-bg px-2 py-1">{risk}</div>
					{/each}
				</div>
				<div>
					<p class="mb-1 text-xs uppercase text-text-tertiary">Opportunities</p>
					{#each summary.opportunities || [] as opp}
						<div class="mb-1 rounded border border-border bg-bg px-2 py-1">{opp}</div>
					{/each}
				</div>
			</div>
		{/if}
	</div>
</div>
