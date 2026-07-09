<script lang="ts">
	import { createMutation, createQuery, useQueryClient } from '@tanstack/svelte-query';
	import {
		capitalAssets,
		type CapitalAsset,
		type CreateCapitalAssetRequest,
		type DepreciationConvention,
		type DepreciationMethod
	} from '$lib/api/endpoints/capital-assets';
	import { capitalAssetSchema, parseForm } from '$lib/schemas';
	import { getCurrentPortfolioId } from '$lib/stores/portfolio.svelte';
	import { showSuccess, showError, apiErrorMessage } from '$lib/utils/toast';
	import { DataGrid } from '$lib/components/data-grid';
	import type { ColumnDef } from '$lib/components/data-grid/types';
	import ConfirmDialog from '$lib/components/shared/ConfirmDialog.svelte';
	import InlineField from '$lib/components/shared/InlineField.svelte';
	import RangeDatePicker from '$lib/components/shared/RangeDatePicker.svelte';
	import * as Dialog from '$lib/components/ui/dialog';
	import { Button } from '$lib/components/ui/button';
	import { Plus, Pencil, Trash2 } from '@lucide/svelte';

	let { propertyId }: { propertyId: number } = $props();

	const queryClient = useQueryClient();
	const portfolioId = $derived(getCurrentPortfolioId());
	const PAGE_SIZE = 10;

	let assetPage = $state(1);
	let assetSort = $state('-inServiceDate');
	let assetFrom = $state('');
	let assetTo = $state('');

	let filterResetPrimed = false;
	$effect(() => {
		propertyId;
		assetFrom;
		assetTo;
		if (!filterResetPrimed) {
			filterResetPrimed = true;
			return;
		}
		assetPage = 1;
	});

	const query = createQuery(() => ({
		queryKey: ['capital-assets', propertyId, 'page', assetPage, assetSort, assetFrom, assetTo, PAGE_SIZE],
		queryFn: () =>
			capitalAssets.listPage({
				propertyId,
				skip: (assetPage - 1) * PAGE_SIZE,
				take: PAGE_SIZE,
				sort: assetSort || undefined,
				from: assetFrom || undefined,
				to: assetTo || undefined
			}),
		enabled: !Number.isNaN(propertyId) && propertyId > 0
	}));

	const list = $derived(query.data?.items ?? []);
	const totalCount = $derived(query.data?.totalCount ?? 0);

	const methodOptions = [
		{ value: 'StraightLine', label: 'Straight-line' },
		{ value: 'Macrs', label: 'MACRS' }
	];
	const conventionOptions = [
		{ value: 'MidMonth', label: 'Mid-month' },
		{ value: 'HalfYear', label: 'Half-year' }
	];
	const recoveryOptions = [
		{ value: '27.5', label: '27.5 years' },
		{ value: '15', label: '15 years' },
		{ value: '7', label: '7 years' },
		{ value: '5', label: '5 years' }
	];

	const emptyForm = {
		description: '',
		costBasis: '',
		inServiceDate: '',
		method: 'StraightLine' as DepreciationMethod,
		recoveryYears: '27.5',
		convention: 'MidMonth' as DepreciationConvention,
		accumulatedDepreciation: ''
	};

	let showForm = $state(false);
	let editingId = $state<number | null>(null);
	let form = $state({ ...emptyForm });
	let formErrors = $state<Record<string, string>>({});
	let deleteTarget = $state<CapitalAsset | null>(null);

	function openAdd() {
		editingId = null;
		form = { ...emptyForm };
		formErrors = {};
		showForm = true;
	}

	function openEdit(asset: CapitalAsset) {
		editingId = asset.id;
		form = {
			description: asset.description,
			costBasis: String(asset.costBasis),
			inServiceDate: asset.inServiceDate.slice(0, 10),
			method: asset.method,
			recoveryYears: String(asset.recoveryYears),
			convention: asset.convention === 'MidQuarter' ? 'HalfYear' : asset.convention,
			accumulatedDepreciation: asset.accumulatedDepreciation ? String(asset.accumulatedDepreciation) : ''
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
		queryClient.invalidateQueries({ queryKey: ['capital-assets', propertyId] });
		queryClient.invalidateQueries({ queryKey: ['accounting-summary', portfolioId] });
		queryClient.invalidateQueries({ queryKey: ['reports', portfolioId] });
	}

	const createMut = createMutation(() => ({
		mutationFn: (data: Omit<CreateCapitalAssetRequest, 'propertyId'>) =>
			capitalAssets.create({ ...data, propertyId }),
		onSuccess: () => {
			showSuccess('Capital asset added.');
			closeForm();
			invalidate();
		},
		onError: (err) => showError(apiErrorMessage(err))
	}));

	const updateMut = createMutation(() => ({
		mutationFn: ({ id, data }: { id: number; data: Record<string, unknown> }) =>
			capitalAssets.update(id, data),
		onSuccess: () => {
			showSuccess('Capital asset updated.');
			closeForm();
			invalidate();
		},
		onError: (err) => showError(apiErrorMessage(err))
	}));

	const deleteMut = createMutation(() => ({
		mutationFn: (id: number) => capitalAssets.remove(id),
		onSuccess: () => {
			showSuccess('Capital asset removed.');
			deleteTarget = null;
			invalidate();
		},
		onError: (err) => showError(apiErrorMessage(err))
	}));

	function submit() {
		const result = parseForm(capitalAssetSchema, form);
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

	const columns: ColumnDef<CapitalAsset>[] = [
		{ key: 'description', title: 'Description', sortable: true, mobileRole: 'title' },
		{ key: 'costBasis', title: 'Basis', format: 'currency', sortable: true, mobileRole: 'metric' },
		{ key: 'inServiceDate', title: 'In service', format: 'date', sortable: true, mobileRole: 'meta' },
		{ key: 'method', title: 'Method', sortable: true, mobileRole: 'subtitle', accessor: (a) => labelMethod(a.method) },
		{ key: 'recoveryYears', title: 'Life', sortable: true, mobileRole: 'meta', accessor: (a) => `${a.recoveryYears} yrs` },
		{ key: 'annualDepreciation', title: 'This year', format: 'currency', mobileRole: 'metric' },
		{ key: 'accumulatedDepreciation', title: 'Accumulated', format: 'currency', sortable: true, mobileRole: 'meta' },
		{ key: 'actions', title: '', align: 'right', width: '5rem', mobileRole: 'hidden', cell: actionsCell }
	];

	function labelMethod(method: DepreciationMethod) {
		return method === 'Macrs' ? 'MACRS' : 'Straight-line';
	}
</script>

{#snippet actionsCell(asset: CapitalAsset)}
	<div class="flex items-center justify-end gap-1">
		<Button variant="ghost" size="sm" class="h-7 w-7 p-0" onclick={(e) => { e.stopPropagation(); openEdit(asset); }} aria-label="Edit capital asset">
			<Pencil class="h-4 w-4" />
		</Button>
		<Button variant="ghost" size="sm" class="h-7 w-7 p-0 text-destructive" onclick={(e) => { e.stopPropagation(); deleteTarget = asset; }} aria-label="Delete capital asset">
			<Trash2 class="h-4 w-4" />
		</Button>
	</div>
{/snippet}

<div class="mb-6" data-testid="property-detail-capital-assets">
	<h2 class="mb-1 text-lg font-semibold">Capital assets</h2>
	<DataGrid
		data={list}
		{columns}
		loading={query.isLoading || query.isFetching}
		emptyMessage="No capital assets yet."
		getRowKey={(asset) => asset.id}
		onRowClick={(asset) => openEdit(asset)}
		pageSize={PAGE_SIZE}
		page={assetPage}
		totalCount={totalCount}
		serverSide
		sort={assetSort}
		onPageChange={(page) => (assetPage = page)}
		onSortChange={(sort) => { assetSort = sort ?? ''; assetPage = 1; }}
		data-testid="property-capital-assets-grid"
	>
		{#snippet toolbar()}
			<div class="flex flex-1"></div>
			<RangeDatePicker
				bind:start={assetFrom}
				bind:end={assetTo}
				presets
				placeholder="In-service dates"
				align="end"
				testid="property-capital-assets-date-range"
			/>
			<Button class="gap-2 shrink-0" onclick={openAdd} data-testid="capital-asset-add-button">
				<Plus class="h-4 w-4" />
				Add Capital Asset
			</Button>
		{/snippet}
	</DataGrid>
</div>

<Dialog.Root open={showForm} onOpenChange={(v) => { if (!v) closeForm(); }}>
	<Dialog.Content class="max-w-md">
		<Dialog.Header>
			<Dialog.Title>{editingId == null ? 'Add Capital Asset' : 'Edit Capital Asset'}</Dialog.Title>
		</Dialog.Header>
		<div class="grid gap-3" data-testid="capital-asset-form">
			<InlineField label="Description" bind:value={form.description} editing type="text" error={formErrors.description} testid="capital-asset-description" />
			<div class="grid grid-cols-2 gap-3">
				<InlineField label="Cost basis" bind:value={form.costBasis} editing type="number" error={formErrors.costBasis} testid="capital-asset-cost-basis" />
				<InlineField label="In-service date" bind:value={form.inServiceDate} editing type="date" error={formErrors.inServiceDate} testid="capital-asset-in-service-date" />
			</div>
			<div class="grid grid-cols-3 gap-3">
				<InlineField label="Method" bind:value={form.method} editing type="select" options={methodOptions} error={formErrors.method} testid="capital-asset-method" />
				<InlineField label="Life" bind:value={form.recoveryYears} editing type="select" options={recoveryOptions} error={formErrors.recoveryYears} testid="capital-asset-recovery-years" />
				<InlineField label="Convention" bind:value={form.convention} editing type="select" options={conventionOptions} error={formErrors.convention} testid="capital-asset-convention" />
			</div>
			<InlineField label="Accumulated depreciation" bind:value={form.accumulatedDepreciation} editing type="number" error={formErrors.accumulatedDepreciation} testid="capital-asset-accumulated-depreciation" />
		</div>
		<div class="mt-4 flex justify-end gap-2">
			<Button variant="outline" onclick={closeForm}>Cancel</Button>
			<Button onclick={submit} disabled={createMut.isPending || updateMut.isPending} data-testid="capital-asset-save-button">
				{(createMut.isPending || updateMut.isPending) ? 'Saving...' : editingId == null ? 'Add' : 'Save'}
			</Button>
		</div>
	</Dialog.Content>
</Dialog.Root>

<ConfirmDialog
	open={deleteTarget !== null}
	title="Remove capital asset"
	message={deleteTarget ? `Remove "${deleteTarget.description}"?` : ''}
	busy={deleteMut.isPending}
	testid="capital-asset-delete-confirm"
	onconfirm={() => deleteTarget && deleteMut.mutate(deleteTarget.id)}
	oncancel={() => (deleteTarget = null)}
/>
