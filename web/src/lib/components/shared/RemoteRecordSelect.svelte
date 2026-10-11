<script lang="ts">
	import { createQuery } from '@tanstack/svelte-query';
	import { ChevronLeft, ChevronRight, ChevronsUpDown, Loader2, Search, X } from '@lucide/svelte';
	import { Button } from '$lib/components/ui/button';
	import { Input } from '$lib/components/ui/input';
	import { debounced } from '$lib/utils/debounce.svelte';

	type RemoteRecordOption = {
		id: string | number;
		label: string;
		description?: string | null;
	};

	type RemoteRecordPage = {
		items: RemoteRecordOption[];
		totalCount: number;
		skip: number;
		take: number;
	};

	type Props = {
		queryKey: string | readonly unknown[];
		label: string;
		hideLabel?: boolean;
		triggerClass?: string;
		value?: string;
		selectedLabel?: string | null;
		placeholder?: string;
		searchPlaceholder?: string;
		emptyLabel?: string;
		clearLabel?: string;
		disabled?: boolean;
		required?: boolean;
		pageSize?: number;
		testid?: string;
		loadPage: (params: { search?: string; skip: number; take: number }) => Promise<RemoteRecordPage>;
		onValueChange?: (value: string, option: RemoteRecordOption | null) => void;
	};

	let {
		queryKey,
		label,
		hideLabel = false,
		triggerClass,
		value = $bindable(''),
		selectedLabel = null,
		placeholder = 'Select a record',
		searchPlaceholder = 'Search…',
		emptyLabel = 'No matching records',
		clearLabel,
		disabled = false,
		required = false,
		pageSize = 20,
		testid = 'remote-record-select',
		loadPage,
		onValueChange
	}: Props = $props();

	let open = $state(false);
	let search = $state('');
	let resultPage = $state(1);
	let rememberedSelection = $state<{ value: string; label: string } | null>(null);
	const debouncedSearch = debounced(() => search, 300);

	$effect(() => {
		if (!value) {
			rememberedSelection = null;
			return;
		}
		if (selectedLabel && rememberedSelection?.value !== value) {
			rememberedSelection = { value, label: selectedLabel };
		}
	});

	$effect(() => {
		debouncedSearch.value;
		resultPage = 1;
	});

	const recordsQuery = createQuery(() => ({
		queryKey: [
			'remote-record-select',
			...(Array.isArray(queryKey) ? queryKey : [queryKey]),
			debouncedSearch.value,
			resultPage,
			pageSize
		],
		queryFn: () =>
			loadPage({
				search: debouncedSearch.value || undefined,
				skip: (resultPage - 1) * pageSize,
				take: pageSize
			}),
		enabled: open && !disabled
	}));

	const totalPages = $derived(Math.max(1, Math.ceil((recordsQuery.data?.totalCount ?? 0) / pageSize)));
	const currentLabel = $derived(
		value
			? rememberedSelection?.value === value
				? rememberedSelection.label
				: selectedLabel || placeholder
			: clearLabel || placeholder
	);

	function choose(option: RemoteRecordOption) {
		const next = String(option.id);
		value = next;
		rememberedSelection = { value: next, label: option.label };
		onValueChange?.(next, option);
		close();
	}

	function clear() {
		value = '';
		rememberedSelection = null;
		onValueChange?.('', null);
		close();
	}

	function close() {
		open = false;
		search = '';
		resultPage = 1;
	}
</script>

