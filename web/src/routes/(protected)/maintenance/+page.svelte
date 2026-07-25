<script lang="ts">
	import { createMutation, createQuery, useQueryClient } from '@tanstack/svelte-query';
	import { goto } from '$app/navigation';
	import { workOrders } from '$lib/api/endpoints/workOrders';
	import { inspections } from '$lib/api/endpoints/inspections';
	import { properties } from '$lib/api/endpoints/properties';
	import { units } from '$lib/api/endpoints/units';
	import { tenants } from '$lib/api/endpoints/tenants';
	import { vendors } from '$lib/api/endpoints/vendors';
	import type { InspectionTemplate, InspectionTemplateInput, InspectionType, WorkOrder } from '$lib/types';
	import { getCurrentPortfolioId } from '$lib/stores/portfolio.svelte';
	import { recordHref } from '$lib/navigation/record-href';
	import { workOrderSchema, inspectionSchema, parseForm } from '$lib/schemas';
	import { localInputToOffsetIso } from '$lib/utils/date';
	import { showSuccess, showError, apiErrorMessage } from '$lib/utils/toast';
	import { debounced } from '$lib/utils/debounce.svelte';
	import * as Dialog from '$lib/components/ui/dialog';
	import ConfirmDialog from '$lib/components/shared/ConfirmDialog.svelte';
	import FormStepper, { type FormStepperStep } from '$lib/components/shared/FormStepper.svelte';
	import StepperNextButton from '$lib/components/shared/StepperNextButton.svelte';
	import SearchInput from '$lib/components/shared/SearchInput.svelte';
	import RemoteRecordSelect from '$lib/components/shared/RemoteRecordSelect.svelte';
	import RangeDatePicker from '$lib/components/shared/RangeDatePicker.svelte';
	import StatusBadge from '$lib/components/shared/StatusBadge.svelte';
	import DateTimePicker from '$lib/components/shared/DateTimePicker.svelte';
	import { clearFieldError } from '$lib/forms/form-errors';
	import { DataGrid } from '$lib/components/data-grid';
	import type { ColumnDef } from '$lib/components/data-grid/types';
	import { Copy, Pencil, Plus, RefreshCw, ShieldCheck, Trash2 } from '@lucide/svelte';
	import { Button } from '$lib/components/ui/button';
	import { Input } from '$lib/components/ui/input';
	import * as Select from '$lib/components/ui/select';
	import * as Card from '$lib/components/ui/card';
	import { page } from '$app/state';
	import { readGridParam, syncGridUrl } from '$lib/utils/grid-url-state.svelte';
	import PageHeader from '$lib/components/m3/PageHeader.svelte';

	const queryClient = useQueryClient();
	const portfolioId = $derived(getCurrentPortfolioId());
	const PAGE_SIZE = 20;
	const WO_STATUSES = ['New', 'Scheduled', 'InProgress', 'WaitingParts', 'Completed', 'Cancelled'];
	const WO_PRIORITIES = ['Low', 'Normal', 'High', 'Emergency'];
	const INSPECTION_TYPES: InspectionType[] = ['Routine', 'MoveIn', 'MoveOut', 'AnnualSafety'];

	// Work-order search / status / priority / sort / page persisted in the URL so they survive navigating
	// away and back. The main work-order grid is server-side so these controls feed the SQL query instead
	// of filtering a capped list in the browser.
	const initialParams = page.url.searchParams;
	let woSearch = $state(readGridParam(initialParams, 'q'));
	let woStatusFilter = $state(readGridParam(initialParams, 'status'));
	let woPriorityFilter = $state(readGridParam(initialParams, 'priority'));
	let woFrom = $state(readGridParam(initialParams, 'from'));
	let woTo = $state(readGridParam(initialParams, 'to'));
	let gridSort = $state(readGridParam(initialParams, 'sort'));
	let gridPage = $state(readGridParam(initialParams, 'page', 1));
	const debouncedWoSearch = debounced(() => woSearch, 300);

	// Reset to page 1 when a filter/search changes — but not on initial mount.
	let filterResetPrimed = false;
	$effect(() => {
		woSearch;
		woStatusFilter;
		woPriorityFilter;
		woFrom;
		woTo;
		if (!filterResetPrimed) {
			filterResetPrimed = true;
			return;
		}
		gridPage = 1;
	});

	$effect(() => {
		syncGridUrl(
			{ q: woSearch, status: woStatusFilter, priority: woPriorityFilter, from: woFrom, to: woTo, sort: gridSort, page: gridPage },
			{ page: 1 }
		);
	});

	const workOrdersQuery = createQuery(() => ({
		queryKey: [
			'work-orders',
			portfolioId,
			'page',
			debouncedWoSearch.value,
			woStatusFilter,
			woPriorityFilter,
			woFrom,
			woTo,
			gridSort,
			gridPage,
			PAGE_SIZE,
		],
		queryFn: () => workOrders.listPage(portfolioId, {
			search: debouncedWoSearch.value,
			status: woStatusFilter || undefined,
			priority: woPriorityFilter || undefined,
			// The work-order grid date range filters on RequestedAt (when the request came in).
			requestedFrom: woFrom || undefined,
			requestedTo: woTo || undefined,
			sort: gridSort || undefined,
			skip: (gridPage - 1) * PAGE_SIZE,
			take: PAGE_SIZE,
		}),
	}));
	const inspectionsQuery = createQuery(() => ({ queryKey: ['inspections', portfolioId], queryFn: () => inspections.list(portfolioId) }));
	async function loadPropertyOptions(params: { search?: string; skip: number; take: number }) {
		const result = await properties.listPage(portfolioId, { ...params, sort: 'name' });
		return {
			...result,
			items: result.items.map((property) => ({
				id: property.id,
				label: property.name,
				description: `${property.addressLine1}, ${property.city}, ${property.state}`
			}))
		};
	}
	async function loadTenantOptions(params: { search?: string; skip: number; take: number }) {
		const result = await tenants.listPage(portfolioId, { ...params, sort: 'lastName' });
		return {
			...result,
			items: result.items.map((tenant) => ({
				id: tenant.id,
				label: tenant.fullName || `${tenant.firstName} ${tenant.lastName}`,
				description: tenant.email || tenant.phone || null
			}))
		};
	}
	async function loadVendorOptions(params: { search?: string; skip: number; take: number }) {
		const result = await vendors.listPage(portfolioId, { ...params, sort: 'name' });
		return {
			...result,
			items: result.items.map((vendor) => ({
				id: vendor.id,
				label: vendor.name,
				description: vendor.serviceType || vendor.email || vendor.phone || null
			}))
		};
	}

	// --- Work order form/dialog ---
	const emptyWo = {
		propertyId: '',
		title: '',
		description: '',
		technicianAccessInstructions: '',
		priority: 'Normal',
		category: 'General',
		unitId: '',
		tenantId: '',
		vendorId: '',
		scheduledFor: '',
		scheduledWindowEnd: '',
		estimatedCost: '',
	};
	let showWoForm = $state(false);
	let editingWoId = $state<number | null>(null);
	let woForm = $state({ ...emptyWo });
	let woErrors = $state<Record<string, string>>({});
	let woStep = $state(0);
	let completedWoSteps = $state<number[]>([]);
	let woDeleteTarget = $state<WorkOrder | null>(null);
	let selectedWoPropertyLabel = $state<string | null>(null);
	let selectedWoUnitLabel = $state<string | null>(null);
	let selectedWoTenantLabel = $state<string | null>(null);
	let selectedWoVendorLabel = $state<string | null>(null);

	const woSteps: FormStepperStep[] = [
		{ id: 'issue', label: 'Issue', description: 'Title and details' },
		{ id: 'triage', label: 'Triage', description: 'Priority and type' },
		{ id: 'location', label: 'Location', description: 'Property and unit' },
		{ id: 'schedule', label: 'Schedule', description: 'Visit window and access' },
		{ id: 'people', label: 'People', description: 'Tenant and vendor' },
		{ id: 'budget', label: 'Budget', description: 'Estimated cost' },
	];
	const woStepFields = [
		['title', 'description'],
		['priority', 'category'],
		['propertyId', 'unitId'],
		['scheduledFor', 'scheduledWindowEnd', 'technicianAccessInstructions'],
		['tenantId', 'vendorId'],
		['estimatedCost'],
	] as const;

	function clearWoError(field: string) {
		const next = clearFieldError(woErrors, field);
		if (next !== woErrors) woErrors = next;
	}

	$effect(() => {
		if (woForm.propertyId) clearWoError('propertyId');
	});
	$effect(() => {
		if (woForm.title.trim()) clearWoError('title');
	});
	$effect(() => {
		if (woForm.description.trim()) clearWoError('description');
	});
	$effect(() => {
		if (woForm.category.trim()) clearWoError('category');
	});
	$effect(() => {
		if (woForm.scheduledFor) clearWoError('scheduledFor');
	});
	$effect(() => {
		if (!woForm.scheduledWindowEnd || !woForm.scheduledFor) return;
		const start = new Date(woForm.scheduledFor).getTime();
		const end = new Date(woForm.scheduledWindowEnd).getTime();
		if (!isNaN(start) && !isNaN(end) && end > start) clearWoError('scheduledWindowEnd');
	});
	$effect(() => {
		if (woForm.estimatedCost) clearWoError('estimatedCost');
	});

	// Optional work-order context. Tenants/vendors load only while the form is open; units are
	// fetched per selected property so the Unit dropdown only offers units of that property.
	const woPropertyId = $derived(woForm.propertyId ? Number(woForm.propertyId) : null);
	async function loadWoUnitOptions(params: { search?: string; skip: number; take: number }) {
		const result = await units.listWithHealthPage({
			...params,
			propertyId: woPropertyId ?? undefined,
			sort: 'unitNumber'
		});
		return {
			...result,
			items: result.items.map((unit) => ({
				id: unit.id,
				label: `Unit ${unit.unitNumber}`,
				description: unit.status
			}))
		};
	}

	function invalidateWo() {
		queryClient.invalidateQueries({ queryKey: ['work-orders', portfolioId] });
	}

	const saveWoMutation = createMutation(() => ({
		mutationFn: ({ id, data }: { id: number | null; data: Record<string, unknown> }) =>
			id == null ? workOrders.create(data) : workOrders.update(id, data),
		onSuccess: (_r, vars) => {
			showSuccess(vars.id == null ? 'Work order created.' : 'Work order updated.');
			closeWoForm();
			invalidateWo();
		},
		onError: (err) => showError(apiErrorMessage(err)),
	}));

	const deleteWoMutation = createMutation(() => ({
		mutationFn: (id: number) => workOrders.delete(id),
		onSuccess: () => {
			showSuccess('Work order deleted.');
			woDeleteTarget = null;
			invalidateWo();
		},
		onError: (err) => showError(apiErrorMessage(err)),
	}));

	function openCreateWo() {
		editingWoId = null;
		woForm = { ...emptyWo };
		woErrors = {};
		woStep = 0;
		completedWoSteps = [];
		selectedWoPropertyLabel = null;
		selectedWoUnitLabel = null;
		selectedWoTenantLabel = null;
		selectedWoVendorLabel = null;
		showWoForm = true;
	}
	function openEditWo(wo: WorkOrder) {
		editingWoId = wo.id;
		woForm = {
			propertyId: String(wo.propertyId),
			title: wo.title,
			description: wo.description,
			technicianAccessInstructions: wo.technicianAccessInstructions ?? '',
			priority: wo.priority,
			category: wo.category,
			unitId: wo.unitId != null ? String(wo.unitId) : '',
			tenantId: wo.tenantId != null ? String(wo.tenantId) : '',
			vendorId: wo.vendorId != null ? String(wo.vendorId) : '',
			// `datetime-local` wants `yyyy-MM-ddTHH:mm`; slice the ISO timestamp (same as appointments).
			scheduledFor: wo.scheduledFor?.slice(0, 16) ?? '',
			scheduledWindowEnd: wo.scheduledWindowEnd?.slice(0, 16) ?? '',
			estimatedCost: wo.estimatedCost != null ? String(wo.estimatedCost) : '',
		};
		woErrors = {};
		woStep = 0;
		completedWoSteps = [];
		selectedWoPropertyLabel = wo.propertyName ?? null;
		selectedWoUnitLabel = wo.unitNumber ? `Unit ${wo.unitNumber}` : null;
		selectedWoTenantLabel = wo.tenantName ?? null;
		selectedWoVendorLabel = wo.vendorName ?? null;
		showWoForm = true;
	}
	function closeWoForm() {
		showWoForm = false;
		editingWoId = null;
		woErrors = {};
		woStep = 0;
		completedWoSteps = [];
		selectedWoPropertyLabel = null;
		selectedWoUnitLabel = null;
		selectedWoTenantLabel = null;
		selectedWoVendorLabel = null;
	}
	function workOrderWindowErrors(): Record<string, string> {
		if (woForm.scheduledFor && woForm.scheduledWindowEnd) {
			const start = new Date(woForm.scheduledFor).getTime();
			const end = new Date(woForm.scheduledWindowEnd).getTime();
			if (!isNaN(start) && !isNaN(end) && end <= start) {
				return { scheduledWindowEnd: 'Window end must be after the start time.' };
			}
		}
		return {};
	}
	function woStepErrorFields(step: number, errors: Record<string, string>) {
		const visibleFields = new Set<string>(woStepFields[step] ?? []);
		return Object.entries(errors).filter(([field]) => visibleFields.has(field));
	}
	function firstWoErrorStep(errors: Record<string, string>) {
		return woStepFields.findIndex((fields) => fields.some((field) => errors[field]));
	}
	function markWoStepInvalid(step: number) {
		completedWoSteps = completedWoSteps.filter((completedStep) => completedStep < step);
	}
	function validateWoStep(step: number) {
		const result = parseForm(workOrderSchema, woForm);
		const allErrors = { ...(result.errors ?? {}), ...workOrderWindowErrors() };
		const currentErrors = Object.fromEntries(woStepErrorFields(step, allErrors));
		const currentFields = new Set<string>(woStepFields[step] ?? []);
		const nextErrors = Object.fromEntries(Object.entries(woErrors).filter(([field]) => !currentFields.has(field)));
		woErrors = { ...nextErrors, ...currentErrors };
		const isValid = Object.keys(currentErrors).length === 0;
		if (!isValid) markWoStepInvalid(step);
		return isValid;
	}
	function nextWoStep() {
		if (!validateWoStep(woStep)) return;
		if (completedWoSteps.includes(woStep)) {
			woStep = Math.min(woStep + 1, woSteps.length - 1);
			return;
		}
		completedWoSteps = [...completedWoSteps, woStep];
		window.setTimeout(() => {
			woStep = Math.min(woStep + 1, woSteps.length - 1);
		}, 260);
	}
	function submitWo() {
		const result = parseForm(workOrderSchema, woForm);
		const errors = { ...(result.errors ?? {}), ...workOrderWindowErrors() };
		if (Object.keys(errors).length > 0) {
			woErrors = errors;
			const firstErrorStep = firstWoErrorStep(errors);
			if (firstErrorStep >= 0) {
				woStep = firstErrorStep;
				markWoStepInvalid(firstErrorStep);
			}
			return;
		}
		if (!result.data) return;
		woErrors = {};
		// FROZEN contract: send the schedule as ISO-8601 with the browser's local offset so the
		// server stores the true instant (and the tenant SMS shows the landlord's wall-clock time),
		// rather than the un-zoned datetime-local string the API would mislabel as UTC.
		const data: Record<string, unknown> = { portfolioId, ...result.data };
		data.scheduledFor = localInputToOffsetIso(woForm.scheduledFor);
		data.scheduledWindowEnd = localInputToOffsetIso(woForm.scheduledWindowEnd);
		saveWoMutation.mutate({ id: editingWoId, data });
	}

	// --- Inspection form/dialog ---
	const emptyInspection = { propertyId: '', type: 'Routine', scheduledFor: '', templateId: '', inspector: '' };
	let showInspectionForm = $state(false);
	let inspectionForm = $state({ ...emptyInspection });
	let inspectionErrors = $state<Record<string, string>>({});
	let inspectionStep = $state(0);
	let completedInspectionSteps = $state<number[]>([]);
	let selectedInspectionPropertyLabel = $state<string | null>(null);
	const inspectionSteps: FormStepperStep[] = [
		{ id: 'where', label: 'Where', description: 'Property and type' },
		{ id: 'checklist', label: 'Checklist', description: 'Template and inspector' },
		{ id: 'schedule', label: 'Schedule', description: 'Date and time' },
	];
	const inspectionStepFields = [
		['propertyId', 'type'],
		['templateId', 'inspector'],
		['scheduledFor'],
	] as const;

	function clearInspectionError(field: string) {
		const next = clearFieldError(inspectionErrors, field);
		if (next !== inspectionErrors) inspectionErrors = next;
	}

	$effect(() => {
		if (inspectionForm.propertyId) clearInspectionError('propertyId');
	});
	$effect(() => {
		if (inspectionForm.scheduledFor) clearInspectionError('scheduledFor');
	});

	function openInspectionForm() {
		inspectionForm = { ...emptyInspection };
		inspectionErrors = {};
		inspectionStep = 0;
		completedInspectionSteps = [];
		selectedInspectionPropertyLabel = null;
		showInspectionForm = true;
	}

	function closeInspectionForm() {
		showInspectionForm = false;
		inspectionForm = { ...emptyInspection };
		inspectionErrors = {};
		inspectionStep = 0;
		completedInspectionSteps = [];
		selectedInspectionPropertyLabel = null;
	}

	function inspectionStepErrorFields(step: number, errors: Record<string, string>) {
		const visibleFields = new Set<string>(inspectionStepFields[step] ?? []);
		return Object.entries(errors).filter(([field]) => visibleFields.has(field));
	}

	function firstInspectionErrorStep(errors: Record<string, string>) {
		return inspectionStepFields.findIndex((fields) => fields.some((field) => errors[field]));
	}

	function markInspectionStepInvalid(step: number) {
		completedInspectionSteps = completedInspectionSteps.filter((completedStep) => completedStep < step);
	}

	function validateInspectionStep(step: number) {
		const result = parseForm(inspectionSchema, inspectionForm);
		const currentErrors = result.errors ? Object.fromEntries(inspectionStepErrorFields(step, result.errors)) : {};
		const currentFields = new Set<string>(inspectionStepFields[step] ?? []);
		const nextErrors = Object.fromEntries(Object.entries(inspectionErrors).filter(([field]) => !currentFields.has(field)));
		inspectionErrors = { ...nextErrors, ...currentErrors };
		const isValid = Object.keys(currentErrors).length === 0;
		if (!isValid) markInspectionStepInvalid(step);
		return isValid;
	}

	function nextInspectionStep() {
		if (!validateInspectionStep(inspectionStep)) return;
		if (completedInspectionSteps.includes(inspectionStep)) {
			inspectionStep = Math.min(inspectionStep + 1, inspectionSteps.length - 1);
			return;
		}
		completedInspectionSteps = [...completedInspectionSteps, inspectionStep];
		window.setTimeout(() => {
			inspectionStep = Math.min(inspectionStep + 1, inspectionSteps.length - 1);
		}, 260);
	}

	// Smart-checklist templates (built-ins have negative ids). Custom templates are editable below.
	const templatesQuery = createQuery(() => ({
		queryKey: ['inspection-templates', portfolioId],
		queryFn: () => inspections.templates(),
	}));
	const templateOptions = $derived(templatesQuery.data ?? []);
	const selectedTemplateName = $derived(
		templateOptions.find((t) => String(t.id) === inspectionForm.templateId)?.name ?? 'No checklist (blank)'
	);

	type TemplateFormItem = {
		key: string;
		area: string;
		label: string;
	};
	type TemplateForm = {
		name: string;
		inspectionType: InspectionType;
		items: TemplateFormItem[];
	};

	let nextTemplateItemKey = 0;
	function newTemplateItem(area = '', label = ''): TemplateFormItem {
		nextTemplateItemKey += 1;
		return { key: `template-item-${nextTemplateItemKey}`, area, label };
	}

	function emptyTemplateForm(): TemplateForm {
		return {
			name: '',
			inspectionType: 'Routine',
			items: [newTemplateItem()],
		};
	}

	let showTemplateEditor = $state(false);
	let editingTemplateId = $state<number | null>(null);
	let templateForm = $state<TemplateForm>(emptyTemplateForm());
	let templateErrors = $state<Record<string, string>>({});
	let templateDeleteTarget = $state<InspectionTemplate | null>(null);

	function invalidateTemplates() {
		queryClient.invalidateQueries({ queryKey: ['inspection-templates', portfolioId] });
	}

	function closeTemplateEditor() {
		showTemplateEditor = false;
		editingTemplateId = null;
		templateForm = emptyTemplateForm();
		templateErrors = {};
	}

	function openNewTemplate() {
		editingTemplateId = null;
		templateForm = emptyTemplateForm();
		templateErrors = {};
		showTemplateEditor = true;
	}

	function openEditTemplate(template: InspectionTemplate) {
		if (template.isBuiltIn) {
			openCopyTemplate(template);
			return;
		}
		editingTemplateId = template.id;
		templateForm = {
			name: template.name,
			inspectionType: template.inspectionType,
			items: template.items.map((item) => newTemplateItem(item.area, item.label)),
		};
		templateErrors = {};
		showTemplateEditor = true;
	}

	function openCopyTemplate(template: InspectionTemplate) {
		editingTemplateId = null;
		templateForm = {
			name: `${template.name} copy`,
			inspectionType: template.inspectionType,
			items: template.items.length > 0
				? template.items.map((item) => newTemplateItem(item.area, item.label))
				: [newTemplateItem()],
		};
		templateErrors = {};
		showTemplateEditor = true;
	}

	function addTemplateQuestion() {
		templateForm.items = [...templateForm.items, newTemplateItem()];
		templateErrors = {};
	}

	function removeTemplateQuestion(index: number) {
		templateForm.items =
			templateForm.items.length === 1
				? [newTemplateItem()]
				: templateForm.items.filter((_, i) => i !== index);
		templateErrors = {};
	}

	function buildTemplatePayload(): InspectionTemplateInput | null {
		const errors: Record<string, string> = {};
		const name = templateForm.name.trim();
		if (!name) {
			errors.name = 'Checklist name is required.';
		}
		if (name.length > 200) {
			errors.name = 'Checklist name must be 200 characters or fewer.';
		}

		const items = templateForm.items.map((item, index) => {
			const area = item.area.trim();
			const label = item.label.trim();
			if (!area) {
				errors[`items.${index}.area`] = 'Area is required.';
			}
			if (area.length > 120) {
				errors[`items.${index}.area`] = 'Area must be 120 characters or fewer.';
			}
			if (!label) {
				errors[`items.${index}.label`] = 'Question is required.';
			}
			if (label.length > 300) {
				errors[`items.${index}.label`] = 'Question must be 300 characters or fewer.';
			}
			return { area, label };
		});

		if (items.length === 0) {
			errors.items = 'Add at least one checklist question.';
		}
		if (items.length > 100) {
			errors.items = 'A checklist can have at most 100 questions.';
		}

		templateErrors = errors;
		if (Object.keys(errors).length > 0) {
			return null;
		}

		return {
			name,
			inspectionType: templateForm.inspectionType,
			items,
		};
	}

	const saveTemplateMutation = createMutation(() => ({
		mutationFn: ({ id, data }: { id: number | null; data: InspectionTemplateInput }) =>
			id == null ? inspections.createTemplate(data) : inspections.updateTemplate(id, data),
		onSuccess: (template, vars) => {
			showSuccess(vars.id == null ? 'Checklist saved.' : 'Checklist updated.');
			closeTemplateEditor();
			invalidateTemplates();
			if (vars.id == null) {
				inspectionForm.templateId = String(template.id);
				inspectionForm.type = template.inspectionType;
			}
		},
		onError: (err) => showError(apiErrorMessage(err, 'Could not save checklist.')),
	}));

	const deleteTemplateMutation = createMutation(() => ({
		mutationFn: (id: number) => inspections.deleteTemplate(id),
		onSuccess: (_r, id) => {
			showSuccess('Checklist deleted.');
			if (inspectionForm.templateId === String(id)) {
				inspectionForm.templateId = '';
			}
			templateDeleteTarget = null;
			invalidateTemplates();
		},
		onError: (err) => showError(apiErrorMessage(err, 'Could not delete checklist.')),
	}));

	function submitTemplate() {
		const payload = buildTemplatePayload();
		if (!payload) return;
		saveTemplateMutation.mutate({ id: editingTemplateId, data: payload });
	}

	const createInspectionMutation = createMutation(() => ({
		mutationFn: (data: Record<string, unknown>) => inspections.create(data),
		onSuccess: (created) => {
			showSuccess('Inspection scheduled.');
			closeInspectionForm();
			queryClient.invalidateQueries({ queryKey: ['inspections', portfolioId] });
			// Jump straight into the checklist so Maria can start ticking items.
			goto('/maintenance/inspections/' + created.id);
		},
		onError: (err) => showError(apiErrorMessage(err)),
	}));

	function submitInspection() {
		const result = parseForm(inspectionSchema, inspectionForm);
		if (result.errors) {
			inspectionErrors = result.errors;
			const firstErrorStep = firstInspectionErrorStep(result.errors);
			if (firstErrorStep >= 0) {
				inspectionStep = firstErrorStep;
				markInspectionStepInvalid(firstErrorStep);
			}
			return;
		}
		inspectionErrors = {};
		createInspectionMutation.mutate({
			portfolioId,
			...result.data,
			scheduledFor: localInputToOffsetIso(inspectionForm.scheduledFor),
		});
	}

	const woList = $derived(workOrdersQuery.data?.items ?? []);
	const woTotalCount = $derived(workOrdersQuery.data?.totalCount ?? 0);

	// DataGrid column definitions — cell snippets referenced below in template
	const woColumns: ColumnDef<WorkOrder>[] = [
		{
			key: 'title',
			title: 'Title',
			sortable: true,
			mobileRole: 'title',
			cell: titleCellSnippet,
		},
		{
			key: 'propertyName',
			title: 'Property',
			sortable: true,
			mobileRole: 'subtitle',
			accessor: (wo) => wo.propertyName ?? '—',
		},
		{
			key: 'priority',
			title: 'Priority',
			mobileRole: 'badge',
			cell: priorityCellSnippet,
		},
		{
			key: 'status',
			title: 'Status',
			mobileRole: 'badge',
			cell: statusCellSnippet,
		},
		{
			key: 'requestedAt',
			title: 'Requested',
			format: 'date',
			sortable: true,
			mobileRole: 'meta',
		},
	];
