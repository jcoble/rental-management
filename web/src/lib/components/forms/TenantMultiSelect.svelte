<script lang="ts">
	import { Checkbox } from '$lib/components/ui/checkbox';
	import type { Tenant } from '$lib/types';

	let {
		label = 'Tenants',
		tenants = [],
		selectedIds = $bindable([]),
		error = '',
		testid = 'tenant-multi-select',
		disabled = false,
	}: {
		label?: string;
		tenants?: Tenant[];
		selectedIds?: string[];
		error?: string;
		testid?: string;
		disabled?: boolean;
	} = $props();

	const selectedSet = $derived(new Set(selectedIds));

	function tenantName(tenant: Tenant) {
		return tenant.fullName || `${tenant.firstName} ${tenant.lastName}`.trim() || `Tenant #${tenant.id}`;
	}

	function setChecked(id: string, checked: boolean) {
		if (checked) {
			if (!selectedIds.includes(id)) selectedIds = [...selectedIds, id];
			return;
		}
		selectedIds = selectedIds.filter((selectedId) => selectedId !== id);
	}
</script>

<div data-testid={testid}>
	<span class="mb-1 block text-xs font-medium text-muted-foreground">{label}</span>
	<div class="max-h-56 overflow-y-auto rounded-lg border bg-background">
		{#if tenants.length === 0}
			<div class="px-3 py-3 text-sm text-muted-foreground" data-testid={`${testid}-empty`}>
				No tenants yet
			</div>
		{:else}
			{#each tenants as tenant, index}
				{@const id = String(tenant.id)}
				<label
					class="m3-state-layer flex cursor-pointer items-center gap-3 border-b px-3 py-2.5 text-sm last:border-b-0"
					data-testid={`${testid}-option-${tenant.id}`}
				>
					<Checkbox
						checked={selectedSet.has(id)}
						onCheckedChange={(value) => setChecked(id, value === true)}
						{disabled}
						data-testid={`${testid}-checkbox-${tenant.id}`}
					/>
					<span class="min-w-0 flex-1">
						<span class="block truncate font-medium">{tenantName(tenant)}</span>
						{#if tenant.email || tenant.phone}
							<span class="block truncate text-xs text-muted-foreground">
								{tenant.email || tenant.phone}
							</span>
						{/if}
					</span>
					{#if selectedIds[0] === id}
						<span class="rounded-full bg-primary/10 px-2 py-0.5 text-[11px] font-medium text-primary">
							Primary
						</span>
					{:else if index === 0 && selectedIds.length === 0}
						<span class="text-[11px] text-muted-foreground">Tap to add</span>
					{/if}
				</label>
			{/each}
		{/if}
	</div>
	{#if error}
		<p class="mt-1 text-xs text-destructive" data-testid={`${testid}-error`}>{error}</p>
	{/if}
</div>
