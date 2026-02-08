<script lang="ts">
	import { createMutation, createQuery, useQueryClient } from '@tanstack/svelte-query';
	import { portfolios } from '$lib/api/endpoints/portfolios';
	import { getCurrentPortfolioId } from '$lib/stores/portfolio.svelte';

	const queryClient = useQueryClient();
	const portfolioId = $derived(getCurrentPortfolioId());

	const portfolioQuery = createQuery(() => ({
		queryKey: ['portfolio', portfolioId],
		queryFn: () => portfolios.get(portfolioId),
	}));

	let form = $state({
		name: '',
		description: '',
		managementCompanyName: '',
		timeZone: 'America/New_York',
		status: 'Active',
		settings: '{\n  "rentCollectionDay": 1\n}',
	});

	$effect(() => {
		if (portfolioQuery.data) {
			form = {
				name: portfolioQuery.data.name,
				description: portfolioQuery.data.description || '',
				managementCompanyName: portfolioQuery.data.managementCompanyName || '',
				timeZone: portfolioQuery.data.timeZone || 'America/New_York',
				status: portfolioQuery.data.status,
				settings: portfolioQuery.data.settings || '{\n  "rentCollectionDay": 1\n}',
			};
		}
	});

	const updateMutation = createMutation(() => ({
		mutationFn: () => portfolios.update(portfolioId, {
			name: form.name,
			description: form.description,
			managementCompanyName: form.managementCompanyName,
			timeZone: form.timeZone,
			status: form.status as any,
			settings: form.settings,
		}),
		onSuccess: () => {
			queryClient.invalidateQueries({ queryKey: ['portfolio', portfolioId] });
			queryClient.invalidateQueries({ queryKey: ['portfolios'] });
		},
	}));
</script>

<svelte:head>
	<title>Settings - Rental Command</title>
</svelte:head>

<div class="h-full overflow-y-auto p-6">
	<div class="mb-4">
		<h1 class="text-2xl font-bold">Portfolio Settings</h1>
		<p class="text-sm text-text-secondary">Configure timezone, status, and operational defaults.</p>
	</div>

	<div class="max-w-3xl rounded-lg border border-border bg-surface p-5">
		<div class="grid gap-3 md:grid-cols-2">
			<div class="md:col-span-2">
				<label for="settings-name" class="mb-1 block text-xs text-text-tertiary">Portfolio Name</label>
				<input id="settings-name" bind:value={form.name} class="w-full rounded border border-border bg-bg px-3 py-2 text-sm" />
			</div>
			<div class="md:col-span-2">
				<label for="settings-description" class="mb-1 block text-xs text-text-tertiary">Description</label>
				<textarea id="settings-description" bind:value={form.description} rows={3} class="w-full rounded border border-border bg-bg px-3 py-2 text-sm"></textarea>
			</div>
			<div>
				<label for="settings-company" class="mb-1 block text-xs text-text-tertiary">Management Company</label>
				<input id="settings-company" bind:value={form.managementCompanyName} class="w-full rounded border border-border bg-bg px-3 py-2 text-sm" />
			</div>
			<div>
				<label for="settings-timezone" class="mb-1 block text-xs text-text-tertiary">Time Zone</label>
				<input id="settings-timezone" bind:value={form.timeZone} class="w-full rounded border border-border bg-bg px-3 py-2 text-sm" />
			</div>
			<div>
				<label for="settings-status" class="mb-1 block text-xs text-text-tertiary">Status</label>
				<select id="settings-status" bind:value={form.status} class="w-full rounded border border-border bg-bg px-3 py-2 text-sm"><option>Onboarding</option><option>Active</option><option>Archived</option></select>
			</div>
			<div class="md:col-span-2">
				<label for="settings-json" class="mb-1 block text-xs text-text-tertiary">Settings JSON</label>
				<textarea id="settings-json" bind:value={form.settings} rows={8} class="w-full rounded border border-border bg-bg px-3 py-2 text-sm font-mono"></textarea>
			</div>
		</div>
		<div class="mt-4">
			<button onclick={() => updateMutation.mutate()} class="rounded bg-accent px-3 py-2 text-sm text-white" disabled={updateMutation.isPending}>Save Settings</button>
		</div>
	</div>
</div>
