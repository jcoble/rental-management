<script lang="ts">
	import { createMutation, createQuery, useQueryClient } from '@tanstack/svelte-query';
	import {
		FileText,
		Upload,
		ExternalLink,
		PenLine,
		RefreshCw,
		Signature,
		CheckCircle2,
		AlertCircle,
	} from '@lucide/svelte';
	import {
		documentTemplates,
		type DocumentTemplate,
		type DocumentTemplateFieldCatalogItem,
		type DocumentTemplateSignerRole,
	} from '$lib/api/endpoints/document-templates';
	import { documentFileHref } from '$lib/api/endpoints/documents';
	import { idempotentMutation } from '$lib/api/idempotency';
	import { showError, showSuccess, apiErrorMessage } from '$lib/utils/toast';
	import { Button } from '$lib/components/ui/button';
	import { Input } from '$lib/components/ui/input';
	import { Badge } from '$lib/components/ui/badge';
	import LeaseTemplateDesigner from '$lib/components/document-templates/LeaseTemplateDesigner.svelte';
	import * as Card from '$lib/components/ui/card';
	import PageHeader from '$lib/components/m3/PageHeader.svelte';

	const queryClient = useQueryClient();

	const templatesQuery = createQuery(() => ({
		queryKey: ['document-templates', 'lease', 'page', 0, 20],
		queryFn: () =>
			documentTemplates.listPage({
				kind: 'Lease',
				sort: '-updatedAt',
				take: 20,
			}),
	}));

	const catalogQuery = createQuery(() => ({
		queryKey: ['document-template-field-catalog', 'Lease'],
		queryFn: () => documentTemplates.fieldCatalog('Lease'),
	}));

	const templates = $derived(templatesQuery.data?.items ?? []);
	const totalCount = $derived(templatesQuery.data?.totalCount ?? 0);
	const hasTemplates = $derived(templates.length > 0);

	let selectedFile = $state<File | null>(null);
	let templateName = $state('');
	let templateDescription = $state('');
	let defaultForPortfolio = $state(false);
	let fileInput = $state<HTMLInputElement | null>(null);
	let designerTemplateId = $state<number | null>(null);
	let activatingTemplateId = $state<number | null>(null);

	const catalogGroups = $derived.by(() => {
		const groups = new Map<DocumentTemplateSignerRole, DocumentTemplateFieldCatalogItem[]>();
		for (const item of catalogQuery.data ?? []) {
			const current = groups.get(item.signerRole) ?? [];
			current.push(item);
			groups.set(item.signerRole, current);
		}
		return Array.from(groups.entries()).map(([role, items]) => ({ role, items }));
	});

	const uploadMutation = createMutation(() => ({
		mutationFn: () => {
			if (!selectedFile) {
				throw new Error('Select a PDF lease template first.');
			}
			const name = templateName.trim();
			if (!name) {
				throw new Error('Template name is required.');
			}
			return documentTemplates.uploadLeasePdf(selectedFile, {
				name,
				description: templateDescription.trim() || undefined,
				defaultForPortfolio,
			});
		},
		onSuccess: (template) => {
			showSuccess(`${template.name} uploaded as a draft lease template.`);
			designerTemplateId = template.id;
			selectedFile = null;
			templateName = '';
			templateDescription = '';
			defaultForPortfolio = false;
			if (fileInput) fileInput.value = '';
			queryClient.invalidateQueries({ queryKey: ['document-templates'] });
		},
		onError: (err) => showError(apiErrorMessage(err, 'Lease template upload failed.')),
	}));

	const activateTemplateMutation = createMutation(() => ({
		mutationFn: (template: DocumentTemplate) => {
			activatingTemplateId = template.id;
			return idempotentMutation(`document-template:${template.id}:activate`, (operationKey) =>
				documentTemplates.update(
					template.id,
					{
						status: 'Active',
						defaultForPortfolio: true
					},
					operationKey
				)
			);
		},
		onSuccess: (template) => {
			showSuccess(`${template.name} is now the default lease PDF.`);
			queryClient.invalidateQueries({ queryKey: ['document-templates'] });
		},
		onError: (err) => showError(apiErrorMessage(err, 'Could not activate lease template.')),
		onSettled: () => {
			activatingTemplateId = null;
		},
	}));

	function handleFileSelected(event: Event) {
		const input = event.currentTarget as HTMLInputElement;
		const file = input.files?.[0] ?? null;
		selectedFile = file;
		if (file && !templateName.trim()) {
			templateName = file.name.replace(/\.pdf$/i, '').replaceAll('-', ' ').replaceAll('_', ' ');
		}
	}

	function submitUpload() {
		if (!selectedFile) {
			showError('Select a PDF lease template first.');
			return;
		}
		const fileLooksLikePdf =
			selectedFile.type === 'application/pdf' ||
			selectedFile.name.toLowerCase().endsWith('.pdf');
		if (!fileLooksLikePdf) {
			showError('Only PDF lease templates are supported right now.');
			return;
		}
		uploadMutation.mutate();
	}

	function activateDefault(template: DocumentTemplate) {
		if (template.fieldCount === 0) {
			showError('Place at least one dynamic field before using this lease PDF as the default.');
			return;
		}
		activateTemplateMutation.mutate(template);
	}

	function statusVariant(status: DocumentTemplate['status']): 'default' | 'secondary' | 'outline' {
		if (status === 'Active') return 'default';
		if (status === 'Draft') return 'secondary';
		return 'outline';
	}

	function renderModeLabel(mode: DocumentTemplate['renderMode']) {
		return mode === 'Overlay' ? 'Exact PDF overlay' : 'Drafted lease';
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
				return 'Auto-filled';
		}
	}

	function formatDateTime(value: string | null | undefined) {
		if (!value) return '—';
		return new Intl.DateTimeFormat('en-US', {
			month: 'short',
			day: 'numeric',
			year: 'numeric',
			hour: 'numeric',
			minute: '2-digit',
		}).format(new Date(value));
	}

	function fieldSummary(template: DocumentTemplate) {
		if (template.fieldCount === 0) return 'No fields placed yet';
		return `${template.fieldCount} field${template.fieldCount === 1 ? '' : 's'} placed`;
	}

	function isActiveDefault(template: DocumentTemplate) {
		return template.status === 'Active' && template.defaultForPortfolio;
	}
