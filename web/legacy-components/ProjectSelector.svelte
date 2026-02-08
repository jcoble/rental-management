<script lang="ts">
	import { createQuery, useQueryClient } from '@tanstack/svelte-query';
	import { projects } from '$lib/api/endpoints/projects';
	import { getCurrentProjectId, setCurrentProjectId } from '$lib/stores/project.svelte';
	import type { Project } from '$lib/types';
	import { ChevronDown, Plus, FolderOpen } from '@lucide/svelte';
	import CreateProjectDialog from './CreateProjectDialog.svelte';

	let { collapsed = false }: { collapsed?: boolean } = $props();

	const queryClient = useQueryClient();

	const projectsQuery = createQuery(() => ({
		queryKey: ['projects'],
		queryFn: () => projects.list(),
	}));

	let open = $state(false);
	let showCreateDialog = $state(false);

	let currentProject = $derived(
		projectsQuery.data?.find((p: Project) => p.id === getCurrentProjectId())
	);

	// If stored project doesn't exist in list, reset to first available
	$effect(() => {
		const list = projectsQuery.data;
		if (list && list.length > 0 && !list.find((p: Project) => p.id === getCurrentProjectId())) {
			setCurrentProjectId(list[0].id);
		}
	});

	function selectProject(id: number) {
		setCurrentProjectId(id);
		open = false;
		queryClient.invalidateQueries();
	}

	function handleCreated(project: Project) {
		showCreateDialog = false;
		queryClient.invalidateQueries({ queryKey: ['projects'] });
		selectProject(project.id);
	}

	function handleClickOutside(e: MouseEvent) {
		const target = e.target as HTMLElement;
		if (!target.closest('.project-selector')) {
			open = false;
		}
	}
</script>

<svelte:window onclick={handleClickOutside} />

<div class="project-selector relative px-2 pb-2">
	{#if collapsed}
		<button
			onclick={() => (open = !open)}
			class="flex w-full items-center justify-center rounded-md px-3 py-2 text-text-secondary transition-colors hover:bg-surface-hover hover:text-text-primary"
			title={currentProject?.name || 'Select project'}
		>
			<FolderOpen class="h-4 w-4 shrink-0" />
		</button>
	{:else}
		<button
			onclick={() => (open = !open)}
			class="flex w-full items-center gap-2 rounded-md border border-border bg-bg px-3 py-1.5 text-sm text-text-primary transition-colors hover:border-border-hover"
		>
			<FolderOpen class="h-3.5 w-3.5 shrink-0 text-text-tertiary" />
			<span class="flex-1 truncate text-left">{currentProject?.name || 'Select project'}</span>
			<ChevronDown class="h-3.5 w-3.5 shrink-0 text-text-tertiary" />
		</button>
	{/if}

	{#if open}
		<div class="absolute left-2 right-2 top-full z-50 mt-1 rounded-md border border-border bg-surface shadow-lg">
			{#if projectsQuery.data}
				<div class="max-h-48 overflow-y-auto py-1">
					{#each projectsQuery.data as project}
						<button
							onclick={() => selectProject(project.id)}
							class="flex w-full items-center gap-2 px-3 py-1.5 text-sm transition-colors hover:bg-surface-hover {project.id === getCurrentProjectId() ? 'text-accent' : 'text-text-primary'}"
						>
							<span class="truncate">{project.name}</span>
							{#if project.id === getCurrentProjectId()}
								<span class="ml-auto text-[10px] text-accent">current</span>
							{/if}
						</button>
					{/each}
				</div>
			{/if}
			<div class="border-t border-border py-1">
				<button
					onclick={() => { open = false; showCreateDialog = true; }}
					class="flex w-full items-center gap-2 px-3 py-1.5 text-sm text-text-secondary transition-colors hover:bg-surface-hover hover:text-text-primary"
				>
					<Plus class="h-3.5 w-3.5" />
					New Project
				</button>
			</div>
		</div>
	{/if}
</div>

{#if showCreateDialog}
	<CreateProjectDialog
		onCreated={handleCreated}
		onClose={() => (showCreateDialog = false)}
	/>
{/if}
