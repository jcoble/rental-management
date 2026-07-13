<script lang="ts">
	import { createMutation, createQuery, useQueryClient } from '@tanstack/svelte-query';
	import {
		propertyDispositions,
		type CreatePropertyDispositionRequest,
		type PropertyDisposition
	} from '$lib/api/endpoints/property-dispositions';
	import { propertyDispositionSchema, parseForm } from '$lib/schemas';
	import { getCurrentPortfolioId } from '$lib/stores/portfolio.svelte';
	import { showSuccess, showError, apiErrorMessage } from '$lib/utils/toast';
	import { DataGrid } from '$lib/components/data-grid';
	import type { ColumnDef } from '$lib/components/data-grid/types';
	import ConfirmDialog from '$lib/components/shared/ConfirmDialog.svelte';
	import InlineField from '$lib/components/shared/InlineField.svelte';
	import * as Dialog from '$lib/components/ui/dialog';
	import { Button } from '$lib/components/ui/button';
	import { Plus, Pencil, Trash2 } from '@lucide/svelte';

	let { propertyId }: { propertyId: number } = $props();

	const queryClient = useQueryClient();
	const portfolioId = $derived(getCurrentPortfolioId());
	const PAGE_SIZE = 5;

	let page = $state(1);
	let sort = $state('-closedOnDate');
	const query = createQuery(() => ({
		queryKey: ['property-dispositions', propertyId, 'page', page, sort, PAGE_SIZE],
		queryFn: () =>
			propertyDispositions.listPage({
				propertyId,
				skip: (page - 1) * PAGE_SIZE,
				take: PAGE_SIZE,
				sort: sort || undefined
			}),
		enabled: !Number.isNaN(propertyId) && propertyId > 0
	}));

	const list = $derived(query.data?.items ?? []);
	const totalCount = $derived(query.data?.totalCount ?? 0);

	const emptyForm = {
		closedOnDate: '',
		salePrice: '',
		sellingCosts: '',
		buyerName: '',
		memo: ''
	};

	let showForm = $state(false);
	let editingId = $state<number | null>(null);
	let form = $state({ ...emptyForm });
	let formErrors = $state<Record<string, string>>({});
	let deleteTarget = $state<PropertyDisposition | null>(null);

	function openAdd() {
		editingId = null;
		form = { ...emptyForm };
		formErrors = {};
		showForm = true;
	}

	function openEdit(disposition: PropertyDisposition) {
		editingId = disposition.id;
		form = {
			closedOnDate: disposition.closedOnDate.slice(0, 10),
			salePrice: String(disposition.salePrice),
			sellingCosts: String(disposition.sellingCosts),
			buyerName: disposition.buyerName ?? '',
			memo: disposition.memo ?? ''
		};
		formErrors = {};
		showForm = true;
	}

	function closeForm() {
		showForm = false;
		editingId = null;
		formErrors = {};
	}

	function invalidate() {
		queryClient.invalidateQueries({ queryKey: ['property-dispositions', propertyId] });
		queryClient.invalidateQueries({ queryKey: ['property', propertyId] });
		queryClient.invalidateQueries({ queryKey: ['properties'] });
		queryClient.invalidateQueries({ queryKey: ['lease-managements'] });
		queryClient.invalidateQueries({ queryKey: ['units'] });
		queryClient.invalidateQueries({ queryKey: ['capital-assets', propertyId] });
		queryClient.invalidateQueries({ queryKey: ['accounting-summary', portfolioId] });
		queryClient.invalidateQueries({ queryKey: ['reports', portfolioId] });
	}

	const createMut = createMutation(() => ({
		mutationFn: (data: Omit<CreatePropertyDispositionRequest, 'propertyId'>) =>
			propertyDispositions.create({ ...data, propertyId }),
		onSuccess: () => {
			showSuccess('Property sale recorded.');
			closeForm();
			invalidate();
		},
		onError: (err) => showError(apiErrorMessage(err))
	}));

	const updateMut = createMutation(() => ({
		mutationFn: ({ id, data }: { id: number; data: Record<string, unknown> }) =>
			propertyDispositions.update(id, data),
		onSuccess: () => {
			showSuccess('Property sale updated.');
			closeForm();
			invalidate();
		},
		onError: (err) => showError(apiErrorMessage(err))
	}));

	const deleteMut = createMutation(() => ({
		mutationFn: (id: number) => propertyDispositions.remove(id),
		onSuccess: () => {
			showSuccess('Property sale removed.');
			deleteTarget = null;
			invalidate();
		},
		onError: (err) => showError(apiErrorMessage(err))
	}));

	function submit() {
		const result = parseForm(propertyDispositionSchema, form);
		if (result.errors) {
			formErrors = result.errors;
			return;
		}
		formErrors = {};
		if (editingId != null) {
			updateMut.mutate({ id: editingId, data: result.data });
		} else {
			createMut.mutate(result.data);
		}
	}

	const columns: ColumnDef<PropertyDisposition>[] = [
		{ key: 'closedOnDate', title: 'Closed', format: 'date', sortable: true, mobileRole: 'meta' },
		{ key: 'salePrice', title: 'Sale price', format: 'currency', sortable: true, mobileRole: 'metric' },
		{ key: 'sellingCosts', title: 'Costs', format: 'currency', sortable: true, mobileRole: 'meta' },
		{ key: 'gainLoss', title: 'Gain / loss', format: 'currency', mobileRole: 'metric' },
		{ key: 'saleYearDepreciation', title: 'Sale-year dep.', format: 'currency', mobileRole: 'meta' },
		{ key: 'unrecapturedSection1250Gain', title: '§1250', format: 'currency', mobileRole: 'meta' },
		{ key: 'buyerName', title: 'Buyer', sortable: true, mobileRole: 'subtitle', accessor: (d) => d.buyerName ?? '-' },
		{ key: 'actions', title: '', align: 'right', width: '5rem', mobileRole: 'hidden', cell: actionsCell }
	];

	function fmtMoney(value: number): string {
		return new Intl.NumberFormat('en-US', { style: 'currency', currency: 'USD' }).format(value || 0);
	}
