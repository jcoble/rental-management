<script lang="ts">
	import { browser } from '$app/environment';
	import { tick, onDestroy } from 'svelte';
	import { createMutation, createQuery, useQueryClient } from '@tanstack/svelte-query';
	import {
		documentTemplates,
		type CreateDocumentTemplateFieldRequest,
		type DocumentTemplate,
		type DocumentTemplateField,
		type DocumentTemplateFieldCatalogItem,
		type DocumentTemplateFieldKind,
		type DocumentTemplateSignerRole,
		type UpdateDocumentTemplateFieldRequest
	} from '$lib/api/endpoints/document-templates';
	import { documentFileHref, fileObjectUrl } from '$lib/api/endpoints/documents';
	import { apiErrorMessage, showError, showSuccess } from '$lib/utils/toast';
	import { Button } from '$lib/components/ui/button';
	import { Badge } from '$lib/components/ui/badge';
	import * as Card from '$lib/components/ui/card';
	import PdfWorkerUrl from 'pdfjs-dist/build/pdf.worker.mjs?url';
	import {
		AlertCircle,
		ArrowLeft,
		ArrowRight,
		CheckCircle2,
		ExternalLink,
		FileText,
		GripHorizontal,
		Loader2,
		MousePointerClick,
		RefreshCw,
		Trash2,
		X
	} from '@lucide/svelte';

	type PdfJsModule = typeof import('pdfjs-dist');
	type PdfViewport = { width: number; height: number };
	type PdfRenderTask = { promise: Promise<void> };
	type PdfPage = {
		getViewport: (options: { scale: number }) => PdfViewport;
		render: (options: {
			canvas: HTMLCanvasElement;
			canvasContext: CanvasRenderingContext2D;
			viewport: PdfViewport;
		}) => PdfRenderTask;
	};
	type PdfDocument = {
		numPages: number;
		getPage: (pageNumber: number) => Promise<PdfPage>;
		destroy?: () => Promise<void>;
	};
	type FieldPosition = { xPct: number; yPct: number };

	let {
		templateId,
		catalog = [],
		onClose
	}: {
		templateId: number;
		catalog?: DocumentTemplateFieldCatalogItem[];
		onClose?: () => void;
	} = $props();

	const queryClient = useQueryClient();

	let pdfjs: PdfJsModule | null = null;
	let pdfDoc = $state<PdfDocument | null>(null);
	let pdfObjectUrl = $state<string | null>(null);
	let loadedFileId = $state<number | null>(null);
	let pageCount = $state(1);
	let selectedPage = $state(1);
	let selectedCatalogKey = $state('');
	let selectedFieldId = $state<number | null>(null);
	let draftPositions = $state<Record<number, FieldPosition>>({});
	let canvasEl = $state<HTMLCanvasElement | null>(null);
	let pageFrameEl = $state<HTMLDivElement | null>(null);
	let pageRendering = $state(false);
	let pdfError = $state<string | null>(null);
	let renderToken = 0;
	let cleanupActiveDrag: (() => void) | null = null;
	let lastTemplateId = $state<number | null>(null);

	const templateQuery = createQuery(() => ({
		queryKey: ['document-template', templateId],
		enabled: templateId > 0,
		queryFn: () => documentTemplates.get(templateId)
	}));

	const template = $derived<DocumentTemplate | null>(templateQuery.data ?? null);
	const selectedCatalogItem = $derived(
		catalog.find((item) => item.fieldKey === selectedCatalogKey) ?? catalog[0] ?? null
	);
	const fields = $derived(template?.fields ?? []);
	const pageFields = $derived(
		fields
			.filter((field) => field.pageNumber === selectedPage)
			.toSorted((a, b) => a.sortOrder - b.sortOrder || a.id - b.id)
	);
	const selectedField = $derived(fields.find((field) => field.id === selectedFieldId) ?? null);
	const sourcePdfHref = $derived(
		template?.originalStoredFileId ? documentFileHref(template.originalStoredFileId) : null
	);

	const catalogGroups = $derived.by(() => {
		const groups = new Map<DocumentTemplateSignerRole, DocumentTemplateFieldCatalogItem[]>();
		for (const item of catalog) {
			const current = groups.get(item.signerRole) ?? [];
			current.push(item);
			groups.set(item.signerRole, current);
		}
		return Array.from(groups.entries()).map(([role, items]) => ({
			role,
			items: items.toSorted((a, b) => a.label.localeCompare(b.label))
		}));
	});

	$effect(() => {
		if (lastTemplateId !== templateId) {
			lastTemplateId = templateId;
			selectedPage = 1;
			selectedFieldId = null;
			draftPositions = {};
			loadedFileId = null;
			pdfError = null;
		}
	});

	$effect(() => {
		if (!selectedCatalogKey && catalog.length > 0) {
			selectedCatalogKey = catalog[0].fieldKey;
		}
	});

	$effect(() => {
		const fileId = template?.originalStoredFileId ?? null;
		if (!fileId || fileId === loadedFileId) return;
		loadedFileId = fileId;
		void loadPdf(fileId);
	});

	$effect(() => {
		const doc = pdfDoc;
		const page = selectedPage;
		const canvas = canvasEl;
		if (doc && canvas) {
			void renderPage(page);
		}
	});

	const addFieldMutation = createMutation(() => ({
		mutationFn: (request: CreateDocumentTemplateFieldRequest) =>
			documentTemplates.addField(templateId, request),
		onSuccess: (field) => {
			selectedFieldId = field.id;
			showSuccess(`${field.label} placed on page ${field.pageNumber}.`);
			invalidateTemplateQueries();
		},
		onError: (err) => showError(apiErrorMessage(err, 'Field placement failed.'))
	}));

	const moveFieldMutation = createMutation(() => ({
		mutationFn: ({
			field,
			request
		}: {
			field: DocumentTemplateField;
			request: UpdateDocumentTemplateFieldRequest;
		}) => documentTemplates.updateField(templateId, field.id, request),
		onSuccess: (field) => {
			removeDraftPosition(field.id);
			selectedFieldId = field.id;
			invalidateTemplateQueries();
		},
		onError: (err, variables) => {
			removeDraftPosition(variables.field.id);
			showError(apiErrorMessage(err, 'Field move failed.'));
		}
	}));

	const deleteFieldMutation = createMutation(() => ({
		mutationFn: (field: DocumentTemplateField) => documentTemplates.deleteField(templateId, field.id),
		onSuccess: (_result, field) => {
			removeDraftPosition(field.id);
			if (selectedFieldId === field.id) selectedFieldId = null;
			showSuccess(`${field.label} removed from this template.`);
			invalidateTemplateQueries();
		},
		onError: (err) => showError(apiErrorMessage(err, 'Field delete failed.'))
	}));

	function invalidateTemplateQueries() {
		queryClient.invalidateQueries({ queryKey: ['document-template', templateId] });
		queryClient.invalidateQueries({ queryKey: ['document-templates'] });
	}

	async function getPdfJs() {
		if (!browser) {
			throw new Error('PDF preview is only available in the browser.');
		}
		if (!pdfjs) {
			pdfjs = await import('pdfjs-dist');
			pdfjs.GlobalWorkerOptions.workerSrc = PdfWorkerUrl;
		}
		return pdfjs;
	}

	async function loadPdf(fileId: number) {
		pageRendering = true;
		pdfError = null;
		try {
			const pdf = await getPdfJs();
			const objectUrl = await fileObjectUrl(fileId);
			if (pdfObjectUrl) URL.revokeObjectURL(pdfObjectUrl);
			pdfObjectUrl = objectUrl;
			await pdfDoc?.destroy?.();
			const loadingTask = pdf.getDocument({ url: objectUrl });
			pdfDoc = (await loadingTask.promise) as unknown as PdfDocument;
			pageCount = Math.max(1, pdfDoc.numPages);
			selectedPage = Math.min(Math.max(1, selectedPage), pageCount);
			await tick();
			await renderPage(selectedPage);
		} catch (err) {
			pdfError = err instanceof Error ? err.message : 'PDF preview failed.';
		} finally {
			pageRendering = false;
		}
	}

	async function renderPage(pageNumber = selectedPage) {
		const doc = pdfDoc;
		const canvas = canvasEl;
		if (!doc || !canvas) return;

		const token = ++renderToken;
		pageRendering = true;
		try {
			const page = await doc.getPage(pageNumber);
			if (token !== renderToken) return;

			const baseViewport = page.getViewport({ scale: 1 });
			const availableWidth = Math.max(320, pageFrameEl?.clientWidth ?? 760);
			const scale = Math.min(1.6, Math.max(0.55, availableWidth / baseViewport.width));
			const viewport = page.getViewport({ scale });
			const ratio = Math.max(window.devicePixelRatio || 1, 1);
			const context = canvas.getContext('2d');
			if (!context) return;

			canvas.style.width = `${viewport.width}px`;
			canvas.style.height = `${viewport.height}px`;
			canvas.width = Math.round(viewport.width * ratio);
			canvas.height = Math.round(viewport.height * ratio);
			context.setTransform(ratio, 0, 0, ratio, 0, 0);
			context.clearRect(0, 0, viewport.width, viewport.height);

			await page.render({ canvas, canvasContext: context, viewport }).promise;
		} catch (err) {
			pdfError = err instanceof Error ? err.message : 'PDF page preview failed.';
		} finally {
			if (token === renderToken) pageRendering = false;
		}
	}

	function registerPageFrame(node: HTMLDivElement) {
		pageFrameEl = node;
		const resize = () => void renderPage(selectedPage);
		window.addEventListener('resize', resize);
		return {
			destroy() {
				window.removeEventListener('resize', resize);
				if (pageFrameEl === node) pageFrameEl = null;
			}
		};
	}

	function placeSelectedField(event: MouseEvent) {
		if (addFieldMutation.isPending || pageRendering || !selectedCatalogItem) return;
		const point = pointFromEvent(event);
		if (!point) return;

		const size = defaultFieldSize(selectedCatalogItem.kind);
		addFieldMutation.mutate({
			fieldKey: selectedCatalogItem.fieldKey,
			label: selectedCatalogItem.label,
			kind: selectedCatalogItem.kind,
			signerRole: selectedCatalogItem.signerRole,
			pageNumber: selectedPage,
			xPct: roundPct(clamp(point.xPct - size.widthPct / 2, 0, 1 - size.widthPct)),
			yPct: roundPct(clamp(point.yPct - size.heightPct / 2, 0, 1 - size.heightPct)),
			widthPct: size.widthPct,
			heightPct: size.heightPct,
			required: selectedCatalogItem.requiredForSignature,
			locked: false,
			sortOrder: fields.length + 1
		});
	}

	function startFieldDrag(event: PointerEvent, field: DocumentTemplateField) {
		event.preventDefault();
		event.stopPropagation();
		selectedFieldId = field.id;
		if (field.locked || moveFieldMutation.isPending) return;

		cleanupActiveDrag?.();
		const start = pointFromEvent(event);
		if (!start) return;
		const origin = getFieldPosition(field);
		let moved = false;

		const handleMove = (moveEvent: PointerEvent) => {
			moveEvent.preventDefault();
			const current = pointFromEvent(moveEvent);
			if (!current) return;
			const nextX = roundPct(clamp(origin.xPct + current.xPct - start.xPct, 0, 1 - field.widthPct));
			const nextY = roundPct(clamp(origin.yPct + current.yPct - start.yPct, 0, 1 - field.heightPct));
			if (Math.abs(nextX - origin.xPct) > 0.001 || Math.abs(nextY - origin.yPct) > 0.001) {
				moved = true;
			}
			draftPositions = {
				...draftPositions,
				[field.id]: { xPct: nextX, yPct: nextY }
			};
		};

		const handleUp = () => {
			cleanupActiveDrag?.();
			cleanupActiveDrag = null;
			if (!moved) {
				removeDraftPosition(field.id);
				return;
			}

			const position = draftPositions[field.id];
			if (!position) return;
			moveFieldMutation.mutate({
				field,
				request: {
					xPct: position.xPct,
					yPct: position.yPct,
					pageNumber: selectedPage
				}
			});
		};

		window.addEventListener('pointermove', handleMove);
		window.addEventListener('pointerup', handleUp, { once: true });
		window.addEventListener('pointercancel', handleUp, { once: true });
		cleanupActiveDrag = () => {
			window.removeEventListener('pointermove', handleMove);
			window.removeEventListener('pointerup', handleUp);
			window.removeEventListener('pointercancel', handleUp);
		};
	}

	function pointFromEvent(event: MouseEvent | PointerEvent): FieldPosition | null {
		if (!canvasEl) return null;
		const rect = canvasEl.getBoundingClientRect();
		if (rect.width <= 0 || rect.height <= 0) return null;
		return {
			xPct: clamp((event.clientX - rect.left) / rect.width, 0, 1),
			yPct: clamp((event.clientY - rect.top) / rect.height, 0, 1)
		};
	}

	function removeDraftPosition(fieldId: number) {
		const { [fieldId]: _removed, ...rest } = draftPositions;
		draftPositions = rest;
	}

	function getFieldPosition(field: DocumentTemplateField): FieldPosition {
		return draftPositions[field.id] ?? { xPct: field.xPct, yPct: field.yPct };
	}

	function markerStyle(field: DocumentTemplateField) {
		const position = getFieldPosition(field);
		return [
			`left:${position.xPct * 100}%`,
			`top:${position.yPct * 100}%`,
			`width:${field.widthPct * 100}%`,
			`height:${field.heightPct * 100}%`
		].join(';');
	}

	function defaultFieldSize(kind: DocumentTemplateFieldKind) {
		switch (kind) {
			case 'Signature':
				return { widthPct: 0.26, heightPct: 0.055 };
			case 'Initial':
				return { widthPct: 0.12, heightPct: 0.045 };
			case 'DateSigned':
			case 'Date':
				return { widthPct: 0.16, heightPct: 0.038 };
			case 'Currency':
				return { widthPct: 0.14, heightPct: 0.038 };
			case 'Checkbox':
				return { widthPct: 0.04, heightPct: 0.035 };
			default:
				return { widthPct: 0.22, heightPct: 0.038 };
		}
	}

	function clamp(value: number, min: number, max: number) {
		return Math.min(max, Math.max(min, value));
	}

	function roundPct(value: number) {
		return Math.round(value * 10_000) / 10_000;
	}

	function formatSigner(role: DocumentTemplateSignerRole) {
		switch (role) {
			case 'Tenant':
				return 'Tenant';
			case 'Landlord':
				return 'Landlord';
			case 'CoSigner':
				return 'Co-signer';
			default:
				return 'Auto-fill';
		}
	}

	function fieldTone(role: DocumentTemplateSignerRole) {
		switch (role) {
			case 'Tenant':
				return 'border-sky-500 bg-sky-500/12 text-sky-950';
			case 'Landlord':
				return 'border-emerald-500 bg-emerald-500/12 text-emerald-950';
			case 'CoSigner':
				return 'border-amber-500 bg-amber-500/14 text-amber-950';
			default:
				return 'border-slate-500 bg-slate-500/10 text-slate-950';
		}
	}

	function pageNumbers() {
		return Array.from({ length: pageCount }, (_, index) => index + 1);
	}

	onDestroy(() => {
		cleanupActiveDrag?.();
		if (pdfObjectUrl) URL.revokeObjectURL(pdfObjectUrl);
		void pdfDoc?.destroy?.();
	});
