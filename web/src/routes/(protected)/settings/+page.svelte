<script lang="ts">
	import { createMutation, createQuery, useQueryClient } from '@tanstack/svelte-query';
	import { portfolios } from '$lib/api/endpoints/portfolios';
	import { notifications } from '$lib/api/endpoints/notifications';
	import { getAuthState } from '$lib/stores/auth.svelte';
	import { getCurrentPortfolioId } from '$lib/stores/portfolio.svelte';
	import { showSuccess, showError, apiErrorMessage } from '$lib/utils/toast';
	import { settingsSchema, parseForm } from '$lib/schemas';
	import { Button } from '$lib/components/ui/button';
	import { Input } from '$lib/components/ui/input';
	import { Checkbox } from '$lib/components/ui/checkbox';
	import * as Select from '$lib/components/ui/select';
	import * as Card from '$lib/components/ui/card';

	const queryClient = useQueryClient();
	const authState = getAuthState();
	const portfolioId = $derived(getCurrentPortfolioId());

	let saveSucceeded = $state(false);
	let formErrors = $state<Record<string, string>>({});
	let notificationEmail = $state('');

	const portfolioQuery = createQuery(() => ({
		queryKey: ['portfolio', portfolioId],
		enabled: authState.isAuthenticated && portfolioId > 0,
		queryFn: () => portfolios.get(portfolioId),
	}));

	const notificationEmailQuery = createQuery(() => ({
		queryKey: ['notification-email', portfolioId],
		enabled: authState.isAuthenticated && portfolioId > 0,
		queryFn: () => notifications.getNotificationEmail(),
	}));

	let form = $state({
		name: '',
		description: '',
		managementCompanyName: '',
		timeZone: 'America/New_York',
		status: 'Active',
		settings: '{\n  "rentCollectionDay": 1\n}',
	});

	// Messaging defaults live under the "messaging" key inside the settings JSON string.
	// Portal is always the base channel; only Email/SMS are configurable defaults here.
	const messagingDefaults = $derived.by(() => {
		try {
			const parsed = JSON.parse(form.settings || '{}');
			const m = parsed?.messaging ?? {};
			return { email: m.email === true, sms: m.sms === true };
		} catch {
			// Invalid JSON in the raw field — fall back to off so the toggles still render.
			return { email: false, sms: false };
		}
	});

	// Returns true if the raw settings JSON is currently unparseable (toggles are disabled then).
	const settingsJsonInvalid = $derived.by(() => {
		try {
			JSON.parse(form.settings || '{}');
			return false;
		} catch {
			return true;
		}
	});

	// Re-stringify the settings JSON with the given messaging default flipped, preserving
	// every other key (e.g. rentCollectionDay) and the existing 2-space formatting.
	function setMessagingDefault(key: 'email' | 'sms', value: boolean) {
		let parsed: Record<string, unknown>;
		try {
			parsed = JSON.parse(form.settings || '{}');
		} catch {
			return; // Don't clobber raw JSON the user is mid-edit on.
		}
		const messaging = {
			...(typeof parsed.messaging === 'object' && parsed.messaging !== null ? parsed.messaging : {}),
			[key]: value,
		};
		parsed.messaging = messaging;
		form.settings = JSON.stringify(parsed, null, 2);
	}

	$effect(() => {
		if (portfolioQuery.data) {
			form = {
				name: portfolioQuery.data.name,
				description: portfolioQuery.data.description || '',
				managementCompanyName: portfolioQuery.data.managementCompanyName || '',
				timeZone: portfolioQuery.data.timeZone || 'America/New_York',
				status: portfolioQuery.data.status,
				settings: portfolioQuery.data.settings || '{\n  "rentCollectionDay": 1\n}',
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
			settings: form.settings,
		}),
		onSuccess: () => {
			queryClient.invalidateQueries({ queryKey: ['portfolio', portfolioId] });
			queryClient.invalidateQueries({ queryKey: ['portfolios'] });
			showSuccess('Settings saved.');
			saveSucceeded = true;
		},
		onError: (err) => showError(apiErrorMessage(err)),
	}));

	$effect(() => {
		if (notificationEmailQuery.data) {
			notificationEmail = notificationEmailQuery.data.email ?? '';
		}
	});

	const saveNotificationEmailMutation = createMutation(() => ({
		mutationFn: () => notifications.setNotificationEmail(notificationEmail.trim() || null),
		onSuccess: (result) => {
			notificationEmail = result.email ?? '';
			queryClient.invalidateQueries({ queryKey: ['notification-email', portfolioId] });
			queryClient.invalidateQueries({ queryKey: ['portfolio', portfolioId] });
			showSuccess('Notification email saved.');
		},
		onError: (err) => showError(apiErrorMessage(err)),
	}));
</script>

<svelte:head>
	<title>Settings - Rental Command</title>
</svelte:head>