</script>

{#snippet actionsCell(disposition: PropertyDisposition)}
	<div class="flex items-center justify-end gap-1">
		<Button variant="ghost" size="sm" class="h-7 w-7 p-0" onclick={(e) => { e.stopPropagation(); openEdit(disposition); }} aria-label="Edit property sale">
			<Pencil class="h-4 w-4" />
		</Button>
		<Button variant="ghost" size="sm" class="h-7 w-7 p-0 text-destructive" onclick={(e) => { e.stopPropagation(); deleteTarget = disposition; }} aria-label="Delete property sale">
			<Trash2 class="h-4 w-4" />
		</Button>
	</div>
{/snippet}

<div class="mb-6" data-testid="property-detail-dispositions">
	<h2 class="mb-1 text-lg font-semibold">Property sale / disposition</h2>
	{#if list.length > 0}
		<div class="mb-3 grid gap-3 md:grid-cols-3">
			{#each list.slice(0, 1) as disposition}
				<div class="rounded-md border border-border bg-muted/20 px-3 py-2">
					<p class="text-xs font-medium uppercase tracking-wide text-muted-foreground">Net proceeds</p>
					<p class="mt-1 font-mono text-lg font-semibold tabular-nums">{fmtMoney(disposition.netSaleProceeds)}</p>
				</div>
				<div class="rounded-md border border-border bg-muted/20 px-3 py-2">
					<p class="text-xs font-medium uppercase tracking-wide text-muted-foreground">Adjusted basis</p>
					<p class="mt-1 font-mono text-lg font-semibold tabular-nums">{fmtMoney(disposition.adjustedBasis)}</p>
				</div>
				<div class="rounded-md border border-border bg-muted/20 px-3 py-2">
					<p class="text-xs font-medium uppercase tracking-wide text-muted-foreground">Estimated gain / loss</p>
					<p class="mt-1 font-mono text-lg font-semibold tabular-nums">{fmtMoney(disposition.gainLoss)}</p>
				</div>
			{/each}
		</div>
	{/if}
	<DataGrid
		data={list}
		{columns}
		loading={query.isLoading || query.isFetching}
		emptyMessage="No property sale recorded yet."
		getRowKey={(disposition) => disposition.id}
		onRowClick={(disposition) => openEdit(disposition)}
		pageSize={PAGE_SIZE}
		{page}
		totalCount={totalCount}
		serverSide
		{sort}
		onPageChange={(next) => (page = next)}
		onSortChange={(next) => { sort = next ?? ''; page = 1; }}
		data-testid="property-dispositions-grid"
	>
		{#snippet toolbar()}
			<div class="flex flex-1"></div>
			<Button class="gap-2 shrink-0" onclick={openAdd} data-testid="property-disposition-add-button">
				<Plus class="h-4 w-4" />
				Record Sale
			</Button>
		{/snippet}
	</DataGrid>
</div>

<Dialog.Root open={showForm} onOpenChange={(v) => { if (!v) closeForm(); }}>
	<Dialog.Content class="max-w-md">
		<Dialog.Header>
			<Dialog.Title>{editingId == null ? 'Record Property Sale' : 'Edit Property Sale'}</Dialog.Title>
		</Dialog.Header>
		<div class="grid gap-3" data-testid="property-disposition-form">
			<InlineField label="Close date" bind:value={form.closedOnDate} editing type="date" error={formErrors.closedOnDate} testid="property-disposition-closed-on" />
			<div class="grid grid-cols-2 gap-3">
				<InlineField label="Sale price" bind:value={form.salePrice} editing type="number" error={formErrors.salePrice} testid="property-disposition-sale-price" />
				<InlineField label="Selling costs" bind:value={form.sellingCosts} editing type="number" error={formErrors.sellingCosts} testid="property-disposition-selling-costs" />
			</div>
			<InlineField label="Buyer" bind:value={form.buyerName} editing type="text" error={formErrors.buyerName} testid="property-disposition-buyer" />
			<InlineField label="Memo" bind:value={form.memo} editing type="textarea" error={formErrors.memo} testid="property-disposition-memo" />
		</div>
		<div class="mt-4 flex justify-end gap-2">
			<Button variant="outline" onclick={closeForm}>Cancel</Button>
			<Button onclick={submit} disabled={createMut.isPending || updateMut.isPending} data-testid="property-disposition-save-button">
				{(createMut.isPending || updateMut.isPending) ? 'Saving...' : editingId == null ? 'Record' : 'Save'}
			</Button>
		</div>
	</Dialog.Content>
</Dialog.Root>

<ConfirmDialog
	open={deleteTarget !== null}
	title="Remove property sale"
	message={deleteTarget ? `Remove sale record for ${deleteTarget.propertyName ?? 'this property'}?` : ''}
	busy={deleteMut.isPending}
	testid="property-disposition-delete-confirm"
	onconfirm={() => deleteTarget && deleteMut.mutate(deleteTarget.id)}
	oncancel={() => (deleteTarget = null)}
/>
