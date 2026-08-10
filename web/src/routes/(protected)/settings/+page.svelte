<script lang="ts">
	import { createMutation, createQuery, useQueryClient } from '@tanstack/svelte-query';
	import { portfolios } from '$lib/api/endpoints/portfolios';
	import { notifications } from '$lib/api/endpoints/notifications';
	import type { NotificationSeverity } from '$lib/api/types/notification';
	import { getAuthState, hasCapability } from '$lib/stores/auth.svelte';
	import { notificationStore } from '$lib/stores/notifications.svelte';
	import { getCurrentPortfolioId } from '$lib/stores/portfolio.svelte';
	import { showSuccess, showError, apiErrorMessage } from '$lib/utils/toast';
	import { formatStatusLabel } from '$lib/utils/status-labels';
	import { settingsSchema, parseForm } from '$lib/schemas';
	import { Button } from '$lib/components/ui/button';
	import { Input } from '$lib/components/ui/input';
	import { Checkbox } from '$lib/components/ui/checkbox';
	import * as Select from '$lib/components/ui/select';
	import * as Card from '$lib/components/ui/card';
	import * as Tabs from '$lib/components/ui/tabs';
	import TimeZoneSelect from '$lib/components/shared/TimeZoneSelect.svelte';
	import SandboxBanner from '$lib/components/SandboxBanner.svelte';
	import WalkMeThrough from '$lib/components/onboarding/WalkMeThrough.svelte';
	import {
		SETTINGS_SECTIONS,
		settingsSection,
		wizardDocsHref,
		wizardStep,
		type SettingsSectionKey,
		type SettingsSectionMeta,
	} from '$lib/onboarding/wizard-steps';
	import { resolveSettingsTab, isLegacySettingsAnchor } from '$lib/onboarding/settings-anchor-map';
	import {
		ArrowRight,
		Bell,
		Building,
		BadgeDollarSign,
		History,
		Lightbulb,
		MessageSquare,
		Shield,
		Upload,
		Wand2,
	} from '@lucide/svelte';
	import { page } from '$app/state';
	import { goto } from '$app/navigation';

	const queryClient = useQueryClient();
	const authState = getAuthState();
	const portfolioId = $derived(getCurrentPortfolioId());
	const isAdmin = $derived(hasCapability('security.manage', 'billing.manage'));
	const canBroadcast = $derived(hasCapability('rentals.manage', 'leasing.onboarding.manage'));
	const canManageTeamRouting = $derived(hasCapability('notifications.manage'));
	const canManageAutomations = $derived(hasCapability('notifications.manage'));
	const canManageTenantNotices = $derived(hasCapability('notifications.manage'));
	const canManageAiProvider = $derived(hasCapability('integrations.manage'));

	// Tabbed hub: each section is one tab. The active tab is mirrored to the URL hash so a deep-link
	// (e.g. /settings#messaging) lands on the right tab and a refresh keeps your place. WalkMeThrough
	// links back via ?from=settings to the section's anchor; those anchors live inside the tab panels.
	const sectionIcons = {
		Building,
		Bell,
		Wand2,
		MessageSquare,
		Shield,
		BadgeDollarSign,
		Upload,
		History,
	} as const;
	const validTabs = SETTINGS_SECTIONS.map((s) => s.key) as string[];
	let activeTab = $state('portfolio');

	// Open the tab named by the URL hash. The getting-started checklist + onboarding wizard deep-link
	// with tab-key hashes (e.g. #notifications); the wizard's "back to settings" return and old
	// bookmarks still use legacy section-anchor IDs. resolveSettingsTab() (the shared contract) maps
	// both to the owning tab — Settings is the single source of truth for "which tab owns which section".
	$effect(() => {
		const hash = page.url.hash.replace('#', '');
		const targetTab = resolveSettingsTab(hash, validTabs);
		if (targetTab && targetTab !== activeTab) {
			activeTab = targetTab;
		}
		// For a legacy section anchor, scroll the section into view once its (now-active) tab panel has
		// rendered. Tab-key hashes need no scroll — the tab itself is the destination.
		if (targetTab && isLegacySettingsAnchor(hash) && typeof document !== 'undefined') {
			requestAnimationFrame(() =>
				requestAnimationFrame(() => {
					document.getElementById(hash)?.scrollIntoView({ behavior: 'smooth', block: 'start' });
				})
			);
		}
	});

	function selectTab(value: string) {
		activeTab = value;
		if (typeof window !== 'undefined') {
			const url = new URL(page.url);
			url.hash = value;
			goto(url.pathname + url.search + url.hash, {
				replaceState: true,
				noScroll: true,
				keepFocus: true,
			});
		}
	}

	// Resolve a section's ELI5 copy. Where a section maps to a wizard step we reuse that step's
	// verbatim explanation/where-to-find so Settings and the wizard never drift apart.
	function sectionDocsHref(section: SettingsSectionMeta): string {
		const step = section.walkThrough ? wizardStep(section.walkThrough) : undefined;
		return wizardDocsHref(step ?? { docsSlug: section.docsSlug } as any);
	}

	// Look up a section's metadata BY KEY (not by array position) so each tab's ELI5 intro is bound to
	// the same key as its <Tabs.Content value=…>. Reordering SETTINGS_SECTIONS can no longer slip the
	// wrong intro above a tab. Keys are compile-time-checked against SettingsSectionKey; the registry
	// is exhaustive, so a miss is a developer error — fail loudly rather than render a blank panel.
	function section(key: SettingsSectionKey): SettingsSectionMeta {
		const meta = settingsSection(key);
		if (!meta) throw new Error(`Settings section "${key}" is missing from SETTINGS_SECTIONS`);
		return meta;
	}

	let saveSucceeded = $state(false);
	let formErrors = $state<Record<string, string>>({});
	let broadcastForm = $state<{
		title: string;
		message: string;
		severity: NotificationSeverity;
	}>({
		title: '',
		message: '',
		severity: 'Info'
	});

	const portfolioQuery = createQuery(() => ({
		queryKey: ['portfolio', portfolioId],
		enabled: authState.isAuthenticated && portfolioId > 0,
		queryFn: () => portfolios.get(portfolioId),
	}));

	const lateFeeSettingsQuery = createQuery(() => ({
		queryKey: ['automation-settings', 'late-fees', portfolioId],
		enabled: authState.isAuthenticated && portfolioId > 0 && canManageAutomations,
		queryFn: () => notifications.automationSettings.getLateFees(),
	}));

	let form = $state({
		name: '',
		description: '',
		managementCompanyName: '',
		timeZone: 'America/New_York',
		status: 'Active',
	});

	// Structured view of the portfolio settings JSON. The raw JSON blob is never shown to the
	// landlord — known keys are real inputs and anything else is an "advanced" key/value row.
	//   - rentCollectionDay → number field
	//   - prorationConvention → rent proration rule for partial first/final periods
	//   - messaging.{email,sms} → toggles (also read by the messages composer)
	//   - everything else (one level deep) → labeled "advanced" rows OR preserved untouched
	type CustomRow = { id: number; key: string; value: string };
	type ProrationConvention = 'ActualDays' | 'ThirtyDay';
	const MANAGED_KEYS = ['rentCollectionDay', 'prorationConvention', 'messaging'];
	// Notification preferences now live in their canonical tables. Discard the retired nested JSON
	// shape when this form is saved instead of carrying two notification models forward.
	const REMOVED_KEYS = ['notifications'];

	let settingsModel = $state<{
		rentCollectionDay: number;
		prorationConvention: ProrationConvention;
		messaging: { email: boolean; sms: boolean };
		customRows: CustomRow[];
		preserved: Record<string, unknown>;
	}>({
		rentCollectionDay: 1,
		prorationConvention: 'ActualDays',
		messaging: { email: false, sms: false },
		customRows: [],
		preserved: {},
	});

	let lateFeeSettingsForm = $state({
		enableLateFees: false,
		lateFeeGraceDays: 5,
	});

	let showAdvanced = $state(false);
	let nextRowId = 0;

	// Parse the stored settings JSON into the structured model. Tolerant of malformed JSON
	// (falls back to defaults) so a landlord is never locked out by an invalid value.
	function loadSettingsModel(settingsJson: string | null | undefined) {
		let parsed: Record<string, unknown> = {};
		let rowId = 0;
		try {
			const candidate = JSON.parse(settingsJson || '{}');
			if (candidate && typeof candidate === 'object' && !Array.isArray(candidate)) {
				parsed = candidate as Record<string, unknown>;
			}
		} catch {
			parsed = {};
		}

		const rawDay = parsed.rentCollectionDay;
		const rentCollectionDay =
			typeof rawDay === 'number' && Number.isFinite(rawDay) ? rawDay : 1;

		const prorationConvention =
			parsed.prorationConvention === 'ThirtyDay' ? 'ThirtyDay' : 'ActualDays';

		const m =
			parsed.messaging && typeof parsed.messaging === 'object' && !Array.isArray(parsed.messaging)
				? (parsed.messaging as Record<string, unknown>)
				: {};
		const messaging = { email: m.email === true, sms: m.sms === true };

		const preserved: Record<string, unknown> = {};
		const customRows: CustomRow[] = [];
		for (const [key, value] of Object.entries(parsed)) {
			if (MANAGED_KEYS.includes(key)) continue;
			if (REMOVED_KEYS.includes(key)) continue;
			// Surface scalar extras as editable advanced rows; keep nested/complex values
			// untouched in `preserved` so the structured editor never lossily flattens them.
			if (value === null || ['string', 'number', 'boolean'].includes(typeof value)) {
				customRows.push({ id: rowId++, key, value: value === null ? '' : String(value) });
			} else {
				preserved[key] = value;
			}
		}
		nextRowId = rowId;
		showAdvanced = customRows.length > 0;

		settingsModel = { rentCollectionDay, prorationConvention, messaging, customRows, preserved };
	}

	// Reassemble the settings JSON from the structured model, preserving untouched keys. Advanced
	// rows are coerced back to number/boolean/null when they look like one, else kept as strings.
	function serializeSettings(): string {
		const out: Record<string, unknown> = { ...settingsModel.preserved };
		// Number inputs become null when cleared; keep the stored shape as a sane day-of-month.
		const day = Number(settingsModel.rentCollectionDay);
		out.rentCollectionDay = Number.isFinite(day) && day >= 1 ? day : 1;
		out.prorationConvention = settingsModel.prorationConvention;
		out.messaging = { email: settingsModel.messaging.email, sms: settingsModel.messaging.sms };
		for (const row of settingsModel.customRows) {
			const key = row.key.trim();
			if (!key || MANAGED_KEYS.includes(key) || REMOVED_KEYS.includes(key)) continue;
			out[key] = coerceValue(row.value);
		}
		return JSON.stringify(out, null, 2);
	}

	function coerceValue(raw: string): unknown {
		const trimmed = raw.trim();
		if (trimmed === '') return '';
		if (trimmed === 'true') return true;
		if (trimmed === 'false') return false;
		if (trimmed === 'null') return null;
		// Only treat as a number when it round-trips exactly (avoids mangling things like phone numbers).
		const asNum = Number(trimmed);
		if (Number.isFinite(asNum) && String(asNum) === trimmed) return asNum;
		return raw;
	}

	function addCustomRow() {
		settingsModel.customRows = [...settingsModel.customRows, { id: nextRowId++, key: '', value: '' }];
		showAdvanced = true;
	}

	function removeCustomRow(id: number) {
		settingsModel.customRows = settingsModel.customRows.filter((row) => row.id !== id);
	}

	$effect(() => {
		if (portfolioQuery.data) {
			form = {
				name: portfolioQuery.data.name,
				description: portfolioQuery.data.description || '',
				managementCompanyName: portfolioQuery.data.managementCompanyName || '',
				timeZone: portfolioQuery.data.timeZone || 'America/New_York',
				status: portfolioQuery.data.status,
			};
			loadSettingsModel(portfolioQuery.data.settings);
		}
	});

	$effect(() => {
		if (lateFeeSettingsQuery.data) {
			lateFeeSettingsForm = {
				enableLateFees: lateFeeSettingsQuery.data.enableLateFees,
				lateFeeGraceDays: lateFeeSettingsQuery.data.lateFeeGraceDays,
			};
		}
	});

	const updateMutation = createMutation(() => ({
		mutationFn: () => portfolios.update(portfolioId, {
			name: form.name,
			description: form.description,
			managementCompanyName: form.managementCompanyName,
			timeZone: form.timeZone,
			status: form.status as any,
			settings: serializeSettings(),
		}),
		onSuccess: () => {
			queryClient.invalidateQueries({ queryKey: ['portfolio', portfolioId] });
			queryClient.invalidateQueries({ queryKey: ['portfolios'] });
			showSuccess('Settings saved.');
			saveSucceeded = true;
		},
		onError: (err) => showError(apiErrorMessage(err)),
	}));

	const broadcastMutation = createMutation(() => ({
		mutationFn: () =>
			notifications.broadcast({
				title: broadcastForm.title.trim(),
				message: broadcastForm.message.trim(),
				severity: broadcastForm.severity
		}),
		onSuccess: () => {
			broadcastForm = { title: '', message: '', severity: 'Info' };
			queryClient.invalidateQueries({ queryKey: ['notifications'] });
			queryClient.invalidateQueries({ queryKey: ['notifications-unread-count'] });
			void notificationStore.refresh();
			showSuccess('Broadcast notification sent.');
		},
		onError: (err) => showError(apiErrorMessage(err)),
	}));

	const lateFeeSettingsMutation = createMutation(() => ({
		mutationFn: () =>
			notifications.automationSettings.updateLateFees({
				enableLateFees: lateFeeSettingsForm.enableLateFees,
				lateFeeGraceDays: Math.max(0, Math.min(31, Number(lateFeeSettingsForm.lateFeeGraceDays) || 0)),
			}),
		onSuccess: (saved) => {
			lateFeeSettingsForm = {
				enableLateFees: saved.enableLateFees,
				lateFeeGraceDays: saved.lateFeeGraceDays,
			};
			queryClient.setQueryData(['automation-settings', 'late-fees', portfolioId], saved);
			queryClient.invalidateQueries({ queryKey: ['portfolio', portfolioId] });
			queryClient.invalidateQueries({ queryKey: ['portfolios'] });
			showSuccess('Late fee automation settings saved.');
		},
		onError: (err) => showError(apiErrorMessage(err)),
	}));
