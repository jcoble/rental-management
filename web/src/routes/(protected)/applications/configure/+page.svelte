<script lang="ts">
	import { createQuery, createMutation, useQueryClient } from '@tanstack/svelte-query';
	import { goto } from '$app/navigation';
	import {
		applications,
		type CustomFieldInput,
		type CustomFieldType,
		type SaveFormConfigRequest,
	} from '$lib/api/endpoints/applications';
	import type { PublicApplicationProperty } from '$lib/api/public-applications';
	import { showSuccess, showError, apiErrorMessage } from '$lib/utils/toast';
	import PageBreadcrumb from '$lib/components/shared/PageBreadcrumb.svelte';
	import DatePicker from '$lib/components/shared/DatePicker.svelte';
	import * as Card from '$lib/components/ui/card';
	import * as Select from '$lib/components/ui/select';
	import { Button } from '$lib/components/ui/button';
	import { Input } from '$lib/components/ui/input';
	import { Checkbox } from '$lib/components/ui/checkbox';
	import {
		ClipboardList,
		Plus,
		X,
		ArrowUp,
		ArrowDown,
		PawPrint,
		Briefcase,
		Home,
		HelpCircle,
		ArrowLeft,
	} from '@lucide/svelte';

	const queryClient = useQueryClient();

	const configQuery = createQuery(() => ({
		queryKey: ['application-form-config'],
		queryFn: () => applications.getFormConfig(),
	}));

	const properties = $derived<PublicApplicationProperty[]>(configQuery.data?.properties ?? []);

	// ── Editable model ───────────────────────────────────────────────────────────
	// shadcn Select binds strings; 'none' is the "no default" sentinel (bits-ui treats '' as no selection).
	const NONE = 'none';
	let incomeEnabled = $state(true);
	let petsEnabled = $state(false);
	let petsAskDeposit = $state(false);
	let defaultProperty = $state(NONE);
	let defaultUnit = $state(NONE);
	let defaultMoveIn = $state('');
	let lockProperty = $state(false);
	let lockUnit = $state(false);
	let lockMoveIn = $state(false);

	type CustomRow = {
		id: number;
		fieldId: string | null;
		label: string;
		type: CustomFieldType;
		required: boolean;
		optionsText: string;
	};
	let rowSeq = 0;
	let customRows = $state<CustomRow[]>([]);

	const FIELD_TYPES: { value: CustomFieldType; label: string; help: string }[] = [
		{ value: 'text', label: 'Short text', help: 'A box for a sentence or two.' },
		{ value: 'number', label: 'Number', help: 'Only numbers (e.g. how many occupants).' },
		{ value: 'yesno', label: 'Yes / No', help: 'A simple yes-or-no question.' },
		{ value: 'select', label: 'Pick from a list', help: 'You provide the choices.' },
	];
	function typeLabel(t: CustomFieldType): string {
		return FIELD_TYPES.find((x) => x.value === t)?.label ?? t;
	}

	// Units available for the chosen default property.
	const selectedProperty = $derived(
		defaultProperty === NONE ? null : properties.find((p) => String(p.id) === defaultProperty) ?? null
	);
	const availableUnits = $derived(selectedProperty?.units ?? []);
	const propertyLabel = $derived(selectedProperty ? selectedProperty.name : 'No default');
	const unitLabel = $derived.by(() => {
		if (defaultUnit === NONE) return 'No default';
		const u = availableUnits.find((x) => String(x.id) === defaultUnit);
		return u ? `Unit ${u.unitNumber}` : 'No default';
	});

	function onDefaultPropertyChange() {
		// Reset the unit when the property changes if it no longer belongs.
		if (!availableUnits.some((u) => String(u.id) === defaultUnit)) {
			defaultUnit = NONE;
			lockUnit = false;
		}
	}

	// Seed the editable model from the loaded config exactly once.
	let loaded = false;
	$effect(() => {
		const data = configQuery.data;
		if (!data || loaded) return;
		loaded = true;
		const c = data.config;
		incomeEnabled = c.incomeSources.enabled;
		petsEnabled = c.pets.enabled;
		petsAskDeposit = c.pets.askDeposit;
		defaultProperty = c.defaults.propertyId != null ? String(c.defaults.propertyId) : NONE;
		defaultUnit = c.defaults.unitId != null ? String(c.defaults.unitId) : NONE;
		defaultMoveIn = c.defaults.desiredMoveInDate ?? '';
		lockProperty = c.locked.includes('propertyId');
		lockUnit = c.locked.includes('unitId');
		lockMoveIn = c.locked.includes('desiredMoveInDate');
		customRows = c.customFields.map((f) => ({
			id: rowSeq++,
			fieldId: f.id,
			label: f.label,
			type: f.type,
			required: f.required,
			optionsText: (f.options ?? []).join('\n'),
		}));
	});

	function addRow() {
		customRows = [
			...customRows,
			{ id: rowSeq++, fieldId: null, label: '', type: 'text', required: false, optionsText: '' },
		];
	}
	function removeRow(id: number) {
		customRows = customRows.filter((r) => r.id !== id);
	}
	function moveRow(index: number, delta: number) {
		const next = index + delta;
		if (next < 0 || next >= customRows.length) return;
		const copy = [...customRows];
		[copy[index], copy[next]] = [copy[next], copy[index]];
		customRows = copy;
	}

	// ── Save ───────────────────────────────────────────────────────────────────
	const saveMutation = createMutation(() => ({
		mutationFn: (body: SaveFormConfigRequest) => applications.saveFormConfig(body),
		onSuccess: () => {
			queryClient.invalidateQueries({ queryKey: ['application-form-config'] });
			showSuccess('Application form saved.');
		},
		onError: (err) => showError(apiErrorMessage(err)),
	}));

	function clientValidate(): string | null {
		for (const r of customRows) {
			if (!r.label.trim()) return 'Every question needs a label.';
			if (r.type === 'select' && parseOptions(r.optionsText).length === 0)
				return `"${r.label.trim()}" is a list question — add at least one choice.`;
		}
		return null;
	}

	function parseOptions(text: string): string[] {
		return text
			.split(/[\n,]+/)
			.map((s) => s.trim())
			.filter(Boolean);
	}

	function save() {
		const err = clientValidate();
		if (err) {
			showError(err);
			return;
		}
		const customFields: CustomFieldInput[] = customRows.map((r) => ({
			id: r.fieldId,
			label: r.label.trim(),
			type: r.type,
			required: r.required,
			options: r.type === 'select' ? parseOptions(r.optionsText) : [],
		}));

		// Only lock keys that actually have a default chosen (the server enforces this too).
		const locked: string[] = [];
		if (lockProperty && defaultProperty !== NONE) locked.push('propertyId');
		if (lockUnit && defaultUnit !== NONE) locked.push('unitId');
		if (lockMoveIn && defaultMoveIn) locked.push('desiredMoveInDate');

		const body: SaveFormConfigRequest = {
			incomeSources: { enabled: incomeEnabled },
			pets: { enabled: petsEnabled, askDeposit: petsAskDeposit },
			customFields,
			defaults: {
				propertyId: defaultProperty === NONE ? null : Number(defaultProperty),
				unitId: defaultUnit === NONE ? null : Number(defaultUnit),
				desiredMoveInDate: defaultMoveIn || null,
			},
			locked,
		};
		saveMutation.mutate(body);
	}