</script>

{#snippet titleCellSnippet(wo: WorkOrder)}
	<span data-testid="work-order-title">{wo.title}</span>
{/snippet}

{#snippet priorityCellSnippet(wo: WorkOrder)}
	<StatusBadge status={wo.priority} />
{/snippet}

{#snippet statusCellSnippet(wo: WorkOrder)}
	<StatusBadge status={wo.status} />
{/snippet}

{#snippet headerActions()}
	<div class="flex flex-wrap gap-2">
		<Button data-testid="work-order-create-button" onclick={openCreateWo}><Plus class="h-4 w-4" /> New Work Order</Button>
		<Button data-testid="inspection-create-button" variant="outline" onclick={openInspectionForm}><ShieldCheck class="h-4 w-4" /> Inspection</Button>
		<Button data-testid="recurring-maintenance-link" variant="outline" onclick={() => goto('/maintenance/recurring')}><RefreshCw class="h-4 w-4" /> Recurring</Button>
	</div>
{/snippet}

<svelte:head>
	<title>Work Orders - Rental Command</title>
</svelte:head>

<div class="box-border h-full overflow-y-auto p-6 pb-20" data-testid="maintenance-page">
	<PageHeader
		class="mb-6"
		band
		art={7}
		tone="coral"
		eyebrow="Work"
		title="Work Orders"
		description="Track resident requests, vendor execution, and compliance checks."
		actions={headerActions}
		data-testid="maintenance-header"
	/>

	<!-- Work Orders DataGrid -->
	<DataGrid
		data={woList}
		columns={woColumns}
		loading={workOrdersQuery.isLoading || workOrdersQuery.isFetching}
		emptyMessage="No work orders found."
		onRowClick={(wo) => goto(recordHref('workOrder', wo))}
		getRowKey={(wo) => wo.id}
		data-testid="work-orders-list"
		pageSize={PAGE_SIZE}
		page={gridPage}
		totalCount={woTotalCount}
		serverSide
		onPageChange={(page) => (gridPage = page)}
		sort={gridSort}
		onSortChange={(s) => { gridSort = s ?? ''; gridPage = 1; }}
	>
		{#snippet toolbar()}
			<div class="flex flex-1 items-center gap-2 min-w-0">
				<SearchInput bind:value={woSearch} placeholder="Search work orders…" testid="work-order-search" />
			</div>
			<Select.Root type="single" bind:value={woStatusFilter}>
				<Select.Trigger class="w-40 shrink-0" data-testid="work-order-status-filter">
					{woStatusFilter ? woStatusFilter : 'All statuses'}
				</Select.Trigger>
				<Select.Content>
					<Select.Item value="" label="All statuses">All statuses</Select.Item>
					{#each WO_STATUSES as s}
						<Select.Item value={s} label={s}>{s}</Select.Item>
					{/each}
				</Select.Content>
			</Select.Root>
			<Select.Root type="single" bind:value={woPriorityFilter}>
				<Select.Trigger class="w-36 shrink-0" data-testid="work-order-priority-filter">
					{woPriorityFilter ? woPriorityFilter : 'All priorities'}
				</Select.Trigger>
				<Select.Content>
					<Select.Item value="" label="All priorities">All priorities</Select.Item>
					{#each WO_PRIORITIES as p}
						<Select.Item value={p} label={p}>{p}</Select.Item>
					{/each}
				</Select.Content>
			</Select.Root>
			<RangeDatePicker
				bind:start={woFrom}
				bind:end={woTo}
				presets
				placeholder="All dates"
				align="end"
				testid="work-order-date-range"
			/>
		{/snippet}
	</DataGrid>

	<div class="mt-6 grid gap-6 xl:grid-cols-[minmax(0,1fr)_minmax(320px,420px)]">
		<Card.Root id="inspections" class="scroll-mt-6 gap-0 py-0">
			<Card.Header class="border-b border-border px-4 py-3">
				<Card.Title class="text-base font-semibold">Inspections</Card.Title>
			</Card.Header>
			<Card.Content class="max-h-[55vh] space-y-2 overflow-y-auto p-3" data-testid="inspections-list">
				{#if (inspectionsQuery.data || []).length === 0}
					<p class="py-6 text-center text-sm text-muted-foreground" data-testid="inspections-empty">No inspections scheduled yet.</p>
				{/if}
				{#each inspectionsQuery.data || [] as inspection (inspection.id)}
					<button
						type="button"
						class="block w-full rounded border border-border bg-background p-3 text-left text-sm transition-colors hover:bg-accent focus:outline-none focus-visible:ring-2 focus-visible:ring-ring"
						data-testid="inspection-row"
						onclick={() => goto('/maintenance/inspections/' + inspection.id)}
					>
						<div class="flex items-center justify-between gap-2">
							<p class="font-medium">{inspection.type} · {inspection.propertyName}</p>
							<StatusBadge status={inspection.status} />
						</div>
						<p class="font-mono tabular-nums text-xs text-muted-foreground">{new Date(inspection.scheduledFor).toLocaleString()}</p>
					</button>
				{/each}
			</Card.Content>
		</Card.Root>

		<Card.Root class="gap-0 py-0">
			<Card.Header class="border-b border-border px-4 py-3">
				<div class="flex items-center justify-between gap-2">
					<div>
						<Card.Title class="text-base font-semibold">Checklist templates</Card.Title>
						<p class="text-xs text-muted-foreground">Custom checklists are editable; built-ins are copyable</p>
					</div>
					<Button data-testid="inspection-template-create-button" size="sm" variant="outline" onclick={openNewTemplate}>
						<Plus class="h-4 w-4" /> New
					</Button>
				</div>
			</Card.Header>
			<Card.Content class="max-h-[55vh] space-y-2 overflow-y-auto p-3" data-testid="inspection-template-list">
				{#if templatesQuery.isLoading}
					<p class="py-6 text-center text-sm text-muted-foreground" data-testid="inspection-template-loading">Loading checklists…</p>
				{:else if templateOptions.length === 0}
					<p class="py-6 text-center text-sm text-muted-foreground" data-testid="inspection-template-empty">No checklist templates yet.</p>
				{:else}
					{#each templateOptions as template (template.id)}
						<div class="rounded border border-border bg-background p-3 text-sm" data-testid="inspection-template-row">
							<div class="flex items-start justify-between gap-3">
								<div class="min-w-0">
									<p class="truncate font-medium">{template.name}</p>
									<p class="mt-1 text-xs text-muted-foreground">
										{template.inspectionType} · {template.items.length} question{template.items.length === 1 ? '' : 's'}
										{template.isBuiltIn ? ' · built-in' : ''}
									</p>
								</div>
								<div class="flex shrink-0 gap-1">
									<Button
										type="button"
										size="icon"
										variant="ghost"
										title="Copy checklist"
										aria-label="Copy checklist"
										onclick={() => openCopyTemplate(template)}
										data-testid="inspection-template-copy-{template.id}"
									>
										<Copy class="h-4 w-4" />
									</Button>
									{#if !template.isBuiltIn}
										<Button
											type="button"
											size="icon"
											variant="ghost"
											title="Edit checklist"
											aria-label="Edit checklist"
											onclick={() => openEditTemplate(template)}
											data-testid="inspection-template-edit-{template.id}"
										>
											<Pencil class="h-4 w-4" />
										</Button>
										<Button
											type="button"
											size="icon"
											variant="ghost"
											title="Delete checklist"
											aria-label="Delete checklist"
											onclick={() => (templateDeleteTarget = template)}
											data-testid="inspection-template-delete-{template.id}"
										>
											<Trash2 class="h-4 w-4" />
										</Button>
									{/if}
								</div>
							</div>
						</div>
					{/each}
				{/if}
			</Card.Content>
		</Card.Root>
	</div>
</div>

<!-- Work order create/edit dialog -->
<Dialog.Root
	open={showWoForm}
	onOpenChange={(v) => { if (!v) closeWoForm(); }}
>
	<Dialog.Content class="max-h-[85vh] max-w-2xl overflow-y-auto">
		<Dialog.Header>
			<Dialog.Title>{editingWoId == null ? 'New Work Order' : 'Edit Work Order'}</Dialog.Title>
		</Dialog.Header>
		<FormStepper steps={woSteps} bind:currentStep={woStep} completedSteps={completedWoSteps} testid="work-order-stepper">
			<div class="space-y-4" data-testid="work-order-form">
				{#if woStep === 0}
					<div>
						<span class="mb-1 block text-xs font-medium text-muted-foreground">Issue title</span>
						<Input data-testid="work-order-title-input" bind:value={woForm.title} placeholder="Issue title" />
						{#if woErrors.title}<p class="mt-1 text-xs text-destructive" data-testid="work-order-title-error">{woErrors.title}</p>{/if}
					</div>
						<div>
							<span class="mb-1 block text-xs font-medium text-muted-foreground">Description</span>
							<textarea data-testid="work-order-description-input" bind:value={woForm.description} rows={4} class="w-full rounded border border-border bg-background px-3 py-2 text-sm" placeholder="Description"></textarea>
							{#if woErrors.description}<p class="mt-1 text-xs text-destructive" data-testid="work-order-description-error">{woErrors.description}</p>{/if}
						</div>
					{:else if woStep === 1}
						<div class="grid gap-3 sm:grid-cols-2">
							<div>
								<span class="mb-1 block text-xs font-medium text-muted-foreground">Priority</span>
							<Select.Root type="single" bind:value={woForm.priority}>
								<Select.Trigger class="w-full" data-testid="work-order-priority-input">
									{woForm.priority || 'Priority'}
								</Select.Trigger>
								<Select.Content>
									{#each WO_PRIORITIES as p}
										<Select.Item value={p} label={p}>{p}</Select.Item>
									{/each}
								</Select.Content>
							</Select.Root>
						</div>
						<div>
							<span class="mb-1 block text-xs font-medium text-muted-foreground">Category</span>
							<Input data-testid="work-order-category-input" bind:value={woForm.category} placeholder="Category" />
								{#if woErrors.category}<p class="mt-1 text-xs text-destructive" data-testid="work-order-category-error">{woErrors.category}</p>{/if}
							</div>
						</div>
						<div class="mt-3">
							<label for="work-order-technician-access" class="mb-1 block text-xs font-medium text-muted-foreground">Safe access instructions (optional)</label>
							<textarea id="work-order-technician-access" data-testid="work-order-technician-access-input" bind:value={woForm.technicianAccessInstructions} rows={3} maxlength={2000} class="w-full rounded border border-border bg-background px-3 py-2 text-sm" placeholder="Entry instructions, lockbox location, pets, or contact guidance safe for the assigned technician"></textarea>
							<p class="mt-1 text-xs text-muted-foreground">Shown only in the assigned technician workspace. Do not include financial or unrelated resident information.</p>
						</div>
					{:else if woStep === 2}
						<div>
							<RemoteRecordSelect
								queryKey={['work-order-property', portfolioId]}
								label="Property"
								bind:value={woForm.propertyId}
								selectedLabel={selectedWoPropertyLabel}
								placeholder="Select property"
								searchPlaceholder="Search properties…"
								emptyLabel="No matching properties"
								required
								loadPage={loadPropertyOptions}
								onValueChange={(_value, option) => {
									selectedWoPropertyLabel = option?.label ?? null;
									woForm.unitId = '';
									selectedWoUnitLabel = null;
								}}
								testid="work-order-property-input"
							/>
						{#if woErrors.propertyId}<p class="mt-1 text-xs text-destructive" data-testid="work-order-property-error">{woErrors.propertyId}</p>{/if}
					</div>
					<div>
						<RemoteRecordSelect
							queryKey={['work-order-unit', portfolioId, woPropertyId]}
							label="Unit (optional)"
							bind:value={woForm.unitId}
							selectedLabel={selectedWoUnitLabel}
							placeholder={woPropertyId == null ? 'Select a property first' : 'No specific unit'}
							clearLabel="No specific unit"
							searchPlaceholder="Search units…"
							emptyLabel="No matching units"
							disabled={woPropertyId == null}
							loadPage={loadWoUnitOptions}
							onValueChange={(_value, option) => (selectedWoUnitLabel = option?.label ?? null)}
							testid="work-order-unit-input"
						/>
						</div>
					{:else if woStep === 3}
						<div class="grid gap-3 sm:grid-cols-2">
							<div>
								<label for="work-order-scheduled" class="mb-1 block text-xs font-medium text-muted-foreground">Scheduled start</label>
								<DateTimePicker id="work-order-scheduled" testid="work-order-scheduled-input" bind:value={woForm.scheduledFor} />
								{#if woErrors.scheduledFor}<p class="mt-1 text-xs text-destructive" data-testid="work-order-scheduled-error">{woErrors.scheduledFor}</p>{/if}
							</div>
							<div>
								<label for="work-order-window-end" class="mb-1 block text-xs font-medium text-muted-foreground">Arrival window end</label>
								<DateTimePicker id="work-order-window-end" testid="work-order-window-end-input" bind:value={woForm.scheduledWindowEnd} />
								{#if woErrors.scheduledWindowEnd}<p class="mt-1 text-xs text-destructive" data-testid="work-order-window-end-error">{woErrors.scheduledWindowEnd}</p>{/if}
							</div>
						</div>
				{:else if woStep === 4}
					<div class="grid gap-3 sm:grid-cols-2">
						<RemoteRecordSelect
							queryKey={['work-order-tenant', portfolioId]}
							label="Tenant (optional)"
							bind:value={woForm.tenantId}
							selectedLabel={selectedWoTenantLabel}
							placeholder="No tenant"
							clearLabel="No tenant"
							searchPlaceholder="Search tenants…"
							emptyLabel="No matching tenants"
							loadPage={loadTenantOptions}
							onValueChange={(_value, option) => (selectedWoTenantLabel = option?.label ?? null)}
							testid="work-order-tenant-input"
						/>
						<RemoteRecordSelect
							queryKey={['work-order-vendor', portfolioId]}
							label="Vendor (optional)"
							bind:value={woForm.vendorId}
							selectedLabel={selectedWoVendorLabel}
							placeholder="No vendor"
							clearLabel="No vendor"
							searchPlaceholder="Search vendors…"
							emptyLabel="No matching vendors"
							loadPage={loadVendorOptions}
							onValueChange={(_value, option) => (selectedWoVendorLabel = option?.label ?? null)}
							testid="work-order-vendor-input"
						/>
					</div>
				{:else}
					<div>
						<label for="work-order-est-cost" class="mb-1 block text-xs font-medium text-muted-foreground">Estimated cost (optional)</label>
						<Input id="work-order-est-cost" data-testid="work-order-estimated-cost-input" type="text" inputmode="decimal" mask="currency" bind:value={woForm.estimatedCost} placeholder="0.00" />
						{#if woErrors.estimatedCost}<p class="mt-1 text-xs text-destructive" data-testid="work-order-estimated-cost-error">{woErrors.estimatedCost}</p>{/if}
					</div>
				{/if}
			</div>
		</FormStepper>
		<Dialog.Footer>
			<Button data-testid="work-order-form-cancel" variant="outline" onclick={closeWoForm}>Cancel</Button>
			{#if woStep > 0}
				<Button data-testid="work-order-step-back" variant="outline" onclick={() => (woStep = Math.max(woStep - 1, 0))}>Back</Button>
				{/if}
				{#if woStep < woSteps.length - 1}
					<StepperNextButton
						testid="work-order-step-next"
						onclick={nextWoStep}
						complete={completedWoSteps.includes(woStep)}
					/>
				{:else}
				<Button data-testid="work-order-form-save" onclick={submitWo} disabled={saveWoMutation.isPending}>{saveWoMutation.isPending ? 'Saving…' : 'Save work order'}</Button>
			{/if}
		</Dialog.Footer>
	</Dialog.Content>
</Dialog.Root>

<!-- Inspection create dialog -->
<Dialog.Root
	open={showInspectionForm}
	onOpenChange={(v) => { if (!v) closeInspectionForm(); }}
>
	<Dialog.Content class="max-h-[85vh] max-w-xl overflow-y-auto">
		<Dialog.Header>
			<Dialog.Title>Schedule Inspection</Dialog.Title>
		</Dialog.Header>
		<FormStepper
			steps={inspectionSteps}
			bind:currentStep={inspectionStep}
			completedSteps={completedInspectionSteps}
			testid="inspection-stepper"
		>
			<div class="space-y-4" data-testid="inspection-form">
				{#if inspectionStep === 0}
					<div>
						<RemoteRecordSelect
							queryKey={['inspection-property', portfolioId]}
							label="Property"
							bind:value={inspectionForm.propertyId}
							selectedLabel={selectedInspectionPropertyLabel}
							placeholder="Select property"
							searchPlaceholder="Search properties…"
							emptyLabel="No matching properties"
							required
							loadPage={loadPropertyOptions}
							onValueChange={(_value, option) => (selectedInspectionPropertyLabel = option?.label ?? null)}
							testid="inspection-property-input"
						/>
						{#if inspectionErrors.propertyId}<p class="mt-1 text-xs text-destructive" data-testid="inspection-property-error">{inspectionErrors.propertyId}</p>{/if}
					</div>
					<Select.Root type="single" bind:value={inspectionForm.type}>
						<Select.Trigger class="w-full" data-testid="inspection-type-input">
							{inspectionForm.type || 'Select type'}
						</Select.Trigger>
						<Select.Content>
							{#each INSPECTION_TYPES as type}
								<Select.Item value={type} label={type}>{type}</Select.Item>
							{/each}
						</Select.Content>
					</Select.Root>
				{:else if inspectionStep === 1}
					<div>
						<label for="inspection-template" class="mb-1 block text-xs font-medium text-muted-foreground">Checklist (optional)</label>
						<Select.Root
							type="single"
							value={inspectionForm.templateId}
							onValueChange={(v) => {
								inspectionForm.templateId = v;
								// Match the inspection type to the chosen checklist for a sensible default.
								const tpl = templateOptions.find((t) => String(t.id) === v);
								if (tpl) inspectionForm.type = tpl.inspectionType;
							}}
						>
							<Select.Trigger id="inspection-template" class="w-full" data-testid="inspection-template-input">
								{selectedTemplateName}
							</Select.Trigger>
							<Select.Content>
								<Select.Item value="" label="No checklist (blank)">No checklist (blank)</Select.Item>
								{#each templateOptions as tpl (tpl.id)}
									<Select.Item value={String(tpl.id)} label={tpl.name}>
										{tpl.name}{tpl.isBuiltIn ? ' · built-in' : ''}
									</Select.Item>
								{/each}
							</Select.Content>
						</Select.Root>
						<p class="mt-1 text-xs text-muted-foreground">Pick a checklist to load the items for this inspection.</p>
					</div>
					<div>
						<label for="inspection-inspector" class="mb-1 block text-xs font-medium text-muted-foreground">Inspector (optional)</label>
						<Input id="inspection-inspector" data-testid="inspection-inspector-input" bind:value={inspectionForm.inspector} placeholder="Who's doing this inspection?" />
					</div>
				{:else}
					<div>
						<label for="inspection-scheduled" class="mb-1 block text-xs font-medium text-muted-foreground">Scheduled for</label>
						<DateTimePicker id="inspection-scheduled" testid="inspection-scheduled-input" bind:value={inspectionForm.scheduledFor} />
						{#if inspectionErrors.scheduledFor}<p class="mt-1 text-xs text-destructive" data-testid="inspection-scheduled-error">{inspectionErrors.scheduledFor}</p>{/if}
					</div>
				{/if}
			</div>
		</FormStepper>
		<Dialog.Footer>
			<Button data-testid="inspection-form-cancel" variant="outline" onclick={closeInspectionForm}>Cancel</Button>
			{#if inspectionStep > 0}
				<Button data-testid="inspection-step-back" variant="outline" onclick={() => (inspectionStep = Math.max(inspectionStep - 1, 0))}>Back</Button>
			{/if}
			{#if inspectionStep < inspectionSteps.length - 1}
				<StepperNextButton
					testid="inspection-step-next"
					onclick={nextInspectionStep}
					complete={completedInspectionSteps.includes(inspectionStep)}
				/>
			{:else}
				<Button data-testid="inspection-form-save" onclick={submitInspection} disabled={createInspectionMutation.isPending}>{createInspectionMutation.isPending ? 'Scheduling…' : 'Schedule'}</Button>
			{/if}
		</Dialog.Footer>
	</Dialog.Content>
</Dialog.Root>

<!-- Inspection checklist template editor -->
<Dialog.Root
	open={showTemplateEditor}
	onOpenChange={(v) => { if (!v) closeTemplateEditor(); }}
>
	<Dialog.Content class="max-w-2xl" data-testid="inspection-template-editor-dialog">
		<Dialog.Header>
			<Dialog.Title>{editingTemplateId == null ? 'New checklist' : 'Edit checklist'}</Dialog.Title>
		</Dialog.Header>
		<div class="max-h-[70vh] space-y-4 overflow-y-auto pr-1" data-testid="inspection-template-editor-form">
			<div class="grid gap-3 sm:grid-cols-[minmax(0,1fr)_180px]">
				<div>
					<label for="inspection-template-name" class="mb-1 block text-xs font-medium text-muted-foreground">Name</label>
					<Input
						id="inspection-template-name"
						data-testid="inspection-template-name-input"
						bind:value={templateForm.name}
						placeholder="Move-out checklist"
					/>
					{#if templateErrors.name}
						<p class="mt-1 text-xs text-destructive" data-testid="inspection-template-name-error">{templateErrors.name}</p>
					{/if}
				</div>
				<div>
					<label for="inspection-template-type" class="mb-1 block text-xs font-medium text-muted-foreground">Type</label>
					<Select.Root type="single" bind:value={templateForm.inspectionType}>
						<Select.Trigger id="inspection-template-type" class="w-full" data-testid="inspection-template-type-input">
							{templateForm.inspectionType}
						</Select.Trigger>
						<Select.Content>
							{#each INSPECTION_TYPES as type}
								<Select.Item value={type} label={type}>{type}</Select.Item>
							{/each}
						</Select.Content>
					</Select.Root>
				</div>
			</div>

			<div class="space-y-2">
				<div class="flex items-center justify-between gap-2">
					<p class="text-sm font-medium">Questions</p>
					<Button
						type="button"
						size="sm"
						variant="outline"
						onclick={addTemplateQuestion}
						disabled={templateForm.items.length >= 100}
						data-testid="inspection-template-add-question"
					>
						<Plus class="h-4 w-4" /> Add question
					</Button>
				</div>
				{#if templateErrors.items}
					<p class="text-xs text-destructive" data-testid="inspection-template-items-error">{templateErrors.items}</p>
				{/if}
				<div class="space-y-3" data-testid="inspection-template-question-list">
					{#each templateForm.items as item, index (item.key)}
						<div class="rounded-md border border-border p-3" data-testid="inspection-template-question-row">
							<div class="grid gap-2 sm:grid-cols-[160px_minmax(0,1fr)_36px]">
								<div>
									<label for="template-area-{item.key}" class="mb-1 block text-xs font-medium text-muted-foreground">Area</label>
									<Input
										id="template-area-{item.key}"
										data-testid="inspection-template-question-area"
										bind:value={item.area}
										placeholder="Kitchen"
									/>
									{#if templateErrors[`items.${index}.area`]}
										<p class="mt-1 text-xs text-destructive">{templateErrors[`items.${index}.area`]}</p>
									{/if}
								</div>
								<div>
									<label for="template-label-{item.key}" class="mb-1 block text-xs font-medium text-muted-foreground">Question</label>
									<Input
										id="template-label-{item.key}"
										data-testid="inspection-template-question-label"
										bind:value={item.label}
										placeholder="Sink and faucet"
									/>
									{#if templateErrors[`items.${index}.label`]}
										<p class="mt-1 text-xs text-destructive">{templateErrors[`items.${index}.label`]}</p>
									{/if}
								</div>
								<div class="flex items-end">
									<Button
										type="button"
										size="icon"
										variant="ghost"
										title="Remove question"
										aria-label="Remove question"
										onclick={() => removeTemplateQuestion(index)}
										data-testid="inspection-template-remove-question"
									>
										<Trash2 class="h-4 w-4" />
									</Button>
								</div>
							</div>
						</div>
					{/each}
				</div>
			</div>
		</div>
		<Dialog.Footer>
			<Button data-testid="inspection-template-editor-cancel" variant="outline" onclick={closeTemplateEditor}>Cancel</Button>
			<Button data-testid="inspection-template-editor-save" onclick={submitTemplate} disabled={saveTemplateMutation.isPending}>
				{saveTemplateMutation.isPending ? 'Saving…' : 'Save checklist'}
			</Button>
		</Dialog.Footer>
	</Dialog.Content>
</Dialog.Root>

<ConfirmDialog
	open={woDeleteTarget !== null}
	title="Delete work order"
	message={woDeleteTarget ? `Delete "${woDeleteTarget.title}"?` : ''}
	busy={deleteWoMutation.isPending}
	testid="work-order-delete"
	onconfirm={() => woDeleteTarget && deleteWoMutation.mutate(woDeleteTarget.id)}
	oncancel={() => (woDeleteTarget = null)}
/>

<ConfirmDialog
	open={templateDeleteTarget !== null}
	title="Delete checklist"
	message={templateDeleteTarget ? `Delete "${templateDeleteTarget.name}"? Scheduled inspections that already used it keep their existing questions.` : ''}
	busy={deleteTemplateMutation.isPending}
	testid="inspection-template-delete"
	onconfirm={() => templateDeleteTarget && deleteTemplateMutation.mutate(templateDeleteTarget.id)}
	oncancel={() => (templateDeleteTarget = null)}
/>
