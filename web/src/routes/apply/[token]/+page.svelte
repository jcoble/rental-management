<script lang="ts">
	import { page } from '$app/stores';
	import { createQuery, createMutation } from '@tanstack/svelte-query';
	import {
		getApplicationContext,
		scanApplicationId,
		submitApplication,
		PublicApiError,
		type PublicApplicationProperty,
		type ScanIdResult,
		type SubmitApplicationBody,
	} from '$lib/api/public-applications';
	import { Building, ScanLine, CheckCircle2, Loader2, AlertCircle, Sparkles } from '@lucide/svelte';
	import * as Select from '$lib/components/ui/select';
	import DatePicker from '$lib/components/shared/DatePicker.svelte';
	import AddressAutocomplete from '$lib/components/shared/AddressAutocomplete.svelte';

	const token = $derived($page.params.token ?? '');

	// ── Load the apply context (company + properties/units) ─────────────────────
	const contextQuery = createQuery(() => ({
		queryKey: ['apply-context', token],
		queryFn: () => getApplicationContext(token),
		retry: false,
		enabled: !!token,
	}));

	const context = $derived(contextQuery.data);
	const properties = $derived<PublicApplicationProperty[]>(context?.properties ?? []);
	const notFound = $derived(
		contextQuery.isError && contextQuery.error instanceof PublicApiError && contextQuery.error.status === 404
	);

	// ── Form state ──────────────────────────────────────────────────────────────
	// shadcn Select binds strings; bits-ui treats '' as "no selection", so 'none' is the
	// "No preference" sentinel. propertyId/unitId derive the numeric ids used on submit.
	const NO_PREFERENCE = 'none';
	let propertyValue = $state(NO_PREFERENCE);
	let unitValue = $state(NO_PREFERENCE);
	const propertyId = $derived<number | null>(
		propertyValue === NO_PREFERENCE ? null : Number(propertyValue)
	);
	const unitId = $derived<number | null>(unitValue === NO_PREFERENCE ? null : Number(unitValue));
	let firstName = $state('');
	let lastName = $state('');
	let email = $state('');
	let phone = $state('');
	let dateOfBirth = $state('');
	let currentAddressLine1 = $state('');
	let currentAddressLine2 = $state('');
	let currentCity = $state('');
	let currentState = $state('');
	let currentPostalCode = $state('');
	let employer = $state('');
	let monthlyIncome = $state('');
	let desiredMoveInDate = $state('');
	let notes = $state('');
	let consentGiven = $state(false);

	// Track which fields the scanner auto-filled so we can flag them.
	let autoFilled = $state<Record<string, boolean>>({});
	let idExtractedFields = $state<Record<string, unknown> | null>(null);

	let formErrors = $state<Record<string, string>>({});
	let submitted = $state(false);

	// Units available for the chosen property.
	const selectedProperty = $derived(properties.find((p) => p.id === propertyId) ?? null);
	const availableUnits = $derived(selectedProperty?.units ?? []);

	function propertyOptionLabel(p: PublicApplicationProperty): string {
		return `${p.name}${p.addressLine1 ? ` — ${p.addressLine1}` : ''}${p.city ? `, ${p.city}` : ''}`;
	}
	const propertySelectLabel = $derived(
		selectedProperty ? propertyOptionLabel(selectedProperty) : 'No preference'
	);
	const unitSelectLabel = $derived.by(() => {
		if (unitValue === NO_PREFERENCE) {
			return availableUnits.length === 0 ? 'No specific unit' : 'No preference';
		}
		const u = availableUnits.find((x) => String(x.id) === unitValue);
		return u ? `Unit ${u.unitNumber}` : 'No preference';
	});

	function onPropertyChange() {
		// Reset unit when property changes; clear if no longer valid.
		if (!availableUnits.some((u) => String(u.id) === unitValue)) unitValue = NO_PREFERENCE;
	}

	// ── Scan-to-autofill ─────────────────────────────────────────────────────────
	let scanMessage = $state<string | null>(null);
	let scanIsError = $state(false);

	const scanMutation = createMutation(() => ({
		mutationFn: (file: File) => scanApplicationId(token, file),
		onSuccess: (result: ScanIdResult) => {
			applyScannedFields(result);
		},
		onError: (err) => {
			scanIsError = true;
			scanMessage = err instanceof Error ? err.message : 'Could not read that image. You can still type your details below.';
		},
	}));

	function applyScannedFields(result: ScanIdResult) {
		scanIsError = false;
		idExtractedFields = result.fields as Record<string, unknown>;
		if (!result.extracted || Object.keys(result.fields).length === 0) {
			scanMessage =
				"We couldn't read details from that image automatically. No problem — just fill in the form below.";
			return;
		}
		const filled: string[] = [];
		const f = result.fields;
		if (f.firstName?.value) { firstName = f.firstName.value; autoFilled.firstName = true; filled.push('first name'); }
		if (f.lastName?.value) { lastName = f.lastName.value; autoFilled.lastName = true; filled.push('last name'); }
		if (f.dateOfBirth?.value) { dateOfBirth = normalizeDate(f.dateOfBirth.value); autoFilled.dateOfBirth = true; filled.push('date of birth'); }
		if (f.currentAddress?.value) { currentAddressLine1 = f.currentAddress.value; autoFilled.currentAddressLine1 = true; filled.push('current address'); }
		if (f.employer?.value) { employer = f.employer.value; autoFilled.employer = true; filled.push('employer'); }
		if (f.monthlyIncome?.value) { monthlyIncome = f.monthlyIncome.value; autoFilled.monthlyIncome = true; filled.push('monthly income'); }
		autoFilled = { ...autoFilled };
		scanMessage = filled.length
			? `We filled in your ${filled.join(', ')}. Please check that everything looks right.`
			: "We couldn't read details from that image. Please fill in the form below.";
	}

	// Best-effort: coerce a scanned date into the yyyy-mm-dd a date input expects.
	function normalizeDate(value: string): string {
		const d = new Date(value);
		if (!isNaN(d.getTime())) return d.toISOString().slice(0, 10);
		return value;
	}

	function handleScanFile(event: Event) {
		const input = event.currentTarget as HTMLInputElement;
		const file = input.files?.[0];
		if (file) {
			scanMessage = null;
			scanIsError = false;
			scanMutation.mutate(file);
		}
		// Allow re-selecting the same file.
		input.value = '';
	}

	// When the user edits an auto-filled field, drop its "auto-filled" flag.
	function clearAutoFill(field: string) {
		if (autoFilled[field]) {
			autoFilled = { ...autoFilled, [field]: false };
		}
	}

	// ── Submit ────────────────────────────────────────────────────────────────────
	const submitMutation = createMutation(() => ({
		mutationFn: (body: SubmitApplicationBody) => submitApplication(token, body),
		onSuccess: () => {
			submitted = true;
			if (typeof window !== 'undefined') window.scrollTo({ top: 0, behavior: 'smooth' });
		},
	}));

	function validate(): boolean {
		const errors: Record<string, string> = {};
		if (!firstName.trim()) errors.firstName = 'Please enter your first name.';
		if (!lastName.trim()) errors.lastName = 'Please enter your last name.';
		if (!email.trim()) errors.email = 'Please enter your email.';
		else if (!/^[^@\s]+@[^@\s]+\.[^@\s]+$/.test(email.trim())) errors.email = 'Please enter a valid email.';
		if (!phone.trim()) errors.phone = 'Please enter your phone number.';
		if (!consentGiven) errors.consent = 'Please check the box to agree before submitting.';
		formErrors = errors;
		return Object.keys(errors).length === 0;
	}

	function onSubmit(event: SubmitEvent) {
		event.preventDefault();
		if (!validate()) {
			if (typeof window !== 'undefined') window.scrollTo({ top: document.body.scrollHeight, behavior: 'smooth' });
			return;
		}
		const income = monthlyIncome.trim() ? Number(monthlyIncome.replace(/[^0-9.]/g, '')) : null;
		const body: SubmitApplicationBody = {
			propertyId: propertyId ?? null,
			unitId: unitId ?? null,
			firstName: firstName.trim(),
			lastName: lastName.trim(),
			email: email.trim(),
			phone: phone.trim(),
			dateOfBirth: dateOfBirth || null,
			currentAddressLine1: currentAddressLine1.trim() || null,
			currentAddressLine2: currentAddressLine2.trim() || null,
			currentCity: currentCity.trim() || null,
			currentState: currentState.trim() || null,
			currentPostalCode: currentPostalCode.trim() || null,
			employer: employer.trim() || null,
			monthlyIncome: income != null && !isNaN(income) ? income : null,
			desiredMoveInDate: desiredMoveInDate || null,
			notes: notes.trim() || null,
			idExtractedFields,
			consentGiven,
		};
		submitMutation.mutate(body);
	}

	const submitErrorMessage = $derived(
		submitMutation.isError
			? submitMutation.error instanceof Error
				? submitMutation.error.message
				: 'Something went wrong. Please try again.'
			: null
	);
