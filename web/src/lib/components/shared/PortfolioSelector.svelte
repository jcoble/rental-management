<script lang="ts">
	import { createQuery } from '@tanstack/svelte-query';
	import { useQueryClient } from '@tanstack/svelte-query';
	import { goto } from '$app/navigation';
	import { auth } from '$lib/api/endpoints/auth';
	import { adoptAccessEnvelope, adoptAccessSession } from '$lib/api/client';
	import { getAuthState } from '$lib/stores/auth.svelte';
	import { safeLandingForAccess } from '$lib/auth/experience-policy';
	import { signalRService } from '$lib/realtime/signalr';
	import { CLIENT_HUB_URL } from '$lib/config';
	import type { WorkspaceExperience } from '$lib/types/user';
	import { beginAccessTransition, endAccessTransition } from '$lib/auth/access-transition-state';
	import { ChevronDown, Building2 } from '@lucide/svelte';
	import SimpleSelect from '$lib/components/shared/SimpleSelect.svelte';

	let { collapsed = false }: { collapsed?: boolean } = $props();

	const authState = getAuthState();
	const queryClient = useQueryClient();

	const contextsQuery = createQuery(() => ({
		queryKey: ['access-contexts'],
		enabled: authState.isAuthenticated,
		queryFn: () => auth.contexts(),
	}));

	let open = $state(false);
	let switching = $state(false);
	let switchError = $state<string | null>(null);
	const access = $derived(authState.accessEnvelope);
	const choices = $derived(contextsQuery.data ?? []);
	const canSwitch = $derived(choices.length > 1);

	async function selectContext(id: number) {
		if (id === access?.selectedContext.accessContextId || switching) return;
		switching = true;
		switchError = null;
		beginAccessTransition();
		try {
			await signalRService.disconnect();
			const result = await auth.selectContext(id);
			const landing = safeLandingForAccess(result.access);
			if (!landing) throw new Error('No authorized landing is available for that workspace.');
			await adoptAccessSession(result.accessToken, result.accessTokenExpiration, result.access);
			open = false;
			await goto(landing, { replaceState: true, invalidateAll: true });
			await signalRService.connect(CLIENT_HUB_URL);
		} catch (error) {
			await signalRService.connect(CLIENT_HUB_URL);
			switchError = error instanceof Error ? error.message : 'Could not switch workspaces.';
		} finally {
			endAccessTransition();
			switching = false;
		}
	}

	async function handleExperienceChange(experience: WorkspaceExperience) {
		if (experience === authState.activeExperience || switching || !access) return;
		const plannedLanding = safeLandingForAccess(access, experience);
		if (!plannedLanding) {
			switchError = 'That work area does not have an available page for this account.';
			return;
		}

		switching = true;
		switchError = null;
		beginAccessTransition();
		try {
			// Stop old-experience events before changing the server-side selection. The access-change
			// callback then purges queries and singleton stores before this safe navigation.
			await signalRService.disconnect();
			const nextAccess = await auth.selectExperience(experience);
			const landing = safeLandingForAccess(nextAccess);
			if (!landing) throw new Error('No authorized landing is available for that work area.');
			await adoptAccessEnvelope(nextAccess);
			open = false;
			await goto(landing, { replaceState: true, invalidateAll: true });
			await signalRService.connect(CLIENT_HUB_URL);
		} catch (error) {
			// A failed selection leaves the existing shell usable and restores realtime best-effort.
			await signalRService.connect(CLIENT_HUB_URL);
			switchError = error instanceof Error ? error.message : 'Could not switch work areas.';
			void queryClient.invalidateQueries();
		} finally {
			endAccessTransition();
			switching = false;
		}
	}

	function experienceHasLanding(experience: WorkspaceExperience): boolean {
		return access ? safeLandingForAccess(access, experience) !== null : false;
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
		<SimpleSelect
			value={authState.activeExperience ?? ''}
			options={(access?.availableExperiences ?? []).map((experience) => ({
				value: experience,
				label: ({
					Management: 'Manage rentals',
					Owner: 'Owner portal',
					Tenant: 'Resident portal',
					Vendor: 'Vendor work',
					Technician: 'Assigned work'
				} as Partial<Record<WorkspaceExperience, string>>)[experience] ?? experience,
				disabled: !experienceHasLanding(experience)
			}))}
			disabled={switching}
			onchange={(value) => void handleExperienceChange(value as WorkspaceExperience)}
			triggerClass="h-9 w-full"
			ariaLabel="Current work area"
			testid="active-experience-select"
		/>
		{#if switchError}
			<p id="active-experience-error" class="mt-1 text-xs text-destructive" role="alert">
				{switchError}
			</p>
		{/if}
	</div>
{/if}
