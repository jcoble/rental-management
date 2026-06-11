<script lang="ts">
	import { createMutation } from '@tanstack/svelte-query';
	import {
		importCsv,
		downloadTemplate,
		type ImportEntityType,
		type ImportResult
	} from '$lib/api/endpoints/import';
	import * as Card from '$lib/components/ui/card';
	import * as Table from '$lib/components/ui/table';
	import { Button } from '$lib/components/ui/button';
	import StatusBadge from '$lib/components/shared/StatusBadge.svelte';
	import { showError, apiErrorMessage } from '$lib/utils/toast';
	import {
		FileSpreadsheet,
		Download,
		Upload,
		CheckCircle2,
		AlertCircle,
		ArrowRight
	} from '@lucide/svelte';

	// ---------------------------------------------------------------------------
	// Entity type options
	// ---------------------------------------------------------------------------
	type EntityOption = {
		value: ImportEntityType;
		label: string;
		columns: string;
		/** Where finished records live, for the "view them" link. */
		listHref: string;
		listLabel: string;
	};

	const ENTITY_OPTIONS: EntityOption[] = [
		{
			value: 'tenant',
			label: 'Tenants',
			columns: 'first name, last name, email, phone',
			listHref: '/tenants',
			listLabel: 'tenants'
		},
		{
			value: 'property',
			label: 'Properties',
			columns: 'name, address, city, state, ZIP, type',
			listHref: '/properties',
			listLabel: 'properties'
		},
		{
			value: 'unit',
			label: 'Units',
			columns: 'property name, unit number, beds, baths, market rent',
			listHref: '/properties',
			listLabel: 'properties'
		}
	];

	let entityType = $state<ImportEntityType>('tenant');
	const selectedOption = $derived(
		ENTITY_OPTIONS.find((o) => o.value === entityType) ?? ENTITY_OPTIONS[0]
	);

	// ---------------------------------------------------------------------------
	// File + result state
	// ---------------------------------------------------------------------------
	let selectedFile = $state<File | null>(null);
	let isDragging = $state(false);
	let fileInputEl = $state<HTMLInputElement | null>(null);

	// Result of the most recent dry-run preview and of a committed import.
	let preview = $state<ImportResult | null>(null);
	let imported = $state<ImportResult | null>(null);
	let downloadingTemplate = $state(false);

	const previewMutation = createMutation(() => ({
		mutationFn: (file: File) => importCsv(entityType, file, true),
		onSuccess: (res) => {
			preview = res;
		},
		onError: (err) => {
			preview = null;
			showError(apiErrorMessage(err, "We couldn't read that file. Make sure it's a CSV."));
		}
	}));

	const importMutation = createMutation(() => ({
		mutationFn: (file: File) => importCsv(entityType, file, false),
		onSuccess: (res) => {
			imported = res;
		},
		onError: (err) => {
			showError(apiErrorMessage(err, 'The import could not be completed. Please try again.'));
		}
	}));

	const busy = $derived(previewMutation.isPending || importMutation.isPending);

	// ---------------------------------------------------------------------------
	// Actions
	// ---------------------------------------------------------------------------
	function resetResults() {
		preview = null;
		imported = null;
	}

	function looksLikeCsv(file: File): boolean {
		if (file.type === 'text/csv' || file.type === 'application/vnd.ms-excel') return true;
		return /\.csv$/i.test(file.name);
	}

	function handleFile(file: File) {
		if (!looksLikeCsv(file)) {
			showError('Please choose a CSV file (a spreadsheet saved as ".csv").');
			return;
		}
		selectedFile = file;
		resetResults();
		// Automatically run a dry-run preview the moment a file is picked.
		previewMutation.mutate(file);
	}

	function onInputChange(e: Event) {
		const file = (e.target as HTMLInputElement).files?.[0];
		if (file) handleFile(file);
	}

	function onDrop(e: DragEvent) {
		e.preventDefault();
		isDragging = false;
		const file = e.dataTransfer?.files?.[0];
		if (file) handleFile(file);
	}

	function clearFile() {
		selectedFile = null;
		resetResults();
		if (fileInputEl) fileInputEl.value = '';
	}

	function runImport() {
		if (selectedFile && (preview?.validRows ?? 0) > 0) {
			importMutation.mutate(selectedFile);
		}
	}

	function onEntityChange(value: ImportEntityType) {
		if (value === entityType) return;
		entityType = value;
		// A previewed file no longer matches the new entity type — start fresh.
		clearFile();
	}

	async function onDownloadTemplate() {
		downloadingTemplate = true;
		try {
			await downloadTemplate(entityType);
		} catch (err) {
			showError(apiErrorMessage(err, "We couldn't download the template. Please try again."));
		} finally {
			downloadingTemplate = false;
		}
	}

	// Status map for the per-row valid/invalid badge.
	const rowStatusMap: Record<string, { label?: string; class: string }> = {
		Good: {
			label: 'Looks good',
			class: 'm3-tone-chip border m3-tone--success'
		},
		Problem: {
			label: 'Needs a fix',
			class: 'm3-tone-chip border m3-tone--error'
		}
	};
