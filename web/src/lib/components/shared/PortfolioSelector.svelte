<script lang="ts">
	import { createQuery } from '@tanstack/svelte-query';
	import { auth } from '$lib/api/endpoints/auth';
	import { adoptAccessSession } from '$lib/api/client';
	import { getAuthState, selectExperience } from '$lib/stores/auth.svelte';
	import { ChevronDown, Building2 } from '@lucide/svelte';

	let { collapsed = false }: { collapsed?: boolean } = $props();

	const authState = getAuthState();

	const contextsQuery = createQuery(() => ({
		queryKey: ['access-contexts'],
		enabled: authState.isAuthenticated,
		queryFn: () => auth.contexts(),
	}));

	let open = $state(false);
	let switching = $state(false);
	const access = $derived(authState.accessEnvelope);
	const choices = $derived(contextsQuery.data ?? []);
	const canSwitch = $derived(choices.length > 1);

	async function selectContext(id: number) {
		if (id === access?.selectedContext.accessContextId || switching) return;
		switching = true;
		try {
			const result = await auth.selectContext(id);
			await adoptAccessSession(result.accessToken, result.accessTokenExpiration, result.access);
			open = false;
		} finally {
			switching = false;
		}
	}

	function handleClickOutside(e: MouseEvent) {
		const target = e.target as HTMLElement;
		if (!target.closest('.portfolio-selector')) {
			open = false;
		}
	}
</script>

<svelte:window onclick={handleClickOutside} />

<div class="portfolio-selector relative px-2 pb-2">
	{#if collapsed}
		<button
			onclick={() => (open = !open)}
			class="m3-state-layer flex w-full items-center justify-center rounded-[var(--m3-shape-full)] px-3 py-2 text-muted-foreground transition-colors hover:bg-secondary hover:text-foreground"
			disabled={!canSwitch}
			aria-label={access?.selectedContext.workspaceName || 'Current workspace'}
			data-m3-tooltip={access?.selectedContext.workspaceName || 'Current workspace'}
		>
			<Building2 class="h-4 w-4 shrink-0" />
		</button>
	{:else}
		<button
			onclick={() => (open = !open)}
			class="m3-field-surface m3-state-layer flex w-full items-center gap-2 px-3 py-1.5 text-sm text-foreground"
		>
			<Building2 class="h-3.5 w-3.5 shrink-0 text-muted-foreground" />
			<span class="flex-1 truncate text-left">{access?.selectedContext.workspaceName || 'Workspace'}</span>
			{#if canSwitch}<ChevronDown class="h-3.5 w-3.5 shrink-0 text-muted-foreground" />{/if}
		</button>
	{/if}

	{#if open && canSwitch}
		<div class="absolute left-2 right-2 top-full z-50 mt-1 rounded-[var(--m3-shape-large)] border border-border bg-card shadow-[var(--m3-elevation-2)]">
			{#if contextsQuery.data}
				<div class="max-h-48 overflow-y-auto py-1">
					{#each choices as context}
						<button
							onclick={() => selectContext(context.accessContextId)}
							disabled={switching}
							class="flex w-full items-center gap-2 px-3 py-1.5 text-sm transition-colors hover:bg-secondary {context.accessContextId === access?.selectedContext.accessContextId ? 'text-primary' : 'text-foreground'}"
						>
							<span class="truncate">{context.workspaceName}</span>
							{#if context.accessContextId === access?.selectedContext.accessContextId}
								<span class="ml-auto text-[10px] text-primary">current</span>
							{/if}
						</button>
					{/each}
				</div>
			{/if}
		</div>
	{/if}
</div>
{#if !collapsed && (access?.availableExperiences.length ?? 0) > 1}
	<div class="px-2 pb-2">
		<label for="active-experience" class="sr-only">Current work area</label>
		<select
			id="active-experience"
			value={authState.activeExperience ?? ''}
			onchange={(event) =>
				void selectExperience(
					event.currentTarget.value as import('$lib/types/user').WorkspaceExperience
				)}
			class="m3-field-surface h-9 w-full px-3 text-sm text-foreground"
		>
			{#each access?.availableExperiences ?? [] as experience}
				<option value={experience}>{experience}</option>
			{/each}
		</select>
	</div>
{/if}