</script>

<svelte:head>
	<title>{context ? `Apply to ${context.managementCompanyName}` : 'Rental Application'}</title>
	<meta name="robots" content="noindex" />
</svelte:head>

<!-- Own scroll container: html/body are overflow:hidden globally, so this standalone public
     page must scroll itself (the form is tall on small screens). -->
<div class="h-dvh overflow-y-auto bg-background px-4 py-8 sm:py-12" data-testid="apply-page">
	<div class="mx-auto w-full max-w-2xl">
		{#if contextQuery.isLoading}
			<!-- Loading -->
			<div class="flex flex-col items-center justify-center gap-3 py-24 text-muted-foreground" data-testid="apply-loading">
				<Loader2 class="h-8 w-8 animate-spin" />
				<p class="text-sm">Loading…</p>
			</div>
		{:else if notFound}
			<!-- Invalid / expired link -->
			<div class="rounded-2xl border border-border bg-card p-8 text-center shadow-sm" data-testid="apply-not-found">
				<div class="mx-auto mb-4 flex h-14 w-14 items-center justify-center rounded-full bg-muted">
					<AlertCircle class="h-7 w-7 text-muted-foreground" />
				</div>
				<h1 class="text-xl font-bold text-foreground">This link isn't working</h1>
				<p class="mx-auto mt-2 max-w-md text-sm text-muted-foreground">
					This application link is invalid or has expired. Please contact the property manager for a new link.
				</p>
			</div>
		{:else if contextQuery.isError}
			<!-- Other error -->
			<div class="rounded-2xl border border-destructive/30 bg-destructive/10 p-8 text-center" data-testid="apply-error">
				<AlertCircle class="mx-auto mb-3 h-8 w-8 text-destructive" />
				<p class="font-medium text-destructive">We couldn't load this page</p>
				<p class="mt-1 text-sm text-muted-foreground">
					{contextQuery.error instanceof Error ? contextQuery.error.message : 'Please try again in a moment.'}
				</p>
				<button
					type="button"
					class="mt-5 inline-flex h-11 items-center justify-center rounded-lg border border-input bg-background px-5 text-sm font-medium transition-colors hover:bg-accent"
					onclick={() => contextQuery.refetch()}
					data-testid="apply-retry"
				>
					Try again
				</button>
			</div>
		{:else if submitted}
			<!-- Thank-you / success -->
			<div class="rounded-2xl border border-border bg-card p-8 text-center shadow-sm" data-testid="apply-success">
				<div class="mx-auto mb-4 flex h-16 w-16 items-center justify-center rounded-full bg-green-100 dark:bg-green-900/30">
					<CheckCircle2 class="h-9 w-9 text-green-600 dark:text-green-400" />
				</div>
				<h1 class="text-2xl font-bold text-foreground">Application submitted!</h1>
				<p class="mx-auto mt-3 max-w-md text-base text-muted-foreground">
					Thank you, {firstName || 'there'}. We've received your application
					{#if context}for {context.managementCompanyName}{/if}. The property manager will review it and
					reach out to you at <span class="font-medium text-foreground">{email}</span>.
				</p>
				<p class="mt-4 text-sm text-muted-foreground">You can close this page now.</p>
			</div>
		{:else}
			<!-- The application form -->
			<header class="mb-6 text-center" data-testid="apply-header">
				<div class="mx-auto mb-3 flex h-14 w-14 items-center justify-center rounded-2xl bg-primary/10">
					<Building class="h-7 w-7 text-primary" />
				</div>
				<h1 class="text-2xl font-bold text-foreground sm:text-3xl" data-testid="apply-company">
					Apply to {context?.managementCompanyName}
				</h1>
				<p class="mt-2 text-base text-muted-foreground">
					Fill out the short form below to apply. It only takes a few minutes.
				</p>
			</header>

			<!-- Scan to autofill -->
			<div class="mb-6 rounded-2xl border border-primary/30 bg-primary/5 p-5" data-testid="apply-scan">
				<div class="flex items-start gap-3">
					<div class="flex h-10 w-10 shrink-0 items-center justify-center rounded-xl bg-primary/15">
						<Sparkles class="h-5 w-5 text-primary" />
					</div>
					<div class="min-w-0 flex-1">
						<h2 class="text-base font-semibold text-foreground">Scan your ID to autofill</h2>
						<p class="mt-0.5 text-sm text-muted-foreground">
							Take a photo of your driver's license or a recent pay stub and we'll fill in the form for you.
							You can edit anything afterward.
						</p>
						<label
							class="mt-3 inline-flex h-12 cursor-pointer items-center justify-center gap-2 rounded-xl bg-primary px-5 text-sm font-semibold text-primary-foreground transition-opacity hover:opacity-90 {scanMutation.isPending ? 'pointer-events-none opacity-70' : ''}"
							data-testid="apply-scan-button"
						>
							{#if scanMutation.isPending}
								<Loader2 class="h-5 w-5 animate-spin" />
								Reading…
							{:else}
								<ScanLine class="h-5 w-5" />
								Scan your ID
							{/if}
							<input
								type="file"
								accept="image/*"
								capture="environment"
								class="sr-only"
								onchange={handleScanFile}
								disabled={scanMutation.isPending}
								data-testid="apply-scan-input"
							/>
						</label>
						{#if scanMessage}
							<p
								class="mt-3 text-sm {scanIsError ? 'text-destructive' : 'text-green-700 dark:text-green-400'}"
								data-testid="apply-scan-message"
							>
								{scanMessage}
							</p>
						{/if}
					</div>
				</div>
			</div>

			<form onsubmit={onSubmit} class="space-y-5" data-testid="apply-form" novalidate>
				<!-- Property / unit picker -->
				{#if properties.length > 0}
					<div class="rounded-2xl border border-border bg-card p-5">
						<h2 class="mb-3 text-base font-semibold text-foreground">Where would you like to live?</h2>
						<div class="grid gap-4 sm:grid-cols-2">
							<div class="block">
								<span class="mb-1.5 block text-sm font-medium text-foreground">Property</span>
								<Select.Root
									type="single"
									bind:value={propertyValue}
									onValueChange={onPropertyChange}
								>
									<Select.Trigger class="h-12 w-full text-base" data-testid="apply-property-select">
										{propertySelectLabel}
									</Select.Trigger>
									<Select.Content>
										<Select.Item value={NO_PREFERENCE} label="No preference">No preference</Select.Item>
										{#each properties as p (p.id)}
											<Select.Item value={String(p.id)} label={propertyOptionLabel(p)}>
												{propertyOptionLabel(p)}
											</Select.Item>
										{/each}
									</Select.Content>
								</Select.Root>
							</div>
							<div class="block">
								<span class="mb-1.5 block text-sm font-medium text-foreground">Unit</span>
								<Select.Root type="single" bind:value={unitValue} disabled={availableUnits.length === 0}>
									<Select.Trigger class="h-12 w-full text-base" data-testid="apply-unit-select">
										{unitSelectLabel}
									</Select.Trigger>
									<Select.Content>
										<Select.Item value={NO_PREFERENCE} label={availableUnits.length === 0 ? 'No specific unit' : 'No preference'}>
											{availableUnits.length === 0 ? 'No specific unit' : 'No preference'}
										</Select.Item>
										{#each availableUnits as u (u.id)}
											<Select.Item value={String(u.id)} label={`Unit ${u.unitNumber}`}>Unit {u.unitNumber}</Select.Item>
										{/each}
									</Select.Content>
								</Select.Root>
							</div>
						</div>
					</div>
				{/if}

				<!-- Your details -->
				<div class="rounded-2xl border border-border bg-card p-5">
					<h2 class="mb-4 text-base font-semibold text-foreground">Your details</h2>
					<div class="grid gap-4 sm:grid-cols-2">
						{@render field('First name', 'firstName', firstName, (v) => (firstName = v), { required: true })}
						{@render field('Last name', 'lastName', lastName, (v) => (lastName = v), { required: true })}
						{@render field('Email', 'email', email, (v) => (email = v), { required: true, type: 'email' })}
						{@render field('Phone', 'phone', phone, (v) => (phone = v), { required: true, type: 'tel' })}
						{@render field('Date of birth', 'dateOfBirth', dateOfBirth, (v) => (dateOfBirth = v), { type: 'date' })}
						{@render field('Desired move-in date', 'desiredMoveInDate', desiredMoveInDate, (v) => (desiredMoveInDate = v), { type: 'date' })}
					</div>
					<div class="mt-4 grid gap-4 sm:grid-cols-2">
						<label class="block sm:col-span-2">
							<span class="mb-1.5 block text-sm font-medium text-foreground">
								Current address
								{#if autoFilled.currentAddressLine1}
									<span class="ml-2 inline-flex items-center gap-1 rounded-full bg-green-100 px-2 py-0.5 text-[11px] font-medium text-green-700 dark:bg-green-900/30 dark:text-green-400" data-testid="apply-autofilled-currentAddressLine1">
										<Sparkles class="h-3 w-3" /> Auto-filled
									</span>
								{/if}
							</span>
							<AddressAutocomplete
								value={currentAddressLine1}
								onchange={(v) => { currentAddressLine1 = v; clearAutoFill('currentAddressLine1'); }}
								onresolved={(a) => {
									if (a.city) currentCity = a.city;
									if (a.state) currentState = a.state;
									if (a.zip) currentPostalCode = a.zip;
									clearAutoFill('currentAddressLine1');
								}}
								placeholder="Street address"
								testid="apply-currentAddressLine1-input"
								class="h-12 w-full rounded-xl border border-input bg-background px-3 text-base focus:border-ring focus:outline-none focus:ring-2 focus:ring-ring/40"
							/>
						</label>
						{@render field('Apt / Suite / Unit #', 'currentAddressLine2', currentAddressLine2, (v) => (currentAddressLine2 = v), { full: true })}
						{@render field('City', 'currentCity', currentCity, (v) => (currentCity = v))}
						{@render field('State', 'currentState', currentState, (v) => (currentState = v))}
						{@render field('ZIP', 'currentPostalCode', currentPostalCode, (v) => (currentPostalCode = v))}
					</div>
				</div>

				<!-- Employment -->
				<div class="rounded-2xl border border-border bg-card p-5">
					<h2 class="mb-4 text-base font-semibold text-foreground">Employment & income</h2>
					<div class="grid gap-4 sm:grid-cols-2">
						{@render field('Employer', 'employer', employer, (v) => (employer = v))}
						{@render field('Monthly income', 'monthlyIncome', monthlyIncome, (v) => (monthlyIncome = v), { type: 'text', inputMode: 'decimal', prefix: '$' })}
					</div>
				</div>

				<!-- Notes -->
				<div class="rounded-2xl border border-border bg-card p-5">
					<label class="block">
						<span class="mb-1.5 block text-base font-semibold text-foreground">Anything else we should know?</span>
						<textarea
							bind:value={notes}
							rows="4"
							placeholder="Pets, co-applicants, questions… (optional)"
							class="w-full rounded-xl border border-input bg-background px-3 py-2.5 text-base text-foreground placeholder:text-muted-foreground focus:border-ring focus:outline-none focus:ring-2 focus:ring-ring/40"
							data-testid="apply-notes-input"
						></textarea>
					</label>
				</div>

				<!-- Consent -->
				<div class="rounded-2xl border border-border bg-card p-5" data-testid="apply-consent">
					<label class="flex cursor-pointer items-start gap-3">
						<input
							type="checkbox"
							bind:checked={consentGiven}
							class="mt-1 h-5 w-5 shrink-0 rounded border-input text-primary focus:ring-2 focus:ring-ring/40"
							data-testid="apply-consent-checkbox"
						/>
						<span class="text-sm leading-relaxed text-foreground">
							I certify that the information I've provided is true and complete. I authorize
							{#if context}<span class="font-medium">{context.managementCompanyName}</span>{:else}the property
								manager{/if} to verify this information and to obtain consumer reports, including a credit
							and background check, in connection with my rental application, as permitted under the Fair
							Credit Reporting Act (FCRA). I understand a report may be requested now and during my tenancy.
						</span>
					</label>
					{#if formErrors.consent}
						<p class="mt-2 pl-8 text-sm text-destructive" data-testid="apply-consent-error">{formErrors.consent}</p>
					{/if}
				</div>

				{#if submitErrorMessage}
					<div class="rounded-xl border border-destructive/30 bg-destructive/10 p-4 text-sm text-destructive" data-testid="apply-submit-error">
						{submitErrorMessage}
					</div>
				{/if}

				<button
					type="submit"
					disabled={submitMutation.isPending}
					class="flex h-14 w-full items-center justify-center gap-2 rounded-2xl bg-primary text-base font-semibold text-primary-foreground transition-opacity hover:opacity-90 disabled:opacity-60"
					data-testid="apply-submit"
				>
					{#if submitMutation.isPending}
						<Loader2 class="h-5 w-5 animate-spin" />
						Submitting…
					{:else}
						Submit application
					{/if}
				</button>
				<p class="pb-4 text-center text-xs text-muted-foreground">
					Your information is sent securely to the property manager.
				</p>
			</form>
		{/if}
	</div>
</div>

{#snippet field(
	label: string,
	name: string,
	value: string,
	setter: (v: string) => void,
	opts: { required?: boolean; type?: string; inputMode?: 'text' | 'decimal' | 'tel' | 'email'; full?: boolean; prefix?: string; address?: boolean } = {}
)}
	<label class="block {opts.full ? 'sm:col-span-2' : ''}">
		<span class="mb-1.5 block text-sm font-medium text-foreground">
			{label}{#if opts.required}<span class="text-destructive"> *</span>{/if}
			{#if autoFilled[name]}
				<span class="ml-2 inline-flex items-center gap-1 rounded-full bg-green-100 px-2 py-0.5 text-[11px] font-medium text-green-700 dark:bg-green-900/30 dark:text-green-400" data-testid="apply-autofilled-{name}">
					<Sparkles class="h-3 w-3" /> Auto-filled
				</span>
			{/if}
		</span>
		<div class="relative">
			{#if opts.prefix}
				<span class="pointer-events-none absolute left-3 top-1/2 -translate-y-1/2 text-base text-muted-foreground">{opts.prefix}</span>
			{/if}
			{#if opts.type === 'date'}
				<DatePicker
					value={value}
					onchange={(v) => { setter(v); clearAutoFill(name); }}
					placeholder={label}
					testid="apply-{name}-input"
				/>
			{:else if opts.address}
				<AddressAutocomplete
					value={value}
					onchange={(v) => { setter(v); clearAutoFill(name); }}
					onresolved={(a) => {
						setter([a.line1, a.city, [a.state, a.zip].filter(Boolean).join(' ')].filter(Boolean).join(', '));
						clearAutoFill(name);
					}}
					placeholder={label}
					testid="apply-{name}-input"
				/>
			{:else}
				<input
					type={opts.type ?? 'text'}
					value={value}
					inputmode={opts.inputMode}
					oninput={(e) => { setter((e.currentTarget as HTMLInputElement).value); clearAutoFill(name); }}
					class="h-12 w-full rounded-xl border border-input bg-background text-base text-foreground placeholder:text-muted-foreground focus:border-ring focus:outline-none focus:ring-2 focus:ring-ring/40 {opts.prefix ? 'pl-7 pr-3' : 'px-3'}"
					data-testid="apply-{name}-input"
				/>
			{/if}
		</div>
		{#if formErrors[name]}
			<p class="mt-1 text-sm text-destructive" data-testid="apply-{name}-error">{formErrors[name]}</p>
		{/if}
	</label>
{/snippet}
