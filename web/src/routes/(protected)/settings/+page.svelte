<script lang="ts">
	import { createMutation, createQuery, useQueryClient } from '@tanstack/svelte-query';
	import { portfolios } from '$lib/api/endpoints/portfolios';
	import { getCurrentPortfolioId } from '$lib/stores/portfolio.svelte';
	import { Button } from '$lib/components/ui/button';
	import { Input } from '$lib/components/ui/input';
	import * as Select from '$lib/components/ui/select';
	import * as Card from '$lib/components/ui/card';

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
		<p class="text-sm text-muted-foreground">Configure timezone, status, and operational defaults.</p>
	</div>

	<Card.Root class="max-w-3xl gap-0 py-0">
		<Card.Content class="p-5">
			<div class="grid gap-3 md:grid-cols-2">
				<div class="md:col-span-2">
					<label for="settings-name" class="mb-1 block text-xs text-muted-foreground">Portfolio Name</label>
					<Input id="settings-name" bind:value={form.name} />
				</div>
				<div class="md:col-span-2">
					<label for="settings-description" class="mb-1 block text-xs text-muted-foreground">Description</label>
					<textarea id="settings-description" bind:value={form.description} rows={3} class="w-full rounded border border-border bg-background px-3 py-2 text-sm"></textarea>
				</div>
				<div>
					<label for="settings-company" class="mb-1 block text-xs text-muted-foreground">Management Company</label>
					<Input id="settings-company" bind:value={form.managementCompanyName} />
				</div>
				<div>
					<label for="settings-timezone" class="mb-1 block text-xs text-muted-foreground">Time Zone</label>
					<Input id="settings-timezone" bind:value={form.timeZone} />
				</div>
				<div>
					<label for="settings-status" class="mb-1 block text-xs text-muted-foreground">Status</label>
					<Select.Root type="single" bind:value={form.status}>
						<Select.Trigger class="w-full" id="settings-status">
							{form.status || 'Select status'}
						</Select.Trigger>
						<Select.Content>
							<Select.Item value="Onboarding" label="Onboarding">Onboarding</Select.Item>
							<Select.Item value="Active" label="Active">Active</Select.Item>
							<Select.Item value="Archived" label="Archived">Archived</Select.Item>
						</Select.Content>
					</Select.Root>
				</div>
				<div class="md:col-span-2">
					<label for="settings-json" class="mb-1 block text-xs text-muted-foreground">Settings JSON</label>
					<textarea id="settings-json" bind:value={form.settings} rows={8} class="w-full rounded border border-border bg-background px-3 py-2 text-sm font-mono"></textarea>
				</div>
			</div>
			<div class="mt-4">
				<Button onclick={() => updateMutation.mutate()} disabled={updateMutation.isPending}>Save Settings</Button>
			</div>
		</Card.Content>
	</Card.Root>
</div>
