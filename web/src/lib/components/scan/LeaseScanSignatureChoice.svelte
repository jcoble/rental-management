<script lang="ts">
	import { createQuery } from '@tanstack/svelte-query';
	import { documentTemplates } from '$lib/api/endpoints/document-templates';
	import { Button } from '$lib/components/ui/button';
	import { Input } from '$lib/components/ui/input';
	import * as Select from '$lib/components/ui/select';

	export type LeaseScanReviewDisposition = 'AlreadyFullySigned' | 'NeedsSignatures';

	let {
		reviewDisposition = $bindable(),
		documentTemplateId = $bindable(),
		propertyId = null,
		disabled = false
	}: {
		reviewDisposition: LeaseScanReviewDisposition | '';
		documentTemplateId: string;
		propertyId?: number | null;
		disabled?: boolean;
	} = $props();

	const PAGE_SIZE = 20;
	let templateSearch = $state('');
	let templateSkip = $state(0);
	let templateSource = $state<'BuiltInRenderer' | 'CustomTemplate'>('BuiltInRenderer');
	let previousPropertyScope = $state<number | null>(null);
	let defaultSeededForScope = $state<number | null>(null);
	const propertyScope = $derived(propertyId && propertyId > 0 ? propertyId : 0);
	const needsSignatures = $derived(reviewDisposition === 'NeedsSignatures');

	const templatesQuery = createQuery(() => ({
		queryKey: [
			'document-templates',
			'lease-scan-signature-choice',
			propertyScope,
			templateSearch.trim(),
			templateSkip
		],
		queryFn: () =>
			documentTemplates.listPage({
				kind: 'Lease',
				status: 'Active',
				// A new property has no id yet. Passing 0 intentionally asks the server for
				// portfolio-wide templates only (`PropertyId IS NULL`), so a template tied
				// to some other property can never be selected accidentally.
				propertyId: propertyScope,
				search: templateSearch.trim() || undefined,
				sort: 'name',
				skip: templateSkip,
				take: PAGE_SIZE
			}),
		enabled: needsSignatures && templateSource === 'CustomTemplate'
	}));

	const selectedTemplateLabel = $derived.by(() => {
		if (!documentTemplateId) return 'Select an active lease template';
		const selected = templatesQuery.data?.items.find(
			(template) => String(template.id) === documentTemplateId
		);
		return selected ? `${selected.name} · v${selected.version}` : `Template #${documentTemplateId}`;
	});

	$effect(() => {
		const nextScope = propertyScope;
		if (previousPropertyScope !== null && previousPropertyScope !== nextScope) {
			documentTemplateId = '';
			templateSource = 'BuiltInRenderer';
			templateSearch = '';
			templateSkip = 0;
			defaultSeededForScope = null;
		}
		previousPropertyScope = nextScope;
	});

	$effect(() => {
		if (documentTemplateId) templateSource = 'CustomTemplate';
	});

	$effect(() => {
		if (!needsSignatures) {
			documentTemplateId = '';
			templateSource = 'BuiltInRenderer';
			defaultSeededForScope = null;
			return;
		}
		if (templateSource !== 'CustomTemplate' || documentTemplateId || defaultSeededForScope === propertyScope) return;
		const page = templatesQuery.data;
		if (!page) return;
		defaultSeededForScope = propertyScope;
		const defaultTemplate = page.items.find((template) => template.defaultForPortfolio);
		if (defaultTemplate) documentTemplateId = String(defaultTemplate.id);
	});
</script>