</script>

<Card.Root class="overflow-hidden border-primary/20" data-testid="lease-template-designer">
	<Card.Header class="border-b border-border bg-muted/20">
		<div class="flex flex-wrap items-start justify-between gap-3">
			<div class="min-w-0">
				<Card.Title class="truncate text-base">
					{template?.name ?? 'Lease template designer'}
				</Card.Title>
				<Card.Description>
					{template ? `${template.fieldCount} field${template.fieldCount === 1 ? '' : 's'} placed` : 'Loading template'}
				</Card.Description>
			</div>
			<div class="flex flex-wrap gap-2">
				<Button
					variant="outline"
					size="sm"
					class="gap-1.5"
					onclick={() => templateQuery.refetch()}
					disabled={templateQuery.isFetching}
					data-testid="lease-template-designer-refresh"
				>
					<RefreshCw class="h-4 w-4 {templateQuery.isFetching ? 'animate-spin' : ''}" />
					Refresh
				</Button>
				{#if sourcePdfHref}
					<Button
						href={sourcePdfHref}
						target="_blank"
						rel="noopener noreferrer"
						variant="outline"
						size="sm"
						class="gap-1.5"
						data-testid="lease-template-designer-source"
					>
						<ExternalLink class="h-4 w-4" />
						Source PDF
					</Button>
				{/if}
				<Button variant="ghost" size="sm" class="gap-1.5" onclick={onClose} data-testid="lease-template-designer-close">
					<X class="h-4 w-4" />
					Close
				</Button>
			</div>
		</div>
	</Card.Header>

	<Card.Content class="space-y-5 pt-5">
		{#if templateQuery.isLoading}
			<div class="grid gap-4 lg:grid-cols-[260px_minmax(0,1fr)_280px]">
				<div class="h-80 animate-pulse rounded-lg border border-border bg-muted/30"></div>
				<div class="h-[640px] animate-pulse rounded-lg border border-border bg-muted/30"></div>
				<div class="h-80 animate-pulse rounded-lg border border-border bg-muted/30"></div>
			</div>
		{:else if templateQuery.isError || !template}
			<div class="rounded-lg border border-destructive/30 bg-destructive/5 p-4" data-testid="lease-template-designer-error">
				<div class="flex items-center gap-2 text-sm font-medium text-destructive">
					<AlertCircle class="h-4 w-4" />
					Template designer is unavailable.
				</div>
				<Button variant="outline" size="sm" class="mt-3" onclick={() => templateQuery.refetch()}>Retry</Button>
			</div>
		{:else if !template.originalStoredFileId}
			<div class="rounded-lg border border-dashed border-border bg-muted/20 p-8 text-center">
				<FileText class="mx-auto h-8 w-8 text-muted-foreground" />
				<h3 class="mt-3 text-sm font-semibold">No source PDF attached</h3>
				<p class="mx-auto mt-1 max-w-md text-sm text-muted-foreground">
					Upload a landlord PDF before placing fields.
				</p>
			</div>
		{:else}
			<div class="grid gap-5 lg:grid-cols-[280px_minmax(0,1fr)_300px]">
				<aside class="space-y-4">
					<div class="rounded-lg border border-border bg-background p-4">
						<div class="flex items-center gap-2">
							<MousePointerClick class="h-4 w-4 text-primary" />
							<h3 class="text-sm font-semibold">Field to Place</h3>
						</div>
						<label for="lease-template-field-select" class="mt-4 block text-sm font-medium">
							Dynamic field
						</label>
						<select
							id="lease-template-field-select"
							bind:value={selectedCatalogKey}
							class="mt-2 h-10 w-full rounded-md border border-input bg-background px-3 text-sm shadow-sm focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring"
							data-testid="lease-template-field-select"
						>
							{#each catalogGroups as group}
								<optgroup label={formatSigner(group.role)}>
									{#each group.items as item}
										<option value={item.fieldKey}>{item.label}</option>
									{/each}
								</optgroup>
							{/each}
						</select>
						{#if selectedCatalogItem}
							<div class="mt-3 rounded-md bg-muted/30 px-3 py-2">
								<div class="flex flex-wrap items-center gap-2">
									<Badge variant="outline">{formatSigner(selectedCatalogItem.signerRole)}</Badge>
									<Badge variant={selectedCatalogItem.requiredForSignature ? 'default' : 'secondary'}>
										{selectedCatalogItem.requiredForSignature ? 'Required' : selectedCatalogItem.kind}
									</Badge>
								</div>
								<p class="mt-2 text-xs text-muted-foreground">{selectedCatalogItem.description}</p>
							</div>
						{/if}
					</div>

					<div class="rounded-lg border border-border bg-background p-4">
						<h3 class="text-sm font-semibold">Page</h3>
						<div class="mt-3 flex items-center gap-2">
							<Button
								variant="outline"
								size="icon"
								disabled={selectedPage <= 1}
								onclick={() => (selectedPage = Math.max(1, selectedPage - 1))}
								aria-label="Previous page"
								data-testid="lease-template-prev-page"
							>
								<ArrowLeft class="h-4 w-4" />
							</Button>
							<select
								bind:value={selectedPage}
								class="h-9 flex-1 rounded-md border border-input bg-background px-2 text-sm"
								data-testid="lease-template-page-select"
							>
								{#each pageNumbers() as page}
									<option value={page}>Page {page}</option>
								{/each}
							</select>
							<Button
								variant="outline"
								size="icon"
								disabled={selectedPage >= pageCount}
								onclick={() => (selectedPage = Math.min(pageCount, selectedPage + 1))}
								aria-label="Next page"
								data-testid="lease-template-next-page"
							>
								<ArrowRight class="h-4 w-4" />
							</Button>
						</div>
						<p class="mt-2 text-xs text-muted-foreground">
							{pageFields.length} field{pageFields.length === 1 ? '' : 's'} on this page.
						</p>
					</div>
				</aside>

				<section class="min-w-0">
					<div class="rounded-lg border border-border bg-muted/25 p-3" data-testid="lease-template-pdf-preview">
						{#if pdfError}
							<div class="rounded-md border border-destructive/30 bg-destructive/5 p-4 text-sm text-destructive">
								{pdfError}
							</div>
						{/if}
						<div class="overflow-auto rounded-md bg-slate-200/70 p-4">
							<div use:registerPageFrame class="mx-auto max-w-full">
								<div class="relative mx-auto inline-block touch-none overflow-hidden rounded-sm bg-white shadow-md ring-1 ring-slate-300">
									<canvas
										bind:this={canvasEl}
										class="block max-w-full cursor-crosshair bg-white"
										onclick={placeSelectedField}
										data-testid="lease-template-pdf-canvas"
									></canvas>
									{#if pageRendering}
										<div class="absolute inset-0 grid place-items-center bg-white/45">
											<span class="inline-flex items-center gap-2 rounded-full border border-border bg-background px-3 py-1.5 text-xs font-medium shadow-sm">
												<Loader2 class="h-3.5 w-3.5 animate-spin" />
												Rendering
											</span>
										</div>
									{/if}
									{#each pageFields as field (field.id)}
										<button
											type="button"
											style={markerStyle(field)}
											class="absolute z-[1] flex min-h-6 items-center gap-1 overflow-hidden rounded border px-1.5 text-left text-[11px] font-semibold leading-tight shadow-sm backdrop-blur-sm transition {fieldTone(field.signerRole)} {selectedFieldId === field.id ? 'ring-2 ring-primary ring-offset-1' : ''}"
											data-template-field-marker
											data-testid={field.testId}
											title={`${field.label} · ${formatSigner(field.signerRole)}`}
											onpointerdown={(event) => startFieldDrag(event, field)}
										>
											<GripHorizontal class="h-3 w-3 shrink-0" />
											<span class="truncate">{field.label}</span>
										</button>
									{/each}
								</div>
							</div>
						</div>
					</div>
				</section>

				<aside class="space-y-4">
					<div class="rounded-lg border border-border bg-background p-4">
						<div class="flex items-center justify-between gap-2">
							<h3 class="text-sm font-semibold">Placed Fields</h3>
							<Badge variant="secondary">{fields.length}</Badge>
						</div>
						{#if fields.length === 0}
							<div class="mt-4 rounded-md border border-dashed border-border bg-muted/20 p-4 text-sm text-muted-foreground">
								No fields placed yet.
							</div>
						{:else}
							<div class="mt-3 max-h-80 space-y-2 overflow-y-auto pr-1">
								{#each fields.toSorted((a, b) => a.pageNumber - b.pageNumber || a.sortOrder - b.sortOrder || a.id - b.id) as field (field.id)}
									<button
										type="button"
										class="w-full rounded-md border px-3 py-2 text-left text-sm transition hover:bg-muted/30 {selectedFieldId === field.id ? 'border-primary bg-primary/5' : 'border-border'}"
										onclick={() => {
											selectedFieldId = field.id;
											selectedPage = field.pageNumber;
										}}
										data-testid="lease-template-field-row-{field.id}"
									>
										<span class="flex items-center justify-between gap-2">
											<span class="truncate font-medium">{field.label}</span>
											<span class="shrink-0 text-xs text-muted-foreground">P{field.pageNumber}</span>
										</span>
										<span class="mt-1 flex flex-wrap gap-1">
											<Badge variant="outline" class="text-[10px]">{formatSigner(field.signerRole)}</Badge>
											{#if field.required}
												<Badge variant="secondary" class="gap-1 text-[10px]">
													<CheckCircle2 class="h-3 w-3" />
													Required
												</Badge>
											{/if}
										</span>
									</button>
								{/each}
							</div>
						{/if}
					</div>

					<div class="rounded-lg border border-border bg-background p-4" data-testid="lease-template-selected-field">
						<h3 class="text-sm font-semibold">Selected Field</h3>
						{#if selectedField}
							<div class="mt-3 space-y-3">
								<div>
									<p class="text-sm font-medium">{selectedField.label}</p>
									<p class="text-xs text-muted-foreground">{selectedField.fieldKey}</p>
								</div>
								<div class="grid grid-cols-2 gap-2 text-xs">
									<div class="rounded-md bg-muted/30 px-2 py-2">
										<p class="text-muted-foreground">Page</p>
										<p class="font-medium">{selectedField.pageNumber}</p>
									</div>
									<div class="rounded-md bg-muted/30 px-2 py-2">
										<p class="text-muted-foreground">Signer</p>
										<p class="font-medium">{formatSigner(selectedField.signerRole)}</p>
									</div>
									<div class="rounded-md bg-muted/30 px-2 py-2">
										<p class="text-muted-foreground">X / Y</p>
										<p class="font-medium">
											{Math.round(getFieldPosition(selectedField).xPct * 100)}% /
											{Math.round(getFieldPosition(selectedField).yPct * 100)}%
										</p>
									</div>
									<div class="rounded-md bg-muted/30 px-2 py-2">
										<p class="text-muted-foreground">Size</p>
										<p class="font-medium">
											{Math.round(selectedField.widthPct * 100)}% × {Math.round(selectedField.heightPct * 100)}%
										</p>
									</div>
								</div>
								<Button
									variant="destructive"
									size="sm"
									class="w-full gap-1.5"
									disabled={deleteFieldMutation.isPending}
									onclick={() => deleteFieldMutation.mutate(selectedField)}
									data-testid="lease-template-delete-field"
								>
									<Trash2 class="h-4 w-4" />
									{deleteFieldMutation.isPending ? 'Removing…' : 'Remove field'}
								</Button>
							</div>
						{:else}
							<p class="mt-3 text-sm text-muted-foreground">
								Select a placed field to inspect or remove it.
							</p>
						{/if}
					</div>
				</aside>
			</div>
		{/if}
	</Card.Content>
</Card.Root>