<div class="box-border h-full overflow-y-auto p-6 pb-20">
	<div class="mb-4">
		<h1 class="text-2xl font-bold">Portfolio Settings</h1>
		<p class="text-sm text-muted-foreground">Configure timezone, status, and operational defaults.</p>
	</div>

	<Card.Root class="max-w-3xl gap-0 py-0">
		<Card.Content class="p-5">
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
					<Input id="settings-timezone" bind:value={form.timeZone} />
				</div>
				<div>
					<label for="settings-status" class="mb-1 block text-xs text-muted-foreground">Status</label>
					<Select.Root type="single" bind:value={form.status}>
						<Select.Trigger class="w-full" id="settings-status">
							{form.status || 'Select status'}
						</Select.Trigger>
						<Select.Content>
							<Select.Item value="Onboarding" label="Onboarding">Onboarding</Select.Item>
							<Select.Item value="Active" label="Active">Active</Select.Item>
							<Select.Item value="Archived" label="Archived">Archived</Select.Item>
						</Select.Content>
					</Select.Root>
				</div>
			</div>

			<!-- Messaging defaults -->
			<div class="mt-6 rounded-lg border border-border bg-muted/30 p-4" data-testid="settings-notification-email">
				<p class="text-sm font-semibold">Notification Email</p>
				<p class="mb-3 text-xs text-muted-foreground">
					Notification emails will be sent to this address. Leave blank to use your login email.
				</p>
				<div class="flex flex-col gap-2 sm:flex-row">
					<Input
						type="email"
						bind:value={notificationEmail}
						placeholder="your-email@example.com"
						data-testid="settings-notification-email-input"
						disabled={notificationEmailQuery.isLoading || saveNotificationEmailMutation.isPending}
					/>
					<Button
						onclick={() => saveNotificationEmailMutation.mutate()}
						disabled={notificationEmailQuery.isLoading || saveNotificationEmailMutation.isPending}
						data-testid="settings-notification-email-save"
					>
						{saveNotificationEmailMutation.isPending ? 'Saving…' : 'Save'}
					</Button>
				</div>
			</div>

			<!-- Messaging defaults -->
			<div class="mt-6 rounded-lg border border-border bg-muted/30 p-4" data-testid="settings-messaging">
				<p class="text-sm font-semibold">Messaging</p>
				<p class="mb-3 text-xs text-muted-foreground">
					Choose which channels are pre-selected when you send a tenant a new message. The tenant
					portal inbox is always included; these just set the defaults for email and text. You can
					still turn any channel on or off for each individual message.
				</p>

				{#if settingsJsonInvalid}
					<p class="text-xs text-destructive" data-testid="settings-messaging-json-warning">
						Fix the Settings JSON below to change these toggles.
					</p>
				{/if}

				<div class="space-y-3">
					<label class="flex items-start gap-3" class:opacity-50={settingsJsonInvalid}>
						<Checkbox
							checked={messagingDefaults.email}
							disabled={settingsJsonInvalid}
							onCheckedChange={(v) => setMessagingDefault('email', v === true)}
							data-testid="settings-messaging-email"
						/>
						<span class="text-sm leading-tight">
							<span class="font-medium">Email tenants by default</span>
							<span class="block text-xs text-muted-foreground">New messages are emailed to the tenant automatically.</span>
						</span>
					</label>

					<label class="flex items-start gap-3" class:opacity-50={settingsJsonInvalid}>
						<Checkbox
							checked={messagingDefaults.sms}
							disabled={settingsJsonInvalid}
							onCheckedChange={(v) => setMessagingDefault('sms', v === true)}
							data-testid="settings-messaging-sms"
						/>
						<span class="text-sm leading-tight">
							<span class="font-medium">Text tenants by default (SMS)</span>
							<span class="block text-xs text-muted-foreground">
								New messages are texted to the tenant automatically.
								Texting requires Twilio to be set up — until then, texts won't go out even if this is on.
							</span>
						</span>
					</label>
				</div>
			</div>

			<!-- Advanced JSON settings -->
			<div class="mt-6 rounded-lg border border-border bg-muted/30 p-4">
				<p class="text-sm font-semibold">Advanced settings</p>
				<p class="mb-3 text-xs text-muted-foreground">Raw portfolio settings object. Changes here affect system-level defaults.</p>
				<label for="settings-json" class="mb-1 block text-xs text-muted-foreground">Settings JSON</label>
				<textarea id="settings-json" bind:value={form.settings} rows={8} class="w-full rounded border border-border bg-background px-3 py-2 text-sm font-mono"></textarea>
			</div>

			<div class="mt-4 flex items-center gap-3">
				<Button onclick={() => {
				const result = parseForm(settingsSchema, form);
				if (result.errors) { formErrors = result.errors; return; }
				formErrors = {};
				saveSucceeded = false;
				updateMutation.mutate();
			}} disabled={updateMutation.isPending}>Save Settings</Button>
				{#if saveSucceeded && !updateMutation.isPending}
					<span class="text-xs text-green-600">Settings saved successfully.</span>
				{/if}
			</div>
		</Card.Content>
	</Card.Root>
</div>