<div class="relative grid gap-1.5" data-testid={testid}>
	<span class={hideLabel ? 'sr-only' : 'text-sm font-medium'}>{label}{#if required}<span aria-hidden="true"> *</span>{/if}</span>
	<div class="flex gap-1">
		<Button
			type="button"
			variant="outline"
			class={['min-w-0 flex-1 justify-between font-normal', triggerClass]}
			aria-haspopup="listbox"
			aria-expanded={open}
			aria-required={required}
			{disabled}
			onclick={() => (open = !open)}
			data-testid="{testid}-trigger"
		>
			<span class="truncate">{currentLabel}</span>
			<ChevronsUpDown class="ml-2 h-4 w-4 shrink-0 opacity-60" />
		</Button>
		{#if value && clearLabel}
			<Button
				type="button"
				variant="ghost"
				size="icon"
				aria-label={clearLabel}
				onclick={clear}
				{disabled}
				data-testid="{testid}-clear"
			>
				<X class="h-4 w-4" />
			</Button>
		{/if}
	</div>

	{#if open}
		<div
			class="absolute top-full z-50 mt-1 w-full min-w-72 rounded-md border bg-popover p-2 text-popover-foreground shadow-md"
			data-testid="{testid}-popover"
		>
			<div class="relative">
				<Search class="pointer-events-none absolute left-2.5 top-2.5 h-4 w-4 text-muted-foreground" />
				<Input
					bind:value={search}
					class="pl-8"
					placeholder={searchPlaceholder}
					aria-label={searchPlaceholder}
					data-testid="{testid}-search"
				/>
			</div>

			<div class="mt-2 max-h-64 overflow-y-auto" role="listbox" aria-label={label}>
				{#if recordsQuery.isLoading}
					<div class="flex items-center justify-center gap-2 py-6 text-sm text-muted-foreground">
						<Loader2 class="h-4 w-4 animate-spin" /> Loading…
					</div>
				{:else if recordsQuery.isError}
					<div class="space-y-2 p-3 text-center" role="alert" data-testid="{testid}-error">
						<p class="text-sm text-destructive">Could not load records.</p>
						<Button type="button" variant="outline" size="sm" onclick={() => recordsQuery.refetch()}>
							Try again
						</Button>
					</div>
				{:else if (recordsQuery.data?.items.length ?? 0) === 0}
					<p class="p-4 text-center text-sm text-muted-foreground" data-testid="{testid}-empty">
						{emptyLabel}
					</p>
				{:else}
					{#if clearLabel}
						<button
							type="button"
							class="w-full rounded-sm px-3 py-2 text-left text-sm hover:bg-accent focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
							role="option"
							aria-selected={!value}
							onclick={clear}
						>
							{clearLabel}
						</button>
					{/if}
					{#each recordsQuery.data?.items ?? [] as option (option.id)}
						<button
							type="button"
							class="w-full rounded-sm px-3 py-2 text-left hover:bg-accent focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
							role="option"
							aria-selected={String(option.id) === value}
							onclick={() => choose(option)}
							data-testid="{testid}-option-{option.id}"
						>
							<span class="block truncate text-sm font-medium">{option.label}</span>
							{#if option.description}
								<span class="block truncate text-xs text-muted-foreground">{option.description}</span>
							{/if}
						</button>
					{/each}
				{/if}
			</div>

			{#if !recordsQuery.isError && (recordsQuery.data?.totalCount ?? 0) > pageSize}
				<div class="mt-2 flex items-center justify-between border-t pt-2" data-testid="{testid}-paging">
					<Button
						type="button"
						variant="ghost"
						size="sm"
						disabled={resultPage <= 1 || recordsQuery.isFetching}
						onclick={() => (resultPage = Math.max(1, resultPage - 1))}
						aria-label="Previous results page"
					>
						<ChevronLeft class="h-4 w-4" /> Previous
					</Button>
					<span class="text-xs text-muted-foreground">Page {resultPage} of {totalPages}</span>
					<Button
						type="button"
						variant="ghost"
						size="sm"
						disabled={resultPage >= totalPages || recordsQuery.isFetching}
						onclick={() => (resultPage = Math.min(totalPages, resultPage + 1))}
						aria-label="Next results page"
					>
						Next <ChevronRight class="h-4 w-4" />
					</Button>
				</div>
			{/if}
		</div>
	{/if}
</div>