</script>

<svelte:head>
	<title>Settings - Rental Command</title>
</svelte:head>

{#snippet sectionIntro(section: SettingsSectionMeta)}
	{@const Icon = sectionIcons[section.icon]}
	<div
		class="flex items-start gap-3 rounded-lg border border-border bg-muted/50 p-4"
		data-testid={`settings-intro-${section.key}`}
	>
		<Icon class="mt-0.5 h-5 w-5 shrink-0 text-primary" />
		<div class="min-w-0 flex-1">
			<p class="text-sm font-semibold text-foreground">{section.title}</p>
			<p class="mt-0.5 text-sm text-muted-foreground">{section.intro}</p>
			<p class="mt-1 text-sm text-muted-foreground">{section.why}</p>
			{#if section.whereToFind}
				<p class="mt-2 flex items-start gap-1.5 text-xs text-muted-foreground">
					<Lightbulb class="mt-0.5 h-3.5 w-3.5 shrink-0" />
					<span><span class="font-medium">Where to find this:</span> {section.whereToFind}</span>
				</p>
			{/if}
			<div class="mt-2 flex flex-wrap items-center gap-2">
				{#if section.walkThrough}
					<WalkMeThrough step={section.walkThrough} />
				{/if}
				<a
					href={sectionDocsHref(section)}
					class="text-xs font-medium text-primary underline-offset-4 hover:underline"
					data-testid={`settings-learn-more-${section.key}`}
				>
					Learn more
				</a>
			</div>
		</div>
	</div>
{/snippet}

<div class="box-border h-full overflow-y-auto p-6 pb-20">
	<div class="mb-4">
		<h1 class="text-2xl font-bold">Settings</h1>
		<p class="text-sm text-muted-foreground">
			Everything that runs your account, broken into simple sections. Pick a tab below.
		</p>
	</div>

	<!-- Account mode: Sandbox vs Live + the one-way Go Live flow. Stays above the tabs so it is
	     always visible — it applies to the whole account, not one section. -->
	<div class="mb-4 max-w-4xl" data-testid="settings-sandbox-section">
		<SandboxBanner variant="card" />
	</div>

	<Tabs.Root bind:value={activeTab} class="w-full max-w-4xl">
		<Tabs.List class="mb-5 flex flex-wrap" data-testid="settings-tabs">
			{#each SETTINGS_SECTIONS as section (section.key)}
				<Tabs.Trigger
					value={section.key}
					onclick={() => selectTab(section.key)}
					data-testid={`settings-tab-${section.key}`}
				>
					{section.label}
				</Tabs.Trigger>
			{/each}
		</Tabs.List>

		<!-- ─────────────────────────── PORTFOLIO ─────────────────────────── -->
		<Tabs.Content value="portfolio">
			<div class="space-y-4">
				{@render sectionIntro(section('portfolio'))}

				<Card.Root class="gap-0 py-0" id="settings-portfolio-basics" data-coach="settings-portfolio" data-testid="settings-portfolio-basics">
					<Card.Content class="p-5">
						<p class="mb-3 text-sm font-semibold">Portfolio basics</p>
						<div class="grid gap-3 md:grid-cols-2">
							<div class="md:col-span-2">
								<label for="settings-name" class="mb-1 block text-xs text-muted-foreground">Portfolio Name</label>
								<Input id="settings-name" bind:value={form.name} data-testid="settings-name-input" />
								{#if formErrors.name}<p class="mt-1 text-xs text-destructive" data-testid="settings-name-error">{formErrors.name}</p>{/if}
							</div>
							<div class="md:col-span-2">
								<label for="settings-description" class="mb-1 block text-xs text-muted-foreground">Description</label>
								<textarea id="settings-description" bind:value={form.description} rows={3} class="w-full rounded border border-border bg-background px-3 py-2 text-sm"></textarea>
							</div>
							<div>
								<label for="settings-company" class="mb-1 block text-xs text-muted-foreground">Management Company</label>
								<Input id="settings-company" bind:value={form.managementCompanyName} />
							</div>
							<div>
								<label for="settings-timezone" class="mb-1 block text-xs text-muted-foreground">Time Zone</label>
								<TimeZoneSelect id="settings-timezone" testid="settings-timezone" bind:value={form.timeZone} />
							</div>
							<div>
								<label for="settings-status" class="mb-1 block text-xs text-muted-foreground">Status</label>
								<Select.Root type="single" bind:value={form.status}>
									<Select.Trigger class="w-full" id="settings-status">
										{form.status ? formatStatusLabel(form.status) : 'Select status'}
									</Select.Trigger>
									<Select.Content>
										<Select.Item value="Onboarding" label="Onboarding">Onboarding</Select.Item>
										<Select.Item value="Active" label="Active">Active</Select.Item>
										<Select.Item value="Archived" label="Archived">Archived</Select.Item>
									</Select.Content>
								</Select.Root>
							</div>
						</div>

						<!-- Operational defaults (typed) -->
						<div class="mt-6 rounded-lg border border-border bg-muted/30 p-4" data-testid="settings-operational">
							<p class="text-sm font-semibold">Everyday defaults</p>
							<p class="mb-3 text-xs text-muted-foreground">The basics the app uses across this portfolio.</p>

							<div class="grid gap-3 md:grid-cols-2">
								<div>
									<label for="settings-rent-collection-day" class="mb-1 block text-xs text-muted-foreground">
										Rent collection day of month
									</label>
									<Input
										id="settings-rent-collection-day"
										type="text"
										inputmode="numeric"
										maxlength={2}
										mask="integer"
										bind:value={settingsModel.rentCollectionDay}
										data-testid="settings-rent-collection-day"
									/>
									<p class="mt-1 text-xs text-muted-foreground">The day each month rent is considered due (1–31).</p>
								</div>
								<div>
									<label for="settings-proration-convention" class="mb-1 block text-xs text-muted-foreground">
										Partial-month rent
									</label>
									<Select.Root type="single" bind:value={settingsModel.prorationConvention}>
										<Select.Trigger
											class="w-full"
											id="settings-proration-convention"
											data-testid="settings-proration-convention"
										>
											{settingsModel.prorationConvention === 'ThirtyDay' ? '30-day month' : 'Actual days in month'}
										</Select.Trigger>
										<Select.Content>
											<Select.Item value="ActualDays" label="Actual days in month">
												Actual days in month
											</Select.Item>
											<Select.Item value="ThirtyDay" label="30-day month">
												30-day month
											</Select.Item>
										</Select.Content>
									</Select.Root>
									<p class="mt-1 text-xs text-muted-foreground">Used for prorated first and final rent charges.</p>
								</div>
							</div>
						</div>

						<!-- Advanced add-a-field escape hatch (typed key/value rows; never a raw JSON blob) -->
						<div class="mt-6 rounded-lg border border-border bg-muted/30 p-4" data-testid="settings-advanced">
							<div class="flex items-center justify-between gap-3">
								<div>
									<p class="text-sm font-semibold">Advanced</p>
									<p class="text-xs text-muted-foreground">
										Extra system settings as name/value pairs. Most landlords never need these.
									</p>
								</div>
								{#if !showAdvanced && settingsModel.customRows.length === 0}
									<Button
										variant="outline"
										size="sm"
										onclick={() => (showAdvanced = true)}
										data-testid="settings-advanced-show"
									>
										Show advanced
									</Button>
								{/if}
							</div>

							{#if showAdvanced || settingsModel.customRows.length > 0}
								<div class="mt-3 space-y-2">
									{#if settingsModel.customRows.length === 0}
										<p class="text-xs text-muted-foreground" data-testid="settings-advanced-empty">
											No advanced settings. Add one only if you were told to.
										</p>
									{/if}

									{#each settingsModel.customRows as row (row.id)}
										<div class="flex items-start gap-2" data-testid="settings-advanced-row">
											<div class="flex-1">
												<label for={`settings-advanced-key-${row.id}`} class="sr-only">Setting name</label>
												<Input
													id={`settings-advanced-key-${row.id}`}
													bind:value={row.key}
													placeholder="Setting name"
													autocomplete="off"
													data-testid="settings-advanced-key"
												/>
											</div>
											<div class="flex-1">
												<label for={`settings-advanced-value-${row.id}`} class="sr-only">Value</label>
												<Input
													id={`settings-advanced-value-${row.id}`}
													bind:value={row.value}
													placeholder="Value"
													autocomplete="off"
													data-testid="settings-advanced-value"
												/>
											</div>
											<Button
												variant="ghost"
												size="sm"
												onclick={() => removeCustomRow(row.id)}
												aria-label="Remove setting"
												data-testid="settings-advanced-remove"
											>
												Remove
											</Button>
										</div>
									{/each}

									<Button
										variant="outline"
										size="sm"
										onclick={addCustomRow}
										data-testid="settings-advanced-add"
									>
										Add field
									</Button>
								</div>
							{/if}
						</div>

						<div class="mt-4 flex items-center gap-3">
							<Button onclick={() => {
							const result = parseForm(settingsSchema, form);
							if (result.errors) { formErrors = result.errors; return; }
							formErrors = {};
							saveSucceeded = false;
							updateMutation.mutate();
						}} disabled={updateMutation.isPending} data-testid="settings-portfolio-save">Save portfolio</Button>
							{#if saveSucceeded && !updateMutation.isPending}
								<span class="text-xs text-[var(--success)]">Settings saved successfully.</span>
							{/if}
						</div>
						<p class="mt-2 text-xs text-muted-foreground">
							Saves the portfolio name, time zone, status, everyday defaults, and advanced fields together.
						</p>
					</Card.Content>
				</Card.Root>
			</div>
		</Tabs.Content>

		<!-- ─────────────────────────── NOTIFICATIONS ─────────────────────────── -->
		<Tabs.Content value="notifications">
			<div class="space-y-4">
				{@render sectionIntro(section('notifications'))}

				<div class="grid gap-4 lg:grid-cols-3" data-coach="settings-notifications" data-testid="notification-settings-landing">
					<a class="group rounded-xl border border-border bg-card p-5 transition-colors hover:border-primary/50" href="/settings/notifications/my-alerts">
						<div class="flex items-start justify-between gap-3">
							<div>
								<h2 class="font-semibold">My alerts</h2>
								<p class="mt-2 text-sm text-muted-foreground">Choose how alerts reach your own account: in-app, mobile push, email, or SMS.</p>
							</div>
							<ArrowRight class="mt-0.5 size-4 shrink-0 text-muted-foreground transition-transform group-hover:translate-x-0.5" />
						</div>
					</a>
					{#if canManageTeamRouting}<a class="group rounded-xl border border-border bg-card p-5 transition-colors hover:border-primary/50" href="/settings/notifications/team-routing">
						<div class="flex items-start justify-between gap-3">
							<div>
								<h2 class="font-semibold">Team routing</h2>
								<p class="mt-2 text-sm text-muted-foreground">Set which named team members handle money, leasing, work orders, owner decisions, and security.</p>
							</div>
							<ArrowRight class="mt-0.5 size-4 shrink-0 text-muted-foreground transition-transform group-hover:translate-x-0.5" />
						</div>
					</a>{/if}
					{#if canManageTenantNotices}<a class="group rounded-xl border border-border bg-card p-5 transition-colors hover:border-primary/50" href="/settings/notifications/tenant-notices">
						<div class="flex items-start justify-between gap-3">
							<div>
								<h2 class="font-semibold">Tenant notices</h2>
								<p class="mt-2 text-sm text-muted-foreground">Control each tenant automation independently and edit the supplied message templates.</p>
							</div>
							<ArrowRight class="mt-0.5 size-4 shrink-0 text-muted-foreground transition-transform group-hover:translate-x-0.5" />
						</div>
					</a>{/if}
				</div>

				{#if canBroadcast}
					<!-- Broadcast: a one-off announcement, not a stored setting. Lives under Notifications. -->
					<Card.Root class="gap-0 py-0" data-testid="settings-broadcast-notification">
						<Card.Content class="p-5">
							<p class="text-sm font-semibold">Send an announcement</p>
							<p class="mb-3 text-xs text-muted-foreground">
								Send a one-time in-app announcement to every user in this portfolio. It appears in their notification bell and tenant dashboard.
							</p>
							<div class="grid gap-3 md:grid-cols-[1fr_160px]">
								<div>
									<label for="broadcast-title" class="mb-1 block text-xs text-muted-foreground">Title</label>
									<Input id="broadcast-title" bind:value={broadcastForm.title} placeholder="Swimming pool closed today" data-testid="broadcast-title" />
								</div>
								<div>
									<label for="broadcast-severity" class="mb-1 block text-xs text-muted-foreground">Severity</label>
									<Select.Root type="single" bind:value={broadcastForm.severity}>
										<Select.Trigger id="broadcast-severity" class="w-full">{broadcastForm.severity}</Select.Trigger>
										<Select.Content>
											<Select.Item value="Info" label="Info">Info</Select.Item>
											<Select.Item value="Warning" label="Warning">Warning</Select.Item>
											<Select.Item value="Critical" label="Critical">Critical</Select.Item>
										</Select.Content>
									</Select.Root>
								</div>
								<div class="md:col-span-2">
									<label for="broadcast-message" class="mb-1 block text-xs text-muted-foreground">Message</label>
									<textarea
										id="broadcast-message"
										bind:value={broadcastForm.message}
										rows={3}
										class="w-full rounded border border-border bg-background px-3 py-2 text-sm"
										placeholder="The swimming pool is closed for maintenance and will reopen tomorrow morning."
										data-testid="broadcast-message"
									></textarea>
								</div>
								<div class="md:col-span-2">
									<Button
										onclick={() => broadcastMutation.mutate()}
										disabled={!broadcastForm.title.trim() || !broadcastForm.message.trim() || broadcastMutation.isPending}
										data-testid="broadcast-send"
									>
										{broadcastMutation.isPending ? 'Sending...' : 'Send Broadcast'}
									</Button>
								</div>
							</div>
						</Card.Content>
					</Card.Root>
				{/if}
			</div>
		</Tabs.Content>

		<!-- ─────────────────────────── AUTOMATIONS ─────────────────────────── -->
		<Tabs.Content value="automations">
			<div class="space-y-4">
				{@render sectionIntro(section('automations'))}

				<Card.Root class="gap-0 py-0" data-coach="settings-automations" data-testid="settings-late-fee-automations">
					<Card.Content class="p-5">
						<div class="flex flex-col gap-4 sm:flex-row sm:items-start sm:justify-between">
							<div>
								<p class="text-sm font-semibold">Rent and late fees</p>
								<p class="mt-1 text-xs text-muted-foreground">
									Rent charges post from active leases automatically. Choose whether overdue rent also receives the saved lease late fee after your workspace grace period.
								</p>
							</div>
							<span class="rounded-md border border-border px-2 py-1 text-xs text-muted-foreground">Rent charges always on</span>
						</div>

						{#if canManageAutomations}
							{#if lateFeeSettingsQuery.isLoading}
								<p class="mt-4 text-sm text-muted-foreground" data-testid="settings-late-fee-loading">Loading late fee settings...</p>
							{:else if lateFeeSettingsQuery.error}
								<p class="mt-4 text-sm text-destructive" data-testid="settings-late-fee-error">{apiErrorMessage(lateFeeSettingsQuery.error)}</p>
							{:else}
								<div class="mt-4 grid gap-4 sm:grid-cols-[minmax(0,1fr)_160px]">
									<label class="flex items-start gap-3">
										<Checkbox
											checked={lateFeeSettingsForm.enableLateFees}
											onCheckedChange={(v) => (lateFeeSettingsForm.enableLateFees = v === true)}
											data-testid="settings-enable-late-fees"
										/>
										<span class="text-sm leading-tight">
											<span class="font-medium">Auto-assess late fees</span>
											<span class="block text-xs text-muted-foreground">When rent remains open after the grace period, Rental Command adds one late-fee charge for that rent period.</span>
										</span>
									</label>
									<div>
										<label for="settings-late-fee-grace-days" class="text-xs font-medium text-muted-foreground">Grace days</label>
										<Input
											id="settings-late-fee-grace-days"
											type="number"
											min="0"
											max="31"
											bind:value={lateFeeSettingsForm.lateFeeGraceDays}
											data-testid="settings-late-fee-grace-days"
										/>
									</div>
								</div>
								<div class="mt-4">
									<Button
										onclick={() => lateFeeSettingsMutation.mutate()}
										disabled={lateFeeSettingsMutation.isPending}
										data-testid="settings-late-fee-save"
									>
										{lateFeeSettingsMutation.isPending ? 'Saving...' : 'Save late fees'}
									</Button>
								</div>
							{/if}
						{:else}
							<p class="mt-4 text-sm text-muted-foreground">Your team assignment does not include permission to manage financial automations.</p>
						{/if}
					</Card.Content>
				</Card.Root>

				{#if canManageTenantNotices}<Card.Root class="gap-0 py-0" data-coach="settings-automations">
					<Card.Content class="flex flex-col gap-4 p-5 sm:flex-row sm:items-center sm:justify-between">
						<div><p class="text-sm font-semibold">Tenant communication automations</p><p class="mt-1 text-xs text-muted-foreground">Each tenant notice now has its own Off, Draft for review, or Send automatically policy.</p></div>
						<Button variant="outline" href="/settings/notifications/tenant-notices">Open tenant notices</Button>
					</Card.Content>
				</Card.Root>{:else}<Card.Root class="gap-0 py-0"><Card.Content class="p-5"><p class="text-sm font-semibold">Tenant communication automations</p><p class="mt-1 text-xs text-muted-foreground">Your team assignment does not include permission to manage tenant notice automations.</p></Card.Content></Card.Root>{/if}
			</div>
		</Tabs.Content>

		<!-- ─────────────────────────── MESSAGING ─────────────────────────── -->
		<Tabs.Content value="messaging">
			<div class="space-y-4">
				{@render sectionIntro(section('messaging'))}

				<!-- Default channels for new tenant messages — stored in portfolio settings JSON. -->
				<Card.Root class="gap-0 py-0" data-testid="settings-messaging">
					<Card.Content class="p-5">
						<p class="text-sm font-semibold">Default channels for new messages</p>
						<p class="mb-3 text-xs text-muted-foreground">
							Choose which channels are pre-selected when you send a tenant a new message. The tenant
							portal inbox is always included; these just set the defaults for email and text. You can
							still turn any channel on or off for each individual message.
						</p>

						<div class="space-y-3">
							<label class="flex items-start gap-3">
								<Checkbox
									checked={settingsModel.messaging.email}
									onCheckedChange={(v) => (settingsModel.messaging.email = v === true)}
									data-testid="settings-messaging-email"
								/>
								<span class="text-sm leading-tight">
									<span class="font-medium">Email tenants by default</span>
									<span class="block text-xs text-muted-foreground">New messages are emailed to the tenant automatically.</span>
								</span>
							</label>

							<label class="flex items-start gap-3">
								<Checkbox
									checked={settingsModel.messaging.sms}
									onCheckedChange={(v) => (settingsModel.messaging.sms = v === true)}
									data-testid="settings-messaging-sms"
								/>
								<span class="text-sm leading-tight">
									<span class="font-medium">Text tenants by default (SMS)</span>
								<span class="block text-xs text-muted-foreground">
									New messages are texted to the tenant automatically.
									Texting requires a workspace SMS provider connection — until then, texts won't go out even if this is on.
								</span>
								</span>
							</label>
						</div>

						<div class="mt-4 flex items-center gap-3">
							<Button onclick={() => {
							const result = parseForm(settingsSchema, form);
							if (result.errors) { formErrors = result.errors; return; }
							formErrors = {};
							saveSucceeded = false;
							updateMutation.mutate();
						}} disabled={updateMutation.isPending} data-testid="settings-messaging-defaults-save">Save message defaults</Button>
							{#if saveSucceeded && !updateMutation.isPending}
								<span class="text-xs text-[var(--success)]">Saved.</span>
							{/if}
						</div>
					</Card.Content>
				</Card.Root>

			</div>
		</Tabs.Content>

		<!-- ─────────────────────────── TEAM ─────────────────────────── -->
		<Tabs.Content value="team">
			<div class="space-y-4">
				{@render sectionIntro(section('team'))}

				<!-- Account security: change-password lives on its own focused route. -->
				<Card.Root class="gap-0 py-0" data-testid="settings-security-link">
					<Card.Content class="flex flex-col gap-3 p-5 sm:flex-row sm:items-center sm:justify-between">
						<div>
							<p class="text-sm font-semibold">Your sign-in &amp; security</p>
							<p class="text-xs text-muted-foreground">Change your password and manage how you sign in.</p>
						</div>
						<Button href="/settings/security" data-testid="settings-security-open">
							Open security <ArrowRight class="ml-1.5 h-4 w-4" />
						</Button>
					</Card.Content>
				</Card.Root>

				<Card.Root class="gap-0 py-0" data-testid="settings-team-link">
					<Card.Content class="flex flex-col gap-3 p-5 sm:flex-row sm:items-center sm:justify-between">
						<div>
							<p class="text-sm font-semibold">Your team</p>
							<p class="text-xs text-muted-foreground">
								{#if isAdmin}
									Add people, set their role, and remove access when someone leaves.
								{:else}
									Only an account owner (Admin) can add or change team members. Ask yours if you need access changed.
								{/if}
							</p>
						</div>
						{#if isAdmin}
							<Button href="/admin/users" data-testid="settings-team-open">
								Manage team <ArrowRight class="ml-1.5 h-4 w-4" />
							</Button>
						{/if}
					</Card.Content>
				</Card.Root>
			</div>
		</Tabs.Content>

		<!-- ─────────────────────────── OWNERS ─────────────────────────── -->
		<Tabs.Content value="owners">
			<div class="space-y-4">
				{@render sectionIntro(section('owners'))}
				<Card.Root class="gap-0 py-0" data-testid="settings-owners-link">
					<Card.Content class="flex flex-col gap-3 p-5 sm:flex-row sm:items-center sm:justify-between">
						<div>
							<p class="text-sm font-semibold">Property owners</p>
							<p class="text-xs text-muted-foreground">Add owners, their tax details, and assign them to properties.</p>
						</div>
						<Button href="/owners" data-testid="settings-owners-open">
							Manage owners <ArrowRight class="ml-1.5 h-4 w-4" />
						</Button>
					</Card.Content>
				</Card.Root>
			</div>
		</Tabs.Content>

		<!-- ─────────────────────────── SETUP & IMPORT ─────────────────────────── -->
		<Tabs.Content value="setup">
			<div class="space-y-4">
					{@render sectionIntro(section('setup'))}
					<Card.Root class="gap-0 py-0" data-testid="settings-setup-link">
						<Card.Content class="grid gap-3 p-5 sm:grid-cols-2">
							<div class="rounded-lg border border-border bg-muted/30 p-4">
								<p class="text-sm font-semibold">Setup & Import</p>
								<p class="mb-3 text-xs text-muted-foreground">Walk step-by-step through your portfolio, owner, properties, tenants, and first lease.</p>
								<Button variant="outline" href="/onboarding?from=settings" data-testid="settings-setup-wizard">
									Open setup & import <ArrowRight class="ml-1.5 h-4 w-4" />
								</Button>
							</div>
						<div class="rounded-lg border border-border bg-muted/30 p-4">
							<p class="text-sm font-semibold">Import from a spreadsheet</p>
							<p class="mb-3 text-xs text-muted-foreground">Already have a list? Bring properties, tenants, and leases in from a spreadsheet — the app reads it and makes drafts to confirm.</p>
							<Button href="/import" data-testid="settings-setup-import">
								Import from a spreadsheet <ArrowRight class="ml-1.5 h-4 w-4" />
							</Button>
						</div>
					</Card.Content>
				</Card.Root>

				{#if canManageAiProvider}
					<Card.Root class="gap-0 py-0" data-testid="settings-ai-provider-link">
						<Card.Content class="flex flex-col gap-3 p-5 sm:flex-row sm:items-center sm:justify-between">
							<div>
								<p class="text-sm font-semibold">AI provider</p>
								<p class="text-xs text-muted-foreground">
									Connect and manage the workspace AI provider used by Scan / Add.
								</p>
							</div>
							<Button href="/settings/integrations/ai" data-testid="settings-ai-provider-open">
								Manage AI provider <ArrowRight class="ml-1.5 h-4 w-4" />
							</Button>
						</Card.Content>
					</Card.Root>
				{/if}

				<!-- Connect your accounting: brings rent payments + expenses in from QuickBooks (and
				     future providers) — its own focused route. -->
				<Card.Root class="gap-0 py-0" data-testid="settings-accounting-link">
					<Card.Content class="flex flex-col gap-3 p-5 sm:flex-row sm:items-center sm:justify-between">
						<div>
							<p class="text-sm font-semibold">Connect your accounting</p>
							<p class="text-xs text-muted-foreground">
								Link QuickBooks (more coming) to bring your existing rent payments and expenses
								into Rental Command — no double entry.
							</p>
						</div>
						<Button href="/settings/accounting" data-testid="settings-accounting-open">
							Connect accounting <ArrowRight class="ml-1.5 h-4 w-4" />
						</Button>
					</Card.Content>
				</Card.Root>
			</div>
		</Tabs.Content>

		<!-- ─────────────────────────── ACTIVITY HISTORY ─────────────────────────── -->
		<Tabs.Content value="activity">
			<div class="space-y-4">
				{@render sectionIntro(section('activity'))}
				<Card.Root class="gap-0 py-0" data-testid="settings-activity-link">
					<Card.Content class="flex flex-col gap-3 p-5 sm:flex-row sm:items-center sm:justify-between">
						<div>
							<p class="text-sm font-semibold">Activity history</p>
							<p class="text-xs text-muted-foreground">Search every change in your account — who did what, and when.</p>
						</div>
						<Button href="/audit" data-testid="settings-activity-open">
							Open activity history <ArrowRight class="ml-1.5 h-4 w-4" />
						</Button>
					</Card.Content>
				</Card.Root>
			</div>
		</Tabs.Content>
	</Tabs.Root>
</div>