</script>

<svelte:head>
	<title>Configure application form - Rental Command</title>
</svelte:head>

<div class="box-border h-full overflow-y-auto p-6 pb-20" data-testid="configure-application-page">
	<div class="mb-4">
		<PageBreadcrumb
			crumbs={[
				{ label: 'Applications', href: '/applications' },
				{ label: 'Configure form' },
			]}
		/>
	</div>

	<div class="mb-6 flex flex-wrap items-start justify-between gap-3">
		<div>
			<h1 class="flex items-center gap-2 text-2xl font-bold">
				<ClipboardList class="h-6 w-6 text-primary" /> Configure your application form
			</h1>
			<p class="mt-1 max-w-2xl text-sm text-muted-foreground">
				Tailor the form prospective tenants fill out. Set what's pre-filled, turn the pets section on
				or off, and add your own questions. Changes apply to your shared application link right away.
			</p>
		</div>
		<Button variant="outline" class="gap-2" onclick={() => goto('/applications')} data-testid="configure-back">
			<ArrowLeft class="h-4 w-4" /> Back
		</Button>
	</div>

	{#if configQuery.isLoading}
		<div class="flex items-center justify-center py-20 text-muted-foreground" data-testid="configure-loading">
			<div class="flex flex-col items-center gap-2">
				<div class="h-6 w-6 animate-spin rounded-full border-2 border-current border-t-transparent"></div>
				<span class="text-sm">Loading…</span>
			</div>
		</div>
	{:else if configQuery.isError}
		<div class="rounded-lg border border-destructive/30 bg-destructive/10 p-6 text-center" data-testid="configure-error">
			<p class="font-medium text-destructive">Could not load the form configuration</p>
			<p class="mt-1 text-sm text-muted-foreground">{apiErrorMessage(configQuery.error)}</p>
			<Button variant="outline" class="mt-4" onclick={() => configQuery.refetch()}>Retry</Button>
		</div>
	{:else}
		<div class="grid max-w-3xl gap-6">
			<!-- Defaults -->
			<Card.Root data-testid="configure-defaults">
				<Card.Header>
					<Card.Title class="flex items-center gap-2 text-base"><Home class="h-4 w-4" /> Pre-filled defaults</Card.Title>
				</Card.Header>
				<Card.Content class="space-y-4">
					<p class="text-sm text-muted-foreground">
						Choose what's already filled in when someone opens your link. Tick "Lock" to stop applicants
						changing it — handy when a link is for one specific home.
					</p>

					<div class="grid gap-4 sm:grid-cols-2">
						<div>
							<span class="mb-1.5 block text-sm font-medium text-foreground">Default property</span>
							<Select.Root type="single" bind:value={defaultProperty} onValueChange={onDefaultPropertyChange}>
								<Select.Trigger class="w-full" data-testid="configure-default-property">{propertyLabel}</Select.Trigger>
								<Select.Content>
									<Select.Item value={NONE} label="No default">No default</Select.Item>
									{#each properties as p (p.id)}
										<Select.Item value={String(p.id)} label={p.name}>{p.name}</Select.Item>
									{/each}
								</Select.Content>
							</Select.Root>
							<label class="mt-2 flex items-center gap-2 text-sm text-foreground">
								<Checkbox
									checked={lockProperty}
									disabled={defaultProperty === NONE}
									onCheckedChange={(v) => (lockProperty = v === true)}
									data-testid="configure-lock-property"
								/>
								Lock this so applicants can't change it
							</label>
						</div>

						<div>
							<span class="mb-1.5 block text-sm font-medium text-foreground">Default unit</span>
							<Select.Root type="single" bind:value={defaultUnit} disabled={availableUnits.length === 0}>
								<Select.Trigger class="w-full" data-testid="configure-default-unit">{unitLabel}</Select.Trigger>
								<Select.Content>
									<Select.Item value={NONE} label="No default">No default</Select.Item>
									{#each availableUnits as u (u.id)}
										<Select.Item value={String(u.id)} label={`Unit ${u.unitNumber}`}>Unit {u.unitNumber}</Select.Item>
									{/each}
								</Select.Content>
							</Select.Root>
							<label class="mt-2 flex items-center gap-2 text-sm text-foreground">
								<Checkbox
									checked={lockUnit}
									disabled={defaultUnit === NONE}
									onCheckedChange={(v) => (lockUnit = v === true)}
									data-testid="configure-lock-unit"
								/>
								Lock this so applicants can't change it
							</label>
						</div>

						<div class="sm:col-span-2">
							<span class="mb-1.5 block text-sm font-medium text-foreground">Default move-in date</span>
							<div class="max-w-xs">
								<DatePicker
									value={defaultMoveIn}
									onchange={(v) => (defaultMoveIn = v)}
									placeholder="No default"
									testid="configure-default-movein"
								/>
							</div>
							<label class="mt-2 flex items-center gap-2 text-sm text-foreground">
								<Checkbox
									checked={lockMoveIn}
									disabled={!defaultMoveIn}
									onCheckedChange={(v) => (lockMoveIn = v === true)}
									data-testid="configure-lock-movein"
								/>
								Lock this so applicants can't change it
							</label>
						</div>
					</div>
				</Card.Content>
			</Card.Root>

			<!-- Income -->
			<Card.Root data-testid="configure-income">
				<Card.Header>
					<Card.Title class="flex items-center gap-2 text-base"><Briefcase class="h-4 w-4" /> Income</Card.Title>
				</Card.Header>
				<Card.Content>
					<label class="flex items-start gap-3">
						<Checkbox
							checked={incomeEnabled}
							onCheckedChange={(v) => (incomeEnabled = v === true)}
							data-testid="configure-income-enabled"
						/>
						<span class="text-sm leading-tight">
							<span class="font-medium">Let applicants add more than one job or income</span>
							<span class="block text-xs text-muted-foreground">
								When on, the applicant can add several income sources. When off, the form asks for a
								single employer and monthly income.
							</span>
						</span>
					</label>
				</Card.Content>
			</Card.Root>

			<!-- Pets -->
			<Card.Root data-testid="configure-pets">
				<Card.Header>
					<Card.Title class="flex items-center gap-2 text-base"><PawPrint class="h-4 w-4" /> Pets</Card.Title>
				</Card.Header>
				<Card.Content class="space-y-3">
					<label class="flex items-start gap-3">
						<Checkbox
							checked={petsEnabled}
							onCheckedChange={(v) => (petsEnabled = v === true)}
							data-testid="configure-pets-enabled"
						/>
						<span class="text-sm leading-tight">
							<span class="font-medium">Ask about pets</span>
							<span class="block text-xs text-muted-foreground">Adds a "Do you have pets?" section with pet details.</span>
						</span>
					</label>
					{#if petsEnabled}
						<label class="ml-8 flex items-start gap-3">
							<Checkbox
								checked={petsAskDeposit}
								onCheckedChange={(v) => (petsAskDeposit = v === true)}
								data-testid="configure-pets-deposit"
							/>
							<span class="text-sm leading-tight">
								<span class="font-medium">Mention a pet deposit may apply</span>
								<span class="block text-xs text-muted-foreground">Just a note on the form — it doesn't charge anything.</span>
							</span>
						</label>
					{/if}
				</Card.Content>
			</Card.Root>

			<!-- Custom questions -->
			<Card.Root data-testid="configure-custom">
				<Card.Header>
					<Card.Title class="flex items-center gap-2 text-base"><HelpCircle class="h-4 w-4" /> Your own questions</Card.Title>
				</Card.Header>
				<Card.Content class="space-y-4">
					<p class="text-sm text-muted-foreground">
						Add any extra questions you want applicants to answer. Drag isn't needed — use the arrows to
						reorder. They appear on the form in this order.
					</p>

					{#if customRows.length === 0}
						<p class="rounded-lg border border-dashed border-border bg-muted/30 p-4 text-sm text-muted-foreground" data-testid="configure-custom-empty">
							No extra questions yet. Add one below.
						</p>
					{/if}

					<div class="space-y-3">
						{#each customRows as row, i (row.id)}
							<div class="rounded-xl border border-border bg-muted/20 p-4" data-testid="configure-custom-row">
								<div class="mb-3 flex items-center justify-between gap-2">
									<span class="text-xs font-semibold uppercase tracking-wide text-muted-foreground">Question {i + 1}</span>
									<div class="flex items-center gap-1">
										<Button variant="ghost" size="sm" class="h-8 w-8 p-0" disabled={i === 0} onclick={() => moveRow(i, -1)} aria-label="Move up" data-testid="configure-custom-up">
											<ArrowUp class="h-4 w-4" />
										</Button>
										<Button variant="ghost" size="sm" class="h-8 w-8 p-0" disabled={i === customRows.length - 1} onclick={() => moveRow(i, 1)} aria-label="Move down" data-testid="configure-custom-down">
											<ArrowDown class="h-4 w-4" />
										</Button>
										<Button variant="ghost" size="sm" class="h-8 gap-1 px-2 text-destructive hover:text-destructive" onclick={() => removeRow(row.id)} aria-label="Remove question" data-testid="configure-custom-remove">
											<X class="h-4 w-4" />
										</Button>
									</div>
								</div>

								<div class="grid gap-3 sm:grid-cols-[1fr_180px]">
									<div>
										<label for={`configure-custom-label-${row.id}`} class="mb-1 block text-xs text-muted-foreground">Question</label>
										<Input
											id={`configure-custom-label-${row.id}`}
											bind:value={row.label}
											placeholder="e.g. Do you smoke?"
											data-testid="configure-custom-label"
										/>
									</div>
									<div>
										<span class="mb-1 block text-xs text-muted-foreground">Answer type</span>
										<Select.Root type="single" value={row.type} onValueChange={(v) => (row.type = v as CustomFieldType)}>
											<Select.Trigger class="w-full" data-testid="configure-custom-type">{typeLabel(row.type)}</Select.Trigger>
											<Select.Content>
												{#each FIELD_TYPES as t (t.value)}
													<Select.Item value={t.value} label={t.label}>
														<span class="flex flex-col">
															<span class="font-medium">{t.label}</span>
															<span class="text-xs text-muted-foreground">{t.help}</span>
														</span>
													</Select.Item>
												{/each}
											</Select.Content>
										</Select.Root>
									</div>
								</div>

								{#if row.type === 'select'}
									<div class="mt-3">
										<label for={`configure-custom-options-${row.id}`} class="mb-1 block text-xs text-muted-foreground">
											Choices (one per line)
										</label>
										<textarea
											id={`configure-custom-options-${row.id}`}
											bind:value={row.optionsText}
											rows={3}
											class="w-full rounded border border-border bg-background px-3 py-2 text-sm"
											placeholder={'Zillow\nFriend\nSign'}
											data-testid="configure-custom-options"
										></textarea>
									</div>
								{/if}

								<label class="mt-3 flex items-center gap-2 text-sm text-foreground">
									<Checkbox
										checked={row.required}
										onCheckedChange={(v) => (row.required = v === true)}
										data-testid="configure-custom-required"
									/>
									Required (applicant must answer)
								</label>
							</div>
						{/each}
					</div>

					<Button variant="outline" class="gap-2" onclick={addRow} data-testid="configure-custom-add">
						<Plus class="h-4 w-4" /> Add a question
					</Button>
				</Card.Content>
			</Card.Root>

			<div class="flex items-center gap-3">
				<Button onclick={save} disabled={saveMutation.isPending} data-testid="configure-save">
					{saveMutation.isPending ? 'Saving…' : 'Save form'}
				</Button>
				<span class="text-xs text-muted-foreground">Applies to your shared application link immediately.</span>
			</div>
		</div>
	{/if}
</div>
