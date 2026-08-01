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
	import { leaseManagements } from '$lib/api/endpoints/lease-managements';
	import { documentFileHref, fileBlob } from '$lib/api/endpoints/documents';
	import { idempotentMutation } from '$lib/api/idempotency';
	import { getCurrentPortfolioId } from '$lib/stores/portfolio.svelte';
	import type { LeaseManagementSummary } from '$lib/types';
	import { apiErrorMessage, showError, showSuccess } from '$lib/utils/toast';
	import { Button } from '$lib/components/ui/button';
	import { Badge } from '$lib/components/ui/badge';
	import * as Card from '$lib/components/ui/card';
	import * as Tabs from '$lib/components/ui/tabs';
	import SimpleSelect from '$lib/components/shared/SimpleSelect.svelte';
	import PdfWorkerUrl from 'pdfjs-dist/build/pdf.worker.mjs?url';
	import {
		adjustFieldRectSize,
		clampDesignerZoom,
		clampFieldRect,
		MAX_FIELD_DIMENSION_PERCENT,
		MIN_FIELD_DIMENSION_PERCENT,
		moveFieldRect,
		parseFieldDimensionPercent,
		resizeFieldRect,
		type FieldRect,
		type FieldResizeHandle
	} from './lease-template-designer-utils';
	import {
		AlertCircle,
		ArrowLeft,
		ArrowRight,
		ArrowLeftRight,
		ArrowUpDown,
		CheckCircle2,
		Eraser,
		ExternalLink,
		Eye,
		FileText,
		GripHorizontal,
		Loader2,
		Maximize2,
		MousePointer2,
		PanelLeft,
		RefreshCw,
		Save,
		Trash2,
		ZoomIn,
		ZoomOut,
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
	type FieldPoint = { xPct: number; yPct: number };
	type CatalogTabId = 'autofill' | 'tenant' | 'landlord';
	type DesignerViewMode = 'design' | 'preview';
	type DesignerTool = 'place' | 'whiteout';

	const WHITEOUT_FIELD_KEY = 'pdf.whiteout';
	const WHITEOUT_FIELD_LABEL = 'Erase PDF text';

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
	let loadedPdfKey = $state<string | null>(null);
	let pageCount = $state(1);
	let selectedPage = $state(1);
	let activeCatalogTab = $state<CatalogTabId>('autofill');
	let selectedCatalogKey = $state('');
	let draggingCatalogKey = $state<string | null>(null);
	let selectedFieldId = $state<number | null>(null);
	let viewMode = $state<DesignerViewMode>('design');
	let designerTool = $state<DesignerTool>('place');
	let previewLeaseId = $state<number | null>(null);
	let draftRects = $state<Record<number, FieldRect>>({});
	let canvasEl = $state<HTMLCanvasElement | null>(null);
	let pageFrameEl = $state<HTMLDivElement | null>(null);
	let pageRendering = $state(false);
	let pdfError = $state<string | null>(null);
	let zoomMode = $state<'fit' | 'manual'>('fit');
	let zoomPct = $state(100);
	let renderedZoomPct = $state(100);
	let lastSavedAt = $state<Date | null>(null);
	let saveErrorMessage = $state<string | null>(null);
	let fieldDimensionError = $state<{
		fieldId: number;
		dimension: 'widthPct' | 'heightPct';
		message: string;
	} | null>(null);
	let renderToken = 0;
	let cleanupActiveDrag: (() => void) | null = null;
	let lastTemplateId = $state<number | null>(null);

	const catalogTabDefinitions: {
		id: CatalogTabId;
		label: string;
		roles: DocumentTemplateSignerRole[];
	}[] = [
		{ id: 'autofill', label: 'Auto-fill', roles: ['None'] },
		{ id: 'tenant', label: 'Tenant', roles: ['Tenant', 'CoTenant'] },
		{ id: 'landlord', label: 'Landlord', roles: ['Landlord'] }
	];

	const templateQuery = createQuery(() => ({
		queryKey: ['document-template', templateId],
		enabled: templateId > 0,
		queryFn: () => documentTemplates.get(templateId)
	}));

	const template = $derived<DocumentTemplate | null>(templateQuery.data ?? null);
	const portfolioId = $derived(getCurrentPortfolioId());
	const previewLeasesQuery = createQuery(() => ({
		queryKey: ['document-template-preview-leases', portfolioId, template?.propertyId ?? null],
		enabled: viewMode === 'preview' && portfolioId > 0 && !!template,
		queryFn: () =>
			leaseManagements.listPage({
				take: 50,
				sort: '-updatedAtUtc',
				propertyId: template?.propertyId ?? undefined
			})
	}));
	const selectedCatalogItem = $derived(
		catalog.find((item) => item.fieldKey === selectedCatalogKey) ?? null
	);
	const fields = $derived(template?.fields ?? []);
	const previewLeases = $derived<LeaseManagementSummary[]>(
		(previewLeasesQuery.data?.items ?? []).filter((relationship) => relationship.leaseAgreementId != null)
	);
	const selectedPreviewLease = $derived(
		previewLeases.find((relationship) => relationship.leaseAgreementId === previewLeaseId) ?? null
	);
	const pageFields = $derived(
		fields
			.filter((field) => field.pageNumber === selectedPage)
			.toSorted((a, b) => a.sortOrder - b.sortOrder || a.id - b.id)
	);
	const selectedField = $derived(fields.find((field) => field.id === selectedFieldId) ?? null);
	const sourcePdfHref = $derived(
		template?.originalStoredFileId ? documentFileHref(template.originalStoredFileId) : null
	);

	const catalogTabs = $derived(
		catalogTabDefinitions.map((tab) => ({
			...tab,
			items: catalog
				.filter((item) => tab.roles.includes(item.signerRole))
				.toSorted((a, b) => a.label.localeCompare(b.label))
		}))
	);
	const activeCatalogItems = $derived(
		catalogTabs.find((tab) => tab.id === activeCatalogTab)?.items ?? []
	);

	$effect(() => {
		if (lastTemplateId !== templateId) {
			lastTemplateId = templateId;
			selectedPage = 1;
			selectedFieldId = null;
			viewMode = 'design';
			designerTool = 'place';
			previewLeaseId = null;
			draftRects = {};
			loadedPdfKey = null;
			pdfError = null;
			saveErrorMessage = null;
			lastSavedAt = null;
		}
	});

	$effect(() => {
		if (catalog.length === 0) {
			selectedCatalogKey = '';
			return;
		}
		if (selectedCatalogKey && catalog.some((item) => item.fieldKey === selectedCatalogKey)) return;
		selectedCatalogKey = activeCatalogItems[0]?.fieldKey ?? catalog[0].fieldKey;
	});

	$effect(() => {
		const tab = activeCatalogTab;
		const items = activeCatalogItems;
		if (!tab || items.length === 0) return;
		if (items.some((item) => item.fieldKey === selectedCatalogKey)) return;
		selectedCatalogKey = items[0].fieldKey;
	});

	$effect(() => {
		if (viewMode !== 'preview') return;
		const leases = previewLeases;
		if (leases.length === 0) {
			previewLeaseId = null;
			return;
		}
		if (!previewLeaseId || !leases.some((relationship) => relationship.leaseAgreementId === previewLeaseId)) {
			previewLeaseId = leases[0].leaseAgreementId ?? null;
		}
	});

	$effect(() => {
		if (viewMode === 'preview') {
			selectedFieldId = null;
			cleanupActiveDrag?.();
			cleanupActiveDrag = null;
		}
	});

	$effect(() => {
		const fileId = template?.originalStoredFileId ?? null;
		const mode = viewMode;
		const leaseId = previewLeaseId;
		const version = template?.version ?? 0;
		if (!fileId) return;

		if (mode === 'preview') {
			if (!leaseId) return;
			const key = `preview:${templateId}:${leaseId}:${version}`;
			if (key === loadedPdfKey) return;
			loadedPdfKey = key;
			void loadPreviewPdf(leaseId, key);
			return;
		}

		const key = `source:${fileId}`;
		if (key === loadedPdfKey) return;
		loadedPdfKey = key;
		void loadSourcePdf(fileId, key);
	});

	$effect(() => {
		const doc = pdfDoc;
		const page = selectedPage;
		const canvas = canvasEl;
		const mode = zoomMode;
		const zoom = zoomPct;
		if (doc && canvas) {
			mode;
			zoom;
			void renderPage(page);
		}
	});

	const addFieldMutation = createMutation(() => ({
		mutationFn: (request: CreateDocumentTemplateFieldRequest) =>
			idempotentMutation(
				`document-template:${templateId}:field-add:${JSON.stringify(request)}`,
				(operationKey) => documentTemplates.addField(templateId, request, operationKey)
			),
		onMutate: () => {
			saveErrorMessage = null;
		},
		onSuccess: (field) => {
			selectedFieldId = field.id;
			markSaved();
			showSuccess(`${field.label} placed on page ${field.pageNumber}.`);
			invalidateTemplateQueries();
		},
		onError: (err) => handleSaveError(err, 'Field placement failed.')
	}));

	const moveFieldMutation = createMutation(() => ({
		mutationFn: ({
			field,
			request
		}: {
			field: DocumentTemplateField;
			request: UpdateDocumentTemplateFieldRequest;
		}) =>
			idempotentMutation(
				`document-template:${templateId}:field-update:${field.id}:${JSON.stringify(request)}`,
				(operationKey) =>
					documentTemplates.updateField(templateId, field.id, request, operationKey)
			),
		onMutate: () => {
			saveErrorMessage = null;
		},
		onSuccess: (field) => {
			removeDraftRect(field.id);
			selectedFieldId = field.id;
			markSaved();
			invalidateTemplateQueries();
		},
		onError: (err, variables) => {
			removeDraftRect(variables.field.id);
			handleSaveError(err, 'Field update failed.');
		}
	}));

	const deleteFieldMutation = createMutation(() => ({
		mutationFn: (field: DocumentTemplateField) =>
			idempotentMutation(`document-template:${templateId}:field-delete:${field.id}`, (operationKey) =>
				documentTemplates.deleteField(templateId, field.id, operationKey)
			),
		onMutate: () => {
			saveErrorMessage = null;
		},
		onSuccess: (_result, field) => {
			removeDraftRect(field.id);
			if (selectedFieldId === field.id) selectedFieldId = null;
			markSaved();
			showSuccess(`${field.label} removed from this template.`);
			invalidateTemplateQueries();
		},
		onError: (err) => handleSaveError(err, 'Field delete failed.')
	}));

	const isSaving = $derived(
		addFieldMutation.isPending || moveFieldMutation.isPending || deleteFieldMutation.isPending
	);
	const saveStatusLabel = $derived.by(() => {
		if (isSaving) return 'Saving';
		if (saveErrorMessage) return 'Save failed';
		if (lastSavedAt) {
			return `Saved ${lastSavedAt.toLocaleTimeString([], { hour: 'numeric', minute: '2-digit' })}`;
		}
		return 'Auto-save ready';
	});
	const saveStatusClass = $derived.by(() => {
		if (saveErrorMessage) return 'border-destructive/35 bg-destructive/10 text-destructive';
		if (isSaving) return 'border-primary/25 bg-primary/10 text-primary';
		return 'border-emerald-500/30 bg-emerald-500/10 text-emerald-700 dark:text-emerald-200';
	});

	function invalidateTemplateQueries() {
		queryClient.invalidateQueries({ queryKey: ['document-template', templateId] });
		queryClient.invalidateQueries({ queryKey: ['document-templates'] });
	}

	function markSaved() {
		saveErrorMessage = null;
		lastSavedAt = new Date();
	}

	function handleSaveError(err: unknown, fallback: string) {
		const message = apiErrorMessage(err, fallback);
		saveErrorMessage = message;
		showError(message);
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

	async function loadSourcePdf(fileId: number, key: string) {
		try {
			const blob = await fileBlob(fileId);
			if (loadedPdfKey !== key) {
				return;
			}
			await loadPdfBlob(blob);
		} catch (err) {
			if (loadedPdfKey === key) {
				resetPdfDisplay();
				pdfError =
					err instanceof Error && err.message.includes('(404)')
						? 'Template source PDF is unavailable.'
						: err instanceof Error
							? err.message
							: 'PDF preview failed.';
			}
		}
	}

	async function loadPreviewPdf(leaseId: number, key: string) {
		try {
			const blob = await documentTemplates.previewLeaseAgreementPdf(templateId, leaseId);
			if (loadedPdfKey !== key) {
				return;
			}
			await loadPdfBlob(blob);
		} catch (err) {
			if (loadedPdfKey === key) {
				resetPdfDisplay();
				pdfError = apiErrorMessage(err, 'Lease-filled preview failed.');
			}
		}
	}

	async function loadPdfBlob(blob: Blob) {
		pageRendering = true;
		pdfError = null;
		try {
			const pdf = await getPdfJs();
			await pdfDoc?.destroy?.();
			const data = new Uint8Array(await blob.arrayBuffer());
			const loadingTask = pdf.getDocument({ data });
			pdfDoc = (await loadingTask.promise) as unknown as PdfDocument;
			pageCount = Math.max(1, pdfDoc.numPages);
			selectedPage = Math.min(Math.max(1, selectedPage), pageCount);
			await tick();
			await renderPage(selectedPage);
		} catch (err) {
			resetPdfDisplay();
			pdfError = err instanceof Error ? err.message : 'PDF preview failed.';
		} finally {
			pageRendering = false;
		}
	}

	function resetPdfDisplay() {
		void pdfDoc?.destroy?.();
		pdfDoc = null;
		pageCount = 1;
		selectedPage = 1;
		pageRendering = false;
		const context = canvasEl?.getContext('2d');
		if (canvasEl && context) {
			context.clearRect(0, 0, canvasEl.width, canvasEl.height);
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
			const fitScale = Math.min(1.6, Math.max(0.55, availableWidth / baseViewport.width));
			const scale = zoomMode === 'fit' ? fitScale : clampDesignerZoom(zoomPct) / 100;
			renderedZoomPct = Math.round(scale * 100);
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
		if (viewMode !== 'design') return;
		const point = pointFromEvent(event);
		if (!point) return;
		if (designerTool === 'whiteout') {
			placeWhiteoutAt(point);
			return;
		}
		placeCatalogFieldAt(selectedCatalogKey, point);
	}

	function placeCatalogFieldAt(fieldKey: string, point: FieldPoint) {
		if (viewMode !== 'design' || designerTool !== 'place') return;
		if (addFieldMutation.isPending || pageRendering) return;
		const catalogItem = catalog.find((item) => item.fieldKey === fieldKey);
		if (!catalogItem) return;

		const size = defaultFieldSize(catalogItem.kind);
		const rect = clampFieldRect({
			xPct: point.xPct - size.widthPct / 2,
			yPct: point.yPct - size.heightPct / 2,
			widthPct: size.widthPct,
			heightPct: size.heightPct
		});
		addFieldMutation.mutate({
			fieldKey: catalogItem.fieldKey,
			label: catalogItem.label,
			kind: catalogItem.kind,
			signerRole: catalogItem.signerRole,
			pageNumber: selectedPage,
			xPct: rect.xPct,
			yPct: rect.yPct,
			widthPct: rect.widthPct,
			heightPct: rect.heightPct,
			required: catalogItem.requiredForSignature,
			locked: false,
			sortOrder: fields.length + 1
		});
	}

	function placeWhiteoutAt(point: FieldPoint) {
		if (viewMode !== 'design' || designerTool !== 'whiteout') return;
		if (addFieldMutation.isPending || pageRendering) return;

		const size = defaultFieldSize('Whiteout');
		const rect = clampFieldRect({
			xPct: point.xPct - size.widthPct / 2,
			yPct: point.yPct - size.heightPct / 2,
			widthPct: size.widthPct,
			heightPct: size.heightPct
		});

		addFieldMutation.mutate({
			fieldKey: WHITEOUT_FIELD_KEY,
			label: WHITEOUT_FIELD_LABEL,
			kind: 'Whiteout',
			signerRole: 'None',
			pageNumber: selectedPage,
			xPct: rect.xPct,
			yPct: rect.yPct,
			widthPct: rect.widthPct,
			heightPct: rect.heightPct,
			required: false,
			locked: false,
			sortOrder: fields.length + 1
		});
	}

	function selectCatalogItem(item: DocumentTemplateFieldCatalogItem) {
		activeCatalogTab = tabForSigner(item.signerRole);
		selectedCatalogKey = item.fieldKey;
	}

	function startCatalogDrag(event: DragEvent, item: DocumentTemplateFieldCatalogItem) {
		if (viewMode !== 'design' || designerTool !== 'place') {
			event.preventDefault();
			return;
		}
		selectCatalogItem(item);
		draggingCatalogKey = item.fieldKey;
		if (!event.dataTransfer) return;
		event.dataTransfer.effectAllowed = 'copy';
		event.dataTransfer.setData('application/x-rental-template-field', item.fieldKey);
		event.dataTransfer.setData('text/plain', item.fieldKey);
	}

	function endCatalogDrag() {
		draggingCatalogKey = null;
	}

	function handlePdfDragOver(event: DragEvent) {
		if (viewMode !== 'design' || designerTool !== 'place') return;
		const fieldKey = draggedCatalogKey(event);
		if (!fieldKey) return;
		event.preventDefault();
		if (event.dataTransfer) event.dataTransfer.dropEffect = 'copy';
	}

	function handlePdfDrop(event: DragEvent) {
		if (viewMode !== 'design' || designerTool !== 'place') return;
		const fieldKey = draggedCatalogKey(event);
		if (!fieldKey) return;
		event.preventDefault();
		draggingCatalogKey = null;
		const point = pointFromEvent(event);
		if (!point) return;
		placeCatalogFieldAt(fieldKey, point);
	}

	function draggedCatalogKey(event: DragEvent) {
		const fieldKey =
			event.dataTransfer?.getData('application/x-rental-template-field') ||
			event.dataTransfer?.getData('text/plain') ||
			draggingCatalogKey ||
			'';
		return catalog.some((item) => item.fieldKey === fieldKey) ? fieldKey : '';
	}

	function handleFieldPointerDown(event: PointerEvent, field: DocumentTemplateField) {
		if (viewMode !== 'design') return;
		startFieldDrag(event, field);
	}

	function startFieldDrag(event: PointerEvent, field: DocumentTemplateField) {
		event.preventDefault();
		event.stopPropagation();
		selectedFieldId = field.id;
		if (field.locked || moveFieldMutation.isPending) return;

		cleanupActiveDrag?.();
		const start = pointFromEvent(event);
		if (!start) return;
		const origin = getFieldRect(field);
		let moved = false;

		const handleMove = (moveEvent: PointerEvent) => {
			moveEvent.preventDefault();
			const current = pointFromEvent(moveEvent);
			if (!current) return;
			const next = moveFieldRect(origin, current.xPct - start.xPct, current.yPct - start.yPct);
			if (fieldRectChanged(origin, next)) {
				moved = true;
			}
			draftRects = {
				...draftRects,
				[field.id]: next
			};
		};

		const handleUp = () => {
			cleanupActiveDrag?.();
			cleanupActiveDrag = null;
			if (!moved) {
				removeDraftRect(field.id);
				return;
			}

			const rect = draftRects[field.id];
			if (!rect) return;
			persistFieldRect(field, rect);
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

	function startFieldResize(
		event: PointerEvent,
		field: DocumentTemplateField,
		handle: FieldResizeHandle
	) {
		event.preventDefault();
		event.stopPropagation();
		if (viewMode !== 'design') return;
		selectedFieldId = field.id;
		if (field.locked || moveFieldMutation.isPending) return;

		cleanupActiveDrag?.();
		const start = pointFromEvent(event);
		if (!start) return;
		const origin = getFieldRect(field);
		let resized = false;

		const handleMove = (moveEvent: PointerEvent) => {
			moveEvent.preventDefault();
			const current = pointFromEvent(moveEvent);
			if (!current) return;
			const next = resizeFieldRect(origin, current.xPct - start.xPct, current.yPct - start.yPct, handle);
			if (fieldRectChanged(origin, next)) resized = true;
			draftRects = {
				...draftRects,
				[field.id]: next
			};
		};

		const handleUp = () => {
			cleanupActiveDrag?.();
			cleanupActiveDrag = null;
			if (!resized) {
				removeDraftRect(field.id);
				return;
			}
			const rect = draftRects[field.id];
			if (!rect) return;
			persistFieldRect(field, rect);
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

	function adjustSelectedFieldSize(widthDeltaPct: number, heightDeltaPct: number) {
		if (!selectedField || selectedField.locked || moveFieldMutation.isPending) return;
		const next = adjustFieldRectSize(getFieldRect(selectedField), widthDeltaPct, heightDeltaPct);
		draftRects = {
			...draftRects,
			[selectedField.id]: next
		};
		persistFieldRect(selectedField, next);
	}

	function setSelectedFieldDimension(
		field: DocumentTemplateField,
		dimension: 'widthPct' | 'heightPct',
		event: Event
	) {
		if (field.locked || moveFieldMutation.isPending) return;
		const target = event.currentTarget as HTMLInputElement;
		const parsed = parseFieldDimensionPercent(target.value);
		if (parsed.error) {
			fieldDimensionError = {
				fieldId: field.id,
				dimension,
				message: parsed.error
			};
			return;
		}
		fieldDimensionError = null;
		const rect = clampFieldRect({
			...getFieldRect(field),
			[dimension]: parsed.valuePct
		});
		draftRects = {
			...draftRects,
			[field.id]: rect
		};
		persistFieldRect(field, rect);
	}

	function persistFieldRect(field: DocumentTemplateField, rect: FieldRect) {
		moveFieldMutation.mutate({
			field,
			request: {
				xPct: rect.xPct,
				yPct: rect.yPct,
				widthPct: rect.widthPct,
				heightPct: rect.heightPct,
				pageNumber: selectedPage
			}
		});
	}

	function pointFromEvent(event: MouseEvent | PointerEvent | DragEvent): FieldPoint | null {
		if (!canvasEl) return null;
		const rect = canvasEl.getBoundingClientRect();
		if (rect.width <= 0 || rect.height <= 0) return null;
		return {
			xPct: clampUnit((event.clientX - rect.left) / rect.width),
			yPct: clampUnit((event.clientY - rect.top) / rect.height)
		};
	}

	function removeDraftRect(fieldId: number) {
		const { [fieldId]: _removed, ...rest } = draftRects;
		draftRects = rest;
	}

	function getFieldRect(field: DocumentTemplateField): FieldRect {
		return (
			draftRects[field.id] ?? {
				xPct: field.xPct,
				yPct: field.yPct,
				widthPct: field.widthPct,
				heightPct: field.heightPct
			}
		);
	}

	function markerStyle(field: DocumentTemplateField) {
		const rect = getFieldRect(field);
		return [
			`left:${rect.xPct * 100}%`,
			`top:${rect.yPct * 100}%`,
			`width:${rect.widthPct * 100}%`,
			`height:${rect.heightPct * 100}%`
		].join(';');
	}

	function fieldRectChanged(a: FieldRect, b: FieldRect) {
		return (
			Math.abs(a.xPct - b.xPct) > 0.001 ||
			Math.abs(a.yPct - b.yPct) > 0.001 ||
			Math.abs(a.widthPct - b.widthPct) > 0.001 ||
			Math.abs(a.heightPct - b.heightPct) > 0.001
		);
	}

	function defaultFieldSize(kind: DocumentTemplateFieldKind) {
		switch (kind) {
			case 'Whiteout':
				return { widthPct: 0.22, heightPct: 0.045 };
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

	function setDesignerZoom(nextZoomPct: number) {
		zoomMode = 'manual';
		zoomPct = clampDesignerZoom(nextZoomPct);
	}

	function adjustZoom(deltaPct: number) {
		setDesignerZoom((zoomMode === 'fit' ? renderedZoomPct : zoomPct) + deltaPct);
	}

	function fitZoom() {
		zoomMode = 'fit';
	}

	function tabForSigner(role: DocumentTemplateSignerRole): CatalogTabId {
		if (role === 'Landlord') return 'landlord';
		if (role === 'Tenant' || role === 'CoTenant') return 'tenant';
		return 'autofill';
	}

	function clampUnit(value: number) {
		return Math.min(1, Math.max(0, value));
	}

	function formatSigner(role: DocumentTemplateSignerRole) {
		switch (role) {
			case 'Tenant':
				return 'Tenant';
			case 'Landlord':
				return 'Landlord';
			case 'CoTenant':
				return 'Co-tenant';
			default:
				return 'Auto-fill';
		}
	}

	function isWhiteoutField(field: DocumentTemplateField) {
		return field.kind === 'Whiteout' || field.fieldKey === WHITEOUT_FIELD_KEY;
	}

	function fieldSignerLabel(field: DocumentTemplateField) {
		return isWhiteoutField(field) ? 'Whiteout' : formatSigner(field.signerRole);
	}

	function fieldTone(field: DocumentTemplateField) {
		if (isWhiteoutField(field)) {
			return 'border-slate-700/80 bg-white/85 text-slate-900 ring-1 ring-slate-400/40';
		}
		switch (field.signerRole) {
			case 'Tenant':
				return 'border-sky-500/80 bg-sky-500/15 text-sky-700 dark:text-sky-100';
			case 'Landlord':
				return 'border-emerald-500/80 bg-emerald-500/15 text-emerald-700 dark:text-emerald-100';
			case 'CoTenant':
				return 'border-amber-500/80 bg-amber-500/15 text-amber-700 dark:text-amber-100';
			default:
				return 'border-slate-500/80 bg-slate-500/15 text-slate-700 dark:text-slate-100';
		}
	}

	function switchView(nextMode: DesignerViewMode) {
		if (viewMode === nextMode) return;
		viewMode = nextMode;
		designerTool = 'place';
		selectedFieldId = null;
		cleanupActiveDrag?.();
		cleanupActiveDrag = null;
	}

	function refreshCurrentPdf() {
		loadedPdfKey = null;
		void templateQuery.refetch();
		if (viewMode === 'preview') {
			void previewLeasesQuery.refetch();
		}
	}

	function leasePreviewLabel(relationship: LeaseManagementSummary) {
		const place = [relationship.propertyName, relationship.unitNumber ? `Unit ${relationship.unitNumber}` : '']
			.filter(Boolean)
			.join(' · ');
		const tenant = relationship.primaryTenantName ? ` · ${relationship.primaryTenantName}` : '';
		return `${place || relationship.relationshipNumber}${tenant}`;
	}

	function canvasCursorClass() {
		if (viewMode === 'preview') return 'cursor-default';
		return 'cursor-crosshair';
	}

	function pageNumbers() {
		return Array.from({ length: pageCount }, (_, index) => index + 1);
	}

	onDestroy(() => {
		cleanupActiveDrag?.();
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
			<div class="flex flex-wrap items-center gap-2">
				<div
					class="inline-flex h-9 items-center gap-1.5 rounded-md border px-3 text-xs font-semibold {saveStatusClass}"
					data-testid="lease-template-save-status"
					title={saveErrorMessage ?? saveStatusLabel}
				>
					{#if isSaving}
						<Loader2 class="h-3.5 w-3.5 animate-spin" />
					{:else}
						<Save class="h-3.5 w-3.5" />
					{/if}
					<span>{saveStatusLabel}</span>
				</div>
				<Button
					variant="outline"
					size="sm"
					class="gap-1.5"
					onclick={refreshCurrentPdf}
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
					{#if viewMode === 'design'}
						<div class="rounded-lg border border-border bg-background p-3" data-testid="lease-template-field-palette">
							<div class="flex items-center justify-between gap-2 px-1">
								<div class="flex items-center gap-2">
									<PanelLeft class="h-4 w-4 text-primary" />
									<h3 class="text-sm font-semibold">Dynamic Fields</h3>
								</div>
								<Badge variant="secondary">{catalog.length}</Badge>
							</div>

							<Tabs.Root bind:value={activeCatalogTab} class="mt-3 gap-3">
								<Tabs.List class="grid w-full grid-cols-3 p-0.5">
									{#each catalogTabs as tab}
										<Tabs.Trigger value={tab.id} class="px-2 py-1.5 text-xs">
											{tab.label}
										</Tabs.Trigger>
									{/each}
								</Tabs.List>

								{#each catalogTabs as tab}
									<Tabs.Content value={tab.id} class="m-0">
										{#if tab.items.length === 0}
											<div class="rounded-md border border-dashed border-border bg-muted/20 p-4 text-sm text-muted-foreground">
												No fields in this group.
											</div>
										{:else}
											<div class="max-h-[560px] space-y-2 overflow-y-auto pr-1">
												{#each tab.items as item (item.fieldKey)}
													<button
														type="button"
														draggable={true}
														class="w-full rounded-md border px-3 py-2 text-left transition hover:bg-muted/35 active:translate-y-px {selectedCatalogKey === item.fieldKey ? 'border-primary bg-primary/8 ring-1 ring-primary/20' : 'border-border bg-background'} {draggingCatalogKey === item.fieldKey ? 'opacity-60' : ''}"
														onclick={() => selectCatalogItem(item)}
														ondragstart={(event) => startCatalogDrag(event, item)}
														ondragend={endCatalogDrag}
														data-testid="lease-template-field-palette-item-{item.fieldKey}"
													>
														<span class="flex items-start justify-between gap-2">
															<span class="min-w-0">
																<span class="block truncate text-sm font-semibold">{item.label}</span>
																<span class="mt-1 block line-clamp-2 text-xs leading-snug text-muted-foreground">
																	{item.description}
																</span>
															</span>
															<span class="shrink-0 space-y-1 text-right">
																<Badge variant="outline" class="text-[10px]">
																	{formatSigner(item.signerRole)}
																</Badge>
																{#if item.requiredForSignature}
																	<Badge variant="secondary" class="block text-[10px]">Required</Badge>
																{/if}
															</span>
														</span>
													</button>
												{/each}
											</div>
										{/if}
									</Tabs.Content>
								{/each}
							</Tabs.Root>

							{#if designerTool === 'whiteout'}
								<div class="mt-3 rounded-md border border-slate-400/40 bg-slate-100/70 px-3 py-2 text-slate-800 dark:bg-slate-900/40 dark:text-slate-100">
									<div class="flex items-center gap-2 text-xs font-semibold">
										<Eraser class="h-3.5 w-3.5" />
										Erase PDF text
									</div>
								</div>
							{:else if selectedCatalogItem}
								<div class="mt-3 rounded-md border border-primary/20 bg-primary/5 px-3 py-2">
									<div class="flex flex-wrap items-center gap-2">
										<Badge variant="outline">{formatSigner(selectedCatalogItem.signerRole)}</Badge>
										<Badge variant={selectedCatalogItem.requiredForSignature ? 'default' : 'secondary'}>
											{selectedCatalogItem.requiredForSignature ? 'Required' : selectedCatalogItem.kind}
										</Badge>
									</div>
									<p class="mt-2 truncate text-xs font-medium">{selectedCatalogItem.label}</p>
								</div>
							{/if}
						</div>
					{:else}
						<div class="rounded-lg border border-border bg-background p-4" data-testid="lease-template-preview-picker">
							<div class="flex items-center gap-2">
								<Eye class="h-4 w-4 text-primary" />
								<h3 class="text-sm font-semibold">Preview Lease</h3>
							</div>
							<label class="mt-4 block space-y-1 text-xs font-medium">
								<span class="text-muted-foreground">Fill fields from</span>
								<SimpleSelect
									value={String(previewLeaseId)}
									onchange={(value) => (previewLeaseId = Number(value))}
									options={previewLeases.map((relationship) => ({
										value: String(relationship.leaseAgreementId),
										label: `${leasePreviewLabel(relationship)} · ${relationship.agreementNumber}`
									}))}
									placeholder="Choose a lease"
									disabled={previewLeasesQuery.isLoading || previewLeases.length === 0}
									testid="lease-template-preview-lease-select"
								/>
							</label>

							{#if previewLeasesQuery.isLoading}
								<div class="mt-3 flex items-center gap-2 text-xs text-muted-foreground">
									<Loader2 class="h-3.5 w-3.5 animate-spin" />
									Loading leases
								</div>
							{:else if previewLeases.length === 0}
								<div class="mt-3 rounded-md border border-dashed border-border bg-muted/20 p-3 text-sm text-muted-foreground">
									No leases are available to preview.
								</div>
							{:else if selectedPreviewLease}
								<div class="mt-3 rounded-md border border-border bg-muted/20 px-3 py-2 text-xs">
									<p class="font-semibold">{selectedPreviewLease.propertyName ?? 'Property'}{selectedPreviewLease.unitNumber ? ` · Unit ${selectedPreviewLease.unitNumber}` : ''}</p>
									<p class="mt-1 text-muted-foreground">{selectedPreviewLease.primaryTenantName ?? 'Tenant'} · {selectedPreviewLease.agreementNumber ?? 'Agreement draft'}</p>
								</div>
							{/if}

							<Button
								variant="outline"
								size="sm"
								class="mt-3 w-full gap-1.5"
								onclick={() => {
									loadedPdfKey = null;
									void previewLeasesQuery.refetch();
								}}
								disabled={pageRendering || previewLeasesQuery.isFetching}
								data-testid="lease-template-preview-refresh"
							>
								<RefreshCw class="h-4 w-4 {previewLeasesQuery.isFetching ? 'animate-spin' : ''}" />
								Refresh preview
							</Button>
						</div>
					{/if}
				</aside>

				<section class="min-w-0">
					<div class="rounded-lg border border-border bg-muted/25 p-3" data-testid="lease-template-pdf-preview">
						<div class="mb-3 flex flex-wrap items-center justify-between gap-2">
							<div class="flex flex-wrap items-center gap-2">
								<div class="inline-flex items-center gap-2 text-sm font-semibold">
									<FileText class="h-4 w-4 text-primary" />
									<span>{viewMode === 'preview' ? 'Filled Preview' : 'PDF Preview'}</span>
									<Badge variant="outline">Page {selectedPage}</Badge>
								</div>
								<div class="inline-flex rounded-md border border-border bg-background p-0.5">
									<button
										type="button"
										class="inline-flex h-8 items-center gap-1.5 rounded px-3 text-xs font-semibold transition {viewMode === 'design' ? 'bg-primary text-primary-foreground shadow-sm' : 'text-muted-foreground hover:text-foreground'}"
										onclick={() => switchView('design')}
										data-testid="lease-template-design-tab"
									>
										<MousePointer2 class="h-3.5 w-3.5" />
										Design
									</button>
									<button
										type="button"
										class="inline-flex h-8 items-center gap-1.5 rounded px-3 text-xs font-semibold transition {viewMode === 'preview' ? 'bg-primary text-primary-foreground shadow-sm' : 'text-muted-foreground hover:text-foreground'}"
										onclick={() => switchView('preview')}
										data-testid="lease-template-preview-tab"
									>
										<Eye class="h-3.5 w-3.5" />
										Preview
									</button>
								</div>
								{#if viewMode === 'design'}
									<div class="inline-flex rounded-md border border-border bg-background p-0.5">
										<button
											type="button"
											class="inline-flex h-8 items-center gap-1.5 rounded px-3 text-xs font-semibold transition {designerTool === 'place' ? 'bg-secondary text-secondary-foreground shadow-sm' : 'text-muted-foreground hover:text-foreground'}"
											onclick={() => (designerTool = 'place')}
											data-testid="lease-template-place-tool"
										>
											<MousePointer2 class="h-3.5 w-3.5" />
											Place
										</button>
										<button
											type="button"
											class="inline-flex h-8 items-center gap-1.5 rounded px-3 text-xs font-semibold transition {designerTool === 'whiteout' ? 'bg-secondary text-secondary-foreground shadow-sm' : 'text-muted-foreground hover:text-foreground'}"
											onclick={() => (designerTool = 'whiteout')}
											data-testid="lease-template-erase-tool"
										>
											<Eraser class="h-3.5 w-3.5" />
											Erase
										</button>
									</div>
								{/if}
							</div>
							<div class="flex items-center gap-1">
								<Button
									variant={zoomMode === 'fit' ? 'default' : 'outline'}
									size="sm"
									class="h-8 gap-1.5"
									onclick={fitZoom}
									data-testid="lease-template-zoom-fit"
								>
									<Maximize2 class="h-3.5 w-3.5" />
									Fit
								</Button>
								<Button
									variant="outline"
									size="icon"
									class="h-8 w-8"
									onclick={() => adjustZoom(-10)}
									aria-label="Zoom out"
									data-testid="lease-template-zoom-out"
								>
									<ZoomOut class="h-4 w-4" />
								</Button>
								<div
									class="grid h-8 min-w-16 place-items-center rounded-md border border-border bg-background px-2 text-xs font-semibold"
									data-testid="lease-template-zoom-label"
								>
									{zoomMode === 'fit' ? `Fit ${renderedZoomPct}%` : `${zoomPct}%`}
								</div>
								<Button
									variant="outline"
									size="icon"
									class="h-8 w-8"
									onclick={() => adjustZoom(10)}
									aria-label="Zoom in"
									data-testid="lease-template-zoom-in"
								>
									<ZoomIn class="h-4 w-4" />
								</Button>
							</div>
						</div>
						{#if pdfError}
							<div class="rounded-md border border-destructive/30 bg-destructive/5 p-4 text-sm text-destructive">
								{pdfError}
							</div>
						{/if}
						<div class="overflow-auto rounded-md bg-slate-200/70 p-4">
							<div use:registerPageFrame class="mx-auto max-w-full">
								<div
									class="relative mx-auto inline-block touch-none rounded-sm bg-white shadow-md ring-1 ring-slate-300"
									role="region"
									aria-label={viewMode === 'preview' ? 'PDF filled preview area' : 'PDF field placement area'}
									ondragover={handlePdfDragOver}
									ondrop={handlePdfDrop}
								>
									<canvas
										bind:this={canvasEl}
										class="block bg-white {canvasCursorClass()}"
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
									{#if viewMode === 'design'}
										{#each pageFields as field (field.id)}
											<button
												type="button"
												style={markerStyle(field)}
												class="absolute z-[1] flex min-h-5 items-center gap-1 rounded border px-1.5 text-left text-[11px] font-semibold leading-tight shadow-sm backdrop-blur-sm transition {fieldTone(field)} {selectedFieldId === field.id ? 'ring-2 ring-primary ring-offset-1' : ''}"
												data-template-field-marker
												data-testid={field.testId}
												title={`${field.label} · ${fieldSignerLabel(field)}`}
												onpointerdown={(event) => handleFieldPointerDown(event, field)}
											>
												{#if isWhiteoutField(field)}
													<Eraser class="h-3 w-3 shrink-0" />
												{:else}
													<GripHorizontal class="h-3 w-3 shrink-0" />
												{/if}
												<span class="min-w-0 truncate">{field.label}</span>
												{#if selectedFieldId === field.id && !field.locked}
													<span
														class="absolute -bottom-1 -right-1 h-3.5 w-3.5 cursor-nwse-resize rounded-full border border-background bg-primary shadow-sm ring-1 ring-primary/40"
														onpointerdown={(event) => startFieldResize(event, field, 'southEast')}
														aria-hidden="true"
													></span>
												{/if}
											</button>
										{/each}
									{/if}
								</div>
							</div>
						</div>
					</div>
				</section>

				<aside class="space-y-4">
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
							<SimpleSelect
								value={String(selectedPage)}
								onchange={(value) => (selectedPage = Number(value))}
								options={pageNumbers().map((page) => ({ value: String(page), label: `Page ${page}` }))}
								triggerClass="h-9 flex-1"
								testid="lease-template-page-select"
							/>
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
											<Badge variant="outline" class="text-[10px]">{fieldSignerLabel(field)}</Badge>
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
										<p class="font-medium">{fieldSignerLabel(selectedField)}</p>
									</div>
									<div class="rounded-md bg-muted/30 px-2 py-2">
										<p class="text-muted-foreground">X / Y</p>
										<p class="font-medium">
											{Math.round(getFieldRect(selectedField).xPct * 100)}% /
											{Math.round(getFieldRect(selectedField).yPct * 100)}%
										</p>
									</div>
									<div class="rounded-md bg-muted/30 px-2 py-2">
										<p class="text-muted-foreground">Size</p>
										<p class="font-medium">
											{Math.round(getFieldRect(selectedField).widthPct * 100)}% × {Math.round(getFieldRect(selectedField).heightPct * 100)}%
										</p>
									</div>
								</div>
								<div class="space-y-3 rounded-md border border-border bg-muted/15 p-3">
										<div class="grid grid-cols-2 gap-2">
											<label class="space-y-1 text-xs font-medium">
												<span class="text-muted-foreground">Width %</span>
												<input
													type="number"
													min={MIN_FIELD_DIMENSION_PERCENT}
													max={MAX_FIELD_DIMENSION_PERCENT}
													step="1"
													value={Math.round(getFieldRect(selectedField).widthPct * 100)}
													class="h-9 w-full rounded-md border border-input bg-background px-2 text-sm"
													disabled={selectedField.locked || moveFieldMutation.isPending}
													aria-invalid={fieldDimensionError?.fieldId === selectedField.id && fieldDimensionError.dimension === 'widthPct'}
													onchange={(event) => setSelectedFieldDimension(selectedField, 'widthPct', event)}
													data-testid="lease-template-field-width"
												/>
												{#if fieldDimensionError?.fieldId === selectedField.id && fieldDimensionError.dimension === 'widthPct'}
													<p class="text-xs font-normal text-destructive" data-testid="lease-template-field-width-error">{fieldDimensionError.message}</p>
												{/if}
											</label>
											<label class="space-y-1 text-xs font-medium">
												<span class="text-muted-foreground">Height %</span>
												<input
													type="number"
													min={MIN_FIELD_DIMENSION_PERCENT}
													max={MAX_FIELD_DIMENSION_PERCENT}
													step="1"
													value={Math.round(getFieldRect(selectedField).heightPct * 100)}
													class="h-9 w-full rounded-md border border-input bg-background px-2 text-sm"
													disabled={selectedField.locked || moveFieldMutation.isPending}
													aria-invalid={fieldDimensionError?.fieldId === selectedField.id && fieldDimensionError.dimension === 'heightPct'}
													onchange={(event) => setSelectedFieldDimension(selectedField, 'heightPct', event)}
													data-testid="lease-template-field-height"
												/>
												{#if fieldDimensionError?.fieldId === selectedField.id && fieldDimensionError.dimension === 'heightPct'}
													<p class="text-xs font-normal text-destructive" data-testid="lease-template-field-height-error">{fieldDimensionError.message}</p>
												{/if}
											</label>
										</div>
									<div class="grid grid-cols-2 gap-2">
										<Button
											variant="outline"
											size="sm"
											class="gap-1.5"
											disabled={selectedField.locked || moveFieldMutation.isPending}
											onclick={() => adjustSelectedFieldSize(0.03, 0)}
											data-testid="lease-template-field-wider"
										>
											<ArrowLeftRight class="h-3.5 w-3.5" />
											Wider
										</Button>
										<Button
											variant="outline"
											size="sm"
											class="gap-1.5"
											disabled={selectedField.locked || moveFieldMutation.isPending}
											onclick={() => adjustSelectedFieldSize(-0.03, 0)}
											data-testid="lease-template-field-narrower"
										>
											<ArrowLeftRight class="h-3.5 w-3.5" />
											Narrower
										</Button>
										<Button
											variant="outline"
											size="sm"
											class="gap-1.5"
											disabled={selectedField.locked || moveFieldMutation.isPending}
											onclick={() => adjustSelectedFieldSize(0, 0.015)}
											data-testid="lease-template-field-taller"
										>
											<ArrowUpDown class="h-3.5 w-3.5" />
											Taller
										</Button>
										<Button
											variant="outline"
											size="sm"
											class="gap-1.5"
											disabled={selectedField.locked || moveFieldMutation.isPending}
											onclick={() => adjustSelectedFieldSize(0, -0.015)}
											data-testid="lease-template-field-shorter"
										>
											<ArrowUpDown class="h-3.5 w-3.5" />
											Shorter
										</Button>
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