</script>

<svelte:head>
	<title>Import from a spreadsheet - Rental Command</title>
</svelte:head>

<div class="box-border h-full overflow-y-auto p-6 pb-20" data-testid="import-page">
	<div class="mx-auto max-w-3xl">
		<!-- Header -->
		<div class="mb-6 flex items-start gap-3">
			<div class="flex h-10 w-10 shrink-0 items-center justify-center rounded-lg bg-primary/10 text-primary">
				<FileSpreadsheet class="h-5 w-5" />
			</div>
			<div>
				<h1 class="text-2xl font-bold" data-testid="import-title">Import from a spreadsheet</h1>
				<p class="mt-1 text-sm text-muted-foreground">
					Already have your tenants, properties, or units in a spreadsheet? Save it as a CSV and
					upload it here — we'll check every row and let you fix any problems before anything is
					added.
				</p>
			</div>
		</div>

		<!-- Step 1: What are you importing? -->
		<Card.Root class="mb-4">
			<Card.Content class="p-6">
				<h2 class="mb-1 text-sm font-semibold">1. What are you importing?</h2>
				<div class="mt-3 grid gap-3 sm:grid-cols-3" data-testid="import-entity-type">
					{#each ENTITY_OPTIONS as opt}
						<button
							type="button"
							data-testid="import-entity-{opt.value}"
							aria-pressed={entityType === opt.value}
							onclick={() => onEntityChange(opt.value)}
							class="flex flex-col items-start gap-0.5 rounded-lg border px-4 py-3 text-left transition-colors {entityType ===
							opt.value
								? 'border-accent bg-accent/10 ring-2 ring-accent'
								: 'border-border bg-background hover:bg-muted/50'}"
						>
							<span class="text-sm font-semibold">{opt.label}</span>
						</button>
					{/each}
				</div>

				<div class="mt-4 flex flex-wrap items-center justify-between gap-3 rounded-md border border-border bg-muted/40 px-3 py-2.5">
					<p class="text-sm text-muted-foreground" data-testid="import-columns">
						Columns: <span class="font-medium text-foreground">{selectedOption.columns}</span>
					</p>
					<Button
						variant="outline"
						size="sm"
						class="gap-1.5"
						data-testid="import-download-template"
						disabled={downloadingTemplate}
						onclick={onDownloadTemplate}
					>
						<Download class="h-4 w-4" />
						{downloadingTemplate ? 'Preparing…' : 'Download template'}
					</Button>
				</div>
			</Card.Content>
		</Card.Root>

		<!-- Step 2: Upload the file -->
		<Card.Root class="mb-4">
			<Card.Content class="p-6">
				<h2 class="mb-3 text-sm font-semibold">2. Upload your CSV file</h2>

				{#if selectedFile}
					<div
						class="flex items-center gap-3 rounded-lg border border-border bg-background px-4 py-3"
						data-testid="import-selected-file"
					>
						<FileSpreadsheet class="h-7 w-7 shrink-0 text-primary" />
						<div class="min-w-0 flex-1">
							<p class="truncate text-sm font-medium text-foreground">{selectedFile.name}</p>
							<p class="text-xs text-muted-foreground">
								{#if previewMutation.isPending}
									Checking your rows…
								{:else if preview}
									{preview.totalRows} row{preview.totalRows === 1 ? '' : 's'} found
								{:else}
									Ready
								{/if}
							</p>
						</div>
						<Button
							variant="ghost"
							size="sm"
							data-testid="import-clear-file"
							disabled={busy}
							onclick={clearFile}
						>
							Choose a different file
						</Button>
					</div>
				{:else}
					<div
						data-testid="import-file-drop"
						role="button"
						tabindex="0"
						class="relative flex cursor-pointer flex-col items-center justify-center rounded-lg border-2 border-dashed px-6 py-8 transition-colors
							{isDragging
							? 'border-accent bg-primary/5'
							: 'border-border bg-card hover:border-accent/60 hover:bg-secondary'}"
						ondragover={(e) => {
							e.preventDefault();
							isDragging = true;
						}}
						ondragleave={(e) => {
							e.preventDefault();
							isDragging = false;
						}}
						ondrop={onDrop}
						onclick={() => fileInputEl?.click()}
						onkeydown={(e) =>
							e.key === 'Enter' || e.key === ' ' ? fileInputEl?.click() : null}
					>
						<input
							bind:this={fileInputEl}
							data-testid="import-file-input"
							type="file"
							accept=".csv,text/csv"
							class="sr-only hidden"
							onchange={onInputChange}
						/>
						<Upload class="mb-3 h-10 w-10 text-muted-foreground" />
						<p class="text-sm font-medium text-foreground">Drop your CSV file here</p>
						<p class="mt-1 text-xs text-muted-foreground">or click to browse — .csv files only</p>
					</div>
				{/if}
			</Card.Content>
		</Card.Root>

		<!-- Step 3: Preview / result -->
		{#if previewMutation.isPending}
			<Card.Root class="mb-4">
				<Card.Content class="flex items-center justify-center gap-3 px-6 py-10">
					<div class="h-6 w-6 animate-spin rounded-full border-2 border-accent border-t-transparent"></div>
					<p class="text-sm text-muted-foreground">Checking your rows…</p>
				</Card.Content>
			</Card.Root>
		{:else if imported}
			<!-- Committed import result -->
			<Card.Root class="mb-4" data-testid="import-result">
				<Card.Content class="p-6">
					<div class="flex items-start gap-3">
						<CheckCircle2 class="mt-0.5 h-6 w-6 shrink-0 text-success" />
						<div class="flex-1">
							<h2 class="text-lg font-semibold">Import complete</h2>
							<p class="mt-1 text-sm text-foreground" data-testid="import-result-summary">
								Created {imported.createdRows}
								{selectedOption.listLabel.replace(/s$/, '')}{imported.createdRows === 1 ? '' : 's'}.
								{#if imported.totalRows - imported.createdRows > 0}
									{imported.totalRows - imported.createdRows} row{imported.totalRows -
										imported.createdRows ===
									1
										? ''
										: 's'} skipped — see the errors below.
								{/if}
							</p>
							<div class="mt-4 flex flex-wrap items-center gap-2">
								<Button
									class="gap-1.5"
									href={selectedOption.listHref}
									data-testid="import-view-records"
								>
									View {selectedOption.listLabel}
									<ArrowRight class="h-4 w-4" />
								</Button>
								<Button variant="outline" data-testid="import-again" onclick={clearFile}>
									Import another file
								</Button>
							</div>
						</div>
					</div>

					{#if imported.rows.some((r) => !r.valid)}
						<div class="mt-6">
							<h3 class="mb-2 text-sm font-semibold">Rows that were skipped</h3>
							{@render rowsTable(imported.rows.filter((r) => !r.valid))}
						</div>
					{/if}
				</Card.Content>
			</Card.Root>
		{:else if preview}
			<!-- Dry-run preview -->
			<Card.Root class="mb-4" data-testid="import-preview">
				<Card.Content class="p-6">
					{#if preview.totalRows === 0}
						<div class="flex items-start gap-3" data-testid="import-empty">
							<AlertCircle class="mt-0.5 h-6 w-6 shrink-0 text-warning" />
							<div>
								<h2 class="text-lg font-semibold">No rows found</h2>
								<p class="mt-1 text-sm text-muted-foreground">
									This file doesn't seem to have any data rows. Make sure the first row is the column
									headers and there's at least one row of data below it. You can download the template
									above to see the expected format.
								</p>
							</div>
						</div>
					{:else}
						<div class="flex flex-wrap items-start justify-between gap-3">
							<div class="flex items-start gap-3">
								{#if preview.validRows === preview.totalRows}
									<CheckCircle2 class="mt-0.5 h-6 w-6 shrink-0 text-success" />
								{:else}
									<AlertCircle class="mt-0.5 h-6 w-6 shrink-0 text-warning" />
								{/if}
								<div>
									<h2 class="text-lg font-semibold" data-testid="import-preview-summary">
										{preview.validRows} of {preview.totalRows} row{preview.totalRows === 1 ? '' : 's'} look
										good
									</h2>
									<p class="mt-1 text-sm text-muted-foreground">
										{#if preview.validRows === preview.totalRows}
											Everything checks out. Click below to add them.
										{:else if preview.validRows > 0}
											We'll add the good rows. Fix the rows with problems and re-upload to bring in the
											rest.
										{:else}
											None of the rows can be imported yet. Fix the problems below and upload the file
											again.
										{/if}
									</p>
								</div>
							</div>
							<Button
								class="gap-1.5"
								data-testid="import-commit"
								disabled={preview.validRows === 0 || busy}
								onclick={runImport}
							>
								{importMutation.isPending
									? 'Importing…'
									: `Import ${preview.validRows} valid row${preview.validRows === 1 ? '' : 's'}`}
							</Button>
						</div>

						<div class="mt-6">
							{@render rowsTable(preview.rows)}
						</div>
					{/if}
				</Card.Content>
			</Card.Root>
		{/if}
	</div>
</div>

{#snippet rowsTable(rows: ImportResult['rows'])}
	<Table.Root data-testid="import-rows-table">
		<Table.Header>
			<Table.Row>
				<Table.Head class="w-16">Row</Table.Head>
				<Table.Head class="w-32">Status</Table.Head>
				<Table.Head>What we found</Table.Head>
			</Table.Row>
		</Table.Header>
		<Table.Body>
			{#each rows as row (row.rowNumber)}
				<Table.Row data-testid="import-row" data-row-valid={row.valid}>
					<Table.Cell class="font-medium text-muted-foreground">{row.rowNumber}</Table.Cell>
					<Table.Cell>
						<StatusBadge status={row.valid ? 'Good' : 'Problem'} map={rowStatusMap} />
					</Table.Cell>
					<Table.Cell>
						{#if row.valid}
							<span class="text-sm text-muted-foreground">Ready to import</span>
						{:else}
							<ul class="space-y-0.5 text-sm text-destructive" data-testid="import-row-errors">
								{#each row.errors as err}
									<li>{err}</li>
								{/each}
							</ul>
						{/if}
					</Table.Cell>
				</Table.Row>
			{/each}
		</Table.Body>
	</Table.Root>
{/snippet}