</script>

<svelte:head>
	<title>Lease Templates - Rental Command</title>
</svelte:head>

{#snippet headerActions()}
	<Button href="/leases" variant="outline" class="gap-1.5" data-testid="lease-templates-back-to-leases">
		<FileText class="h-4 w-4" />
		Leases
	</Button>
{/snippet}

<div class="box-border h-full overflow-y-auto p-6 pb-20" data-testid="lease-templates-page">
	<PageHeader
		class="mb-6"
		band
		art={5}
		tone="amber"
		eyebrow="Rentals"
		title="Lease Templates"
		description="Upload landlord lease PDFs and prepare them for auto-fill fields and e-signature."
		actions={headerActions}
		data-testid="lease-templates-header"
	/>

	<div class="grid gap-6 xl:grid-cols-[420px_minmax(0,1fr)]">
		<Card.Root class="h-fit" data-testid="lease-template-upload-card">
			<Card.Header class="border-b border-border">
				<Card.Title class="text-base">Upload landlord PDF</Card.Title>
				<Card.Description>Start from the exact lease the landlord already uses.</Card.Description>
			</Card.Header>
			<Card.Content class="space-y-4 pt-6">
				<div class="space-y-2">
					<label for="lease-template-file" class="text-sm font-medium">PDF file</label>
					<Input
						id="lease-template-file"
						bind:ref={fileInput}
						type="file"
						accept="application/pdf,.pdf"
						onchange={handleFileSelected}
						data-testid="lease-template-file-input"
					/>
					{#if selectedFile}
						<p class="text-xs text-muted-foreground" data-testid="lease-template-selected-file">
							{selectedFile.name} · {Math.ceil(selectedFile.size / 1024).toLocaleString()} KB
						</p>
					{/if}
				</div>

				<div class="space-y-2">
					<label for="lease-template-name" class="text-sm font-medium">Template name</label>
					<Input
						id="lease-template-name"
						bind:value={templateName}
						placeholder="Standard residential lease"
						maxlength={200}
						data-testid="lease-template-name-input"
					/>
				</div>

				<div class="space-y-2">
					<label for="lease-template-description" class="text-sm font-medium">Notes</label>
					<textarea
						id="lease-template-description"
						bind:value={templateDescription}
						maxlength="2000"
						rows="3"
						class="min-h-20 w-full rounded-md border border-input bg-background px-3 py-2 text-sm shadow-sm transition-colors placeholder:text-muted-foreground focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring"
						placeholder="Use for annual renewals, single-family rentals, etc."
						data-testid="lease-template-description-input"
					></textarea>
				</div>

				<label class="flex items-start gap-3 rounded-md border border-border bg-muted/30 px-3 py-3 text-sm">
					<input
						type="checkbox"
						bind:checked={defaultForPortfolio}
						class="mt-0.5 h-4 w-4 rounded border-border"
						data-testid="lease-template-default-input"
					/>
					<span>
						<span class="block font-medium">Use as portfolio default</span>
						<span class="block text-xs text-muted-foreground">New lease workflows can prefer this template after its fields are placed.</span>
					</span>
				</label>

				<Button
					class="w-full gap-1.5"
					onclick={submitUpload}
					disabled={uploadMutation.isPending}
					data-testid="lease-template-upload-submit"
				>
					<Upload class="h-4 w-4" />
					{uploadMutation.isPending ? 'Uploading…' : 'Upload lease PDF'}
				</Button>
			</Card.Content>
		</Card.Root>

		<section class="space-y-4" aria-labelledby="lease-template-library-heading">
			<div class="flex flex-wrap items-center justify-between gap-3">
				<div>
					<h2 id="lease-template-library-heading" class="text-lg font-semibold tracking-tight">Template Library</h2>
					<p class="text-sm text-muted-foreground">
						{totalCount === 1 ? '1 lease template' : `${totalCount} lease templates`} in this portfolio.
					</p>
				</div>
				<Button
					variant="outline"
					size="sm"
					class="gap-1.5"
					onclick={() => templatesQuery.refetch()}
					disabled={templatesQuery.isFetching}
					data-testid="lease-template-refresh"
				>
					<RefreshCw class="h-4 w-4 {templatesQuery.isFetching ? 'animate-spin' : ''}" />
					Refresh
				</Button>
			</div>

			{#if templatesQuery.isLoading}
				<div class="grid gap-3" data-testid="lease-template-loading">
					{#each Array.from({ length: 3 }) as _}
						<div class="h-28 animate-pulse rounded-lg border border-border bg-muted/30"></div>
					{/each}
				</div>
			{:else if templatesQuery.isError}
				<div class="rounded-lg border border-destructive/30 bg-destructive/5 p-4" data-testid="lease-template-error">
					<div class="flex items-center gap-2 text-sm font-medium text-destructive">
						<AlertCircle class="h-4 w-4" />
						Template library is unavailable.
					</div>
					<Button variant="outline" size="sm" class="mt-3" onclick={() => templatesQuery.refetch()}>Retry</Button>
				</div>
			{:else if !hasTemplates}
				<div class="rounded-lg border border-dashed border-border bg-muted/20 p-8 text-center" data-testid="lease-template-empty">
					<FileText class="mx-auto h-8 w-8 text-muted-foreground" />
					<h3 class="mt-3 text-sm font-semibold">No lease templates yet</h3>
					<p class="mx-auto mt-1 max-w-md text-sm text-muted-foreground">
						Upload the landlord's PDF lease to create the first reusable template.
					</p>
				</div>
			{:else}
				<div class="grid gap-3" data-testid="lease-template-list">
					{#each templates as template (template.id)}
						<article class="rounded-lg border border-border bg-card p-4 shadow-sm" data-testid={template.testId}>
							<div class="flex flex-wrap items-start justify-between gap-3">
								<div class="min-w-0 flex-1">
									<div class="flex flex-wrap items-center gap-2">
										<h3 class="truncate text-base font-semibold">{template.name}</h3>
										<Badge variant={statusVariant(template.status)}>{template.status}</Badge>
										{#if template.defaultForPortfolio}
											<Badge variant="outline" class="gap-1">
												<CheckCircle2 class="h-3 w-3" />
												Default
											</Badge>
										{/if}
									</div>
									<p class="mt-1 text-sm text-muted-foreground">
										{template.description || renderModeLabel(template.renderMode)}
									</p>
								</div>
								<div class="flex shrink-0 flex-wrap gap-2">
									{#if template.originalStoredFileId}
										<Button
											href={documentFileHref(template.originalStoredFileId)}
											target="_blank"
											rel="noopener noreferrer"
											variant="outline"
											size="sm"
											class="gap-1.5"
											data-testid="lease-template-source-{template.id}"
										>
											<ExternalLink class="h-4 w-4" />
											Source PDF
										</Button>
									{/if}
									<Button
										variant="outline"
										size="sm"
										class="gap-1.5"
										onclick={() => (designerTemplateId = template.id)}
										data-testid="lease-template-designer-{template.id}"
									>
										<PenLine class="h-4 w-4" />
										Design fields
									</Button>
									{#if !isActiveDefault(template)}
										<Button
											size="sm"
											class="gap-1.5"
											onclick={() => activateDefault(template)}
											disabled={activateTemplateMutation.isPending || template.fieldCount === 0}
											title={template.fieldCount === 0 ? 'Place fields before activating this template.' : 'Use this PDF for new lease agreements'}
											data-testid="lease-template-use-default-{template.id}"
										>
											<CheckCircle2 class="h-4 w-4" />
											{activatingTemplateId === template.id ? 'Saving…' : 'Use as default'}
										</Button>
									{/if}
								</div>
							</div>
							<div class="mt-4 grid gap-2 text-sm sm:grid-cols-3">
								<div class="rounded-md bg-muted/30 px-3 py-2">
									<p class="text-xs text-muted-foreground">Field placement</p>
									<p class="font-medium">{fieldSummary(template)}</p>
								</div>
								<div class="rounded-md bg-muted/30 px-3 py-2">
									<p class="text-xs text-muted-foreground">Mode</p>
									<p class="font-medium">{renderModeLabel(template.renderMode)}</p>
								</div>
								<div class="rounded-md bg-muted/30 px-3 py-2">
									<p class="text-xs text-muted-foreground">Updated</p>
									<p class="font-medium">{formatDateTime(template.updatedAtUtc)}</p>
								</div>
							</div>
						</article>
					{/each}
				</div>
			{/if}
		</section>
	</div>

	{#if designerTemplateId}
		<section class="mt-6" aria-label="Lease template field designer">
			<LeaseTemplateDesigner
				templateId={designerTemplateId}
				catalog={catalogQuery.data ?? []}
				onClose={() => (designerTemplateId = null)}
			/>
		</section>
	{/if}

	<section class="mt-8 space-y-4" aria-labelledby="lease-template-fields-heading" data-testid="lease-template-field-catalog">
		<div>
			<h2 id="lease-template-fields-heading" class="text-lg font-semibold tracking-tight">Dynamic Fields</h2>
			<p class="text-sm text-muted-foreground">
				These are the merge and signature fields available for lease templates.
			</p>
		</div>

		{#if catalogQuery.isLoading}
			<div class="grid gap-3 md:grid-cols-2 xl:grid-cols-4">
				{#each Array.from({ length: 4 }) as _}
					<div class="h-36 animate-pulse rounded-lg border border-border bg-muted/30"></div>
				{/each}
			</div>
		{:else if catalogQuery.isError}
			<div class="rounded-lg border border-destructive/30 bg-destructive/5 p-4 text-sm text-destructive">
				Field catalog is unavailable.
			</div>
		{:else}
			<div class="grid gap-4 md:grid-cols-2 xl:grid-cols-4">
				{#each catalogGroups as group}
					<div class="rounded-lg border border-border bg-card p-4">
						<div class="mb-3 flex items-center gap-2">
							<span class="inline-grid h-8 w-8 place-items-center rounded-md bg-muted text-muted-foreground">
								{#if group.role === 'None'}
									<FileText class="h-4 w-4" />
								{:else}
									<Signature class="h-4 w-4" />
								{/if}
							</span>
							<h3 class="font-semibold">{formatSigner(group.role)}</h3>
						</div>
						<div class="space-y-2">
							{#each group.items as field (field.fieldKey)}
								<div class="rounded-md bg-muted/25 px-3 py-2">
									<div class="flex items-center justify-between gap-2">
										<p class="text-sm font-medium">{field.label}</p>
										{#if field.requiredForSignature}
											<Badge variant="outline">Required</Badge>
										{/if}
									</div>
									<p class="mt-1 text-xs text-muted-foreground">{field.description}</p>
								</div>
							{/each}
						</div>
					</div>
				{/each}
			</div>
		{/if}
	</section>
</div>