<fieldset class="space-y-3" data-testid="lease-scan-signature-choice" disabled={disabled}>
	<legend class="text-sm font-semibold text-foreground">Has everyone already signed this lease?</legend>
	<p class="text-xs leading-relaxed text-muted-foreground">
		This decides whether the uploaded PDF is the final legal agreement or a source document for a new signature request.
	</p>

	<div class="grid gap-2 md:grid-cols-2">
		<label
			class="cursor-pointer rounded-lg border p-3 transition-colors has-[:checked]:border-primary has-[:checked]:bg-primary/5 {disabled ? 'cursor-not-allowed opacity-60' : 'hover:border-primary/60'}"
			data-testid="lease-scan-already-signed-option"
		>
			<input
				type="radio"
				class="sr-only"
				name="lease-scan-review-disposition"
				value="AlreadyFullySigned"
				bind:group={reviewDisposition}
			/>
			<span class="block text-sm font-medium text-foreground">Yes, everyone has signed</span>
			<span class="mt-1 block text-xs leading-relaxed text-muted-foreground">
				Keep this exact PDF as the executed agreement. It becomes the active lease immediately.
			</span>
		</label>

		<label
			class="cursor-pointer rounded-lg border p-3 transition-colors has-[:checked]:border-primary has-[:checked]:bg-primary/5 {disabled ? 'cursor-not-allowed opacity-60' : 'hover:border-primary/60'}"
			data-testid="lease-scan-needs-signatures-option"
		>
			<input
				type="radio"
				class="sr-only"
				name="lease-scan-review-disposition"
				value="NeedsSignatures"
				bind:group={reviewDisposition}
			/>
			<span class="block text-sm font-medium text-foreground">No, signatures are still needed</span>
			<span class="mt-1 block text-xs leading-relaxed text-muted-foreground">
				Create an editable draft from the supplied lease or one of your templates. It will not take effect until the required people sign it.
			</span>
		</label>
	</div>

	{#if needsSignatures}
		<div class="space-y-2 rounded-lg border border-border bg-muted/25 p-3" data-testid="lease-scan-template-picker">
			<div>
				<p class="block text-xs font-semibold text-foreground">Agreement source</p>
				<p class="mt-1 text-xs text-muted-foreground">
					Use Rental Command's supplied lease, or choose one of your active custom templates. The uploaded file remains attached as the source.
				</p>
			</div>
			<div class="grid gap-2 sm:grid-cols-2">
				<label class="cursor-pointer rounded-md border p-3 has-[:checked]:border-primary has-[:checked]:bg-primary/5">
					<input class="sr-only" type="radio" name="lease-scan-template-source" value="BuiltInRenderer" bind:group={templateSource} onchange={() => (documentTemplateId = '')} />
					<span class="block text-sm font-medium">Rental Command supplied lease</span>
					<span class="mt-1 block text-xs text-muted-foreground">Ready to edit and send without creating a template first.</span>
				</label>
				<label class="cursor-pointer rounded-md border p-3 has-[:checked]:border-primary has-[:checked]:bg-primary/5">
					<input class="sr-only" type="radio" name="lease-scan-template-source" value="CustomTemplate" bind:group={templateSource} />
					<span class="block text-sm font-medium">My custom template</span>
					<span class="mt-1 block text-xs text-muted-foreground">Optionally use an active template you already configured.</span>
				</label>
			</div>

			{#if templateSource === 'CustomTemplate'}
				<Input
					id="lease-scan-template-search"
					aria-label="Search active lease templates"
					placeholder="Search active lease templates"
					value={templateSearch}
					disabled={disabled}
					oninput={(event) => {
						templateSearch = (event.currentTarget as HTMLInputElement).value;
						templateSkip = 0;
					}}
				/>
				<Select.Root type="single" bind:value={documentTemplateId} disabled={disabled || templatesQuery.isLoading}>
					<Select.Trigger class="w-full" data-testid="lease-scan-template-select" disabled={disabled || templatesQuery.isLoading}>
						{selectedTemplateLabel}
					</Select.Trigger>
					<Select.Content>
						{#each templatesQuery.data?.items ?? [] as template (template.id)}
							<Select.Item value={String(template.id)} label={`${template.name} · v${template.version}`}>
								{template.name} · v{template.version}{template.defaultForPortfolio ? ' · Default' : ''}
							</Select.Item>
						{/each}
					</Select.Content>
				</Select.Root>

				{#if templatesQuery.isLoading}
					<p class="text-xs text-muted-foreground">Loading active lease templates…</p>
				{:else if templatesQuery.isError}
					<p class="text-xs text-[var(--m3c-error)]">Custom templates could not be loaded. Use the supplied lease or try again.</p>
				{:else if templatesQuery.data?.totalCount === 0}
					<p class="text-xs text-[var(--warning)]">
						No custom active template is available for this property. You can use the Rental Command supplied lease above.
						<a class="font-medium underline underline-offset-2" href="/lease-templates" target="_blank" rel="noreferrer">Open Lease Templates</a>
						to create one for later.
					</p>
				{:else if templatesQuery.data}
					<div class="flex items-center justify-between gap-2 text-xs text-muted-foreground">
						<span>{templateSkip + 1}–{Math.min(templateSkip + PAGE_SIZE, templatesQuery.data.totalCount)} of {templatesQuery.data.totalCount}</span>
						<div class="flex gap-1">
							<Button type="button" variant="outline" size="sm" disabled={templateSkip === 0 || disabled} onclick={() => (templateSkip = Math.max(0, templateSkip - PAGE_SIZE))}>Previous</Button>
							<Button type="button" variant="outline" size="sm" disabled={templateSkip + PAGE_SIZE >= templatesQuery.data.totalCount || disabled} onclick={() => (templateSkip += PAGE_SIZE)}>Next</Button>
						</div>
					</div>
				{/if}
			{/if}
		</div>
	{/if}
</fieldset>
