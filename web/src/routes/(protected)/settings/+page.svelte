<script lang="ts">
	import { createMutation, createQuery, useQueryClient } from '@tanstack/svelte-query';
	import { portfolios } from '$lib/api/endpoints/portfolios';
	import { notifications } from '$lib/api/endpoints/notifications';
	import type {
		NotificationChannelPreference,
		NotificationChannelType,
	} from '$lib/api/types/notification';
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
	let signalWireTokenInput = $state('');
	let broadcastForm = $state({
		title: '',
		message: '',
		severity: 'Info'
	});
	let notificationSettingsForm = $state({
		enableRentCharges: false,
		enableLateFees: false,
		enableLeaseExpiryReminders: true,
		notifyTenants: false,
		rentChargeLeadDays: 5,
		lateFeeGraceDays: 5,
		leaseExpiryReminderDays: 60,
		enableDailyBriefingMessages: false,
		dailyBriefingSendHourLocal: 8,
		dailyBriefingIncludeEmpty: false,
		dailyBriefingSmsRecipientsText: '',
		dailyBriefingEmailRecipientsText: '',
		signalWireProjectId: '',
		signalWireSpaceUrl: '',
		signalWireFromNumber: '',
		signalWireTokenSet: false,
	});

	// Per-notification × per-channel matrix. Ordered list of rows with plain-language
	// labels for the non-technical landlord. The API always returns one entry per type,
	// but we key by notificationType so order/labels are controlled here, not by the API.
	const channelRows: { type: NotificationChannelType; label: string; description: string }[] = [
		{ type: 'RentCharge', label: 'Rent reminders', description: 'Heads-up before rent is due.' },
		{ type: 'LateFee', label: 'Late fees', description: 'When a late fee is added to a lease.' },
		{ type: 'LeaseExpiry', label: 'Lease expiry / renewal', description: 'When a lease is coming up for renewal or move-out.' },
		{ type: 'RentConfirmation', label: 'Rent confirmations', description: 'When a rent payment is recorded.' },
		{ type: 'NoticeAutopilot', label: 'Lease notices (autopilot)', description: 'Automated notices sent to tenants.' },
		{ type: 'DailyBriefing', label: 'Daily briefing', description: 'Your once-a-day summary of what needs attention.' },
	];

	type ChannelPrefs = { enableInApp: boolean; enableEmail: boolean; enableSms: boolean };

	function emptyChannelPrefs(): Record<NotificationChannelType, ChannelPrefs> {
		return Object.fromEntries(
			channelRows.map((row) => [row.type, { enableInApp: false, enableEmail: false, enableSms: false }]),
		) as Record<NotificationChannelType, ChannelPrefs>;
	}

	let channelPreferences = $state<Record<NotificationChannelType, ChannelPrefs>>(emptyChannelPrefs());

	function applyChannelPreferences(prefs: NotificationChannelPreference[]) {
		const next = emptyChannelPrefs();
		for (const pref of prefs) {
			if (pref.notificationType in next) {
				next[pref.notificationType] = {
					enableInApp: pref.enableInApp,
					enableEmail: pref.enableEmail,
					enableSms: pref.enableSms,
				};
			}
		}
		channelPreferences = next;
	}

	function channelPreferencesPayload(): NotificationChannelPreference[] {
		return channelRows.map((row) => ({
			notificationType: row.type,
			enableInApp: channelPreferences[row.type].enableInApp,
			enableEmail: channelPreferences[row.type].enableEmail,
			enableSms: channelPreferences[row.type].enableSms,
		}));
	}

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

	const notificationSettingsQuery = createQuery(() => ({
		queryKey: ['notification-settings'],
		enabled: authState.isAuthenticated && portfolioId > 0,
		queryFn: () => notifications.getSettings(),
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

	$effect(() => {
		if (notificationSettingsQuery.data) {
			const data = notificationSettingsQuery.data;
			notificationSettingsForm = {
				enableRentCharges: data.enableRentCharges,
				enableLateFees: data.enableLateFees,
				enableLeaseExpiryReminders: data.enableLeaseExpiryReminders,
				notifyTenants: data.notifyTenants,
				rentChargeLeadDays: data.rentChargeLeadDays,
				lateFeeGraceDays: data.lateFeeGraceDays,
				leaseExpiryReminderDays: data.leaseExpiryReminderDays,
				enableDailyBriefingMessages: data.enableDailyBriefingMessages,
				dailyBriefingSendHourLocal: data.dailyBriefingSendHourLocal,
				dailyBriefingIncludeEmpty: data.dailyBriefingIncludeEmpty,
				dailyBriefingSmsRecipientsText: data.dailyBriefingSmsRecipients.join('\n'),
				dailyBriefingEmailRecipientsText: data.dailyBriefingEmailRecipients.join('\n'),
				signalWireProjectId: data.signalWireProjectId ?? '',
				signalWireSpaceUrl: data.signalWireSpaceUrl ?? '',
				signalWireFromNumber: data.signalWireFromNumber ?? '',
				signalWireTokenSet: data.signalWireTokenSet,
			};
			applyChannelPreferences(data.channelPreferences ?? []);
			signalWireTokenInput = '';
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

	function parseRecipients(text: string) {
		return text
			.split(/[\n,]+/)
			.map((value) => value.trim())
			.filter(Boolean);
	}

	const saveNotificationSettingsMutation = createMutation(() => ({
		mutationFn: () =>
			notifications.setSettings({
				enableRentCharges: notificationSettingsForm.enableRentCharges,
				enableLateFees: notificationSettingsForm.enableLateFees,
				enableLeaseExpiryReminders: notificationSettingsForm.enableLeaseExpiryReminders,
				notifyTenants: notificationSettingsForm.notifyTenants,
				rentChargeLeadDays: Number(notificationSettingsForm.rentChargeLeadDays) || 5,
				lateFeeGraceDays: Number(notificationSettingsForm.lateFeeGraceDays) || 5,
				leaseExpiryReminderDays: Number(notificationSettingsForm.leaseExpiryReminderDays) || 60,
				enableDailyBriefingMessages: notificationSettingsForm.enableDailyBriefingMessages,
				dailyBriefingSendHourLocal: Number(notificationSettingsForm.dailyBriefingSendHourLocal) || 8,
				dailyBriefingIncludeEmpty: notificationSettingsForm.dailyBriefingIncludeEmpty,
				dailyBriefingSmsRecipients: parseRecipients(notificationSettingsForm.dailyBriefingSmsRecipientsText),
				dailyBriefingEmailRecipients: parseRecipients(notificationSettingsForm.dailyBriefingEmailRecipientsText),
				signalWireProjectId: notificationSettingsForm.signalWireProjectId.trim() || null,
				signalWireToken: signalWireTokenInput.length > 0 ? signalWireTokenInput : undefined,
				signalWireSpaceUrl: notificationSettingsForm.signalWireSpaceUrl.trim() || null,
				signalWireFromNumber: notificationSettingsForm.signalWireFromNumber.trim() || null,
				channelPreferences: channelPreferencesPayload(),
			}),
		onSuccess: (result) => {
			notificationSettingsForm = {
				enableRentCharges: result.enableRentCharges,
				enableLateFees: result.enableLateFees,
				enableLeaseExpiryReminders: result.enableLeaseExpiryReminders,
				notifyTenants: result.notifyTenants,
				rentChargeLeadDays: result.rentChargeLeadDays,
				lateFeeGraceDays: result.lateFeeGraceDays,
				leaseExpiryReminderDays: result.leaseExpiryReminderDays,
				enableDailyBriefingMessages: result.enableDailyBriefingMessages,
				dailyBriefingSendHourLocal: result.dailyBriefingSendHourLocal,
				dailyBriefingIncludeEmpty: result.dailyBriefingIncludeEmpty,
				dailyBriefingSmsRecipientsText: result.dailyBriefingSmsRecipients.join('\n'),
				dailyBriefingEmailRecipientsText: result.dailyBriefingEmailRecipients.join('\n'),
				signalWireProjectId: result.signalWireProjectId ?? '',
				signalWireSpaceUrl: result.signalWireSpaceUrl ?? '',
				signalWireFromNumber: result.signalWireFromNumber ?? '',
				signalWireTokenSet: result.signalWireTokenSet,
			};
			applyChannelPreferences(result.channelPreferences ?? []);
			signalWireTokenInput = '';
			queryClient.invalidateQueries({ queryKey: ['notification-settings'] });
			showSuccess('Notification delivery settings saved.');
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
			showSuccess('Broadcast notification sent.');
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

			<!-- Per-notification × per-channel matrix -->
			<div class="mt-6 rounded-lg border border-border bg-muted/30 p-4" data-testid="settings-notification-channels">
				<p class="text-sm font-semibold">How you get notified</p>
				<p class="mb-3 text-xs text-muted-foreground">
					Pick how you want to hear about each kind of update. In-app shows in the bell at the top.
					Email goes to your notification email. Text (SMS) needs SignalWire set up below.
				</p>

				<div class="overflow-x-auto">
					<table class="w-full min-w-[28rem] border-collapse text-sm">
						<thead>
							<tr class="border-b border-border text-xs text-muted-foreground">
								<th class="py-2 pr-3 text-left font-medium">Notification</th>
								<th class="px-3 py-2 text-center font-medium">In-app</th>
								<th class="px-3 py-2 text-center font-medium">Email</th>
								<th class="px-3 py-2 text-center font-medium">Text (SMS)</th>
							</tr>
						</thead>
						<tbody>
							{#each channelRows as row (row.type)}
								<tr class="border-b border-border/60 last:border-0" data-testid={`settings-channel-row-${row.type}`}>
									<td class="py-2 pr-3 align-top">
										<span class="block font-medium leading-tight">{row.label}</span>
										<span class="block text-xs text-muted-foreground">{row.description}</span>
									</td>
									<td class="px-3 py-2 text-center align-top">
										<Checkbox
											checked={channelPreferences[row.type].enableInApp}
											onCheckedChange={(v) => (channelPreferences[row.type].enableInApp = v === true)}
											aria-label={`${row.label} in-app`}
											data-testid={`settings-channel-${row.type}-inapp`}
										/>
									</td>
									<td class="px-3 py-2 text-center align-top">
										<Checkbox
											checked={channelPreferences[row.type].enableEmail}
											onCheckedChange={(v) => (channelPreferences[row.type].enableEmail = v === true)}
											aria-label={`${row.label} email`}
											data-testid={`settings-channel-${row.type}-email`}
										/>
									</td>
									<td class="px-3 py-2 text-center align-top">
										<Checkbox
											checked={channelPreferences[row.type].enableSms}
											onCheckedChange={(v) => (channelPreferences[row.type].enableSms = v === true)}
											aria-label={`${row.label} text message`}
											data-testid={`settings-channel-${row.type}-sms`}
										/>
									</td>
								</tr>
							{/each}
						</tbody>
					</table>
				</div>

				<p class="mt-3 text-xs text-muted-foreground">
					These save together with the delivery settings below.
				</p>
			</div>

			<div class="mt-6 rounded-lg border border-border bg-muted/30 p-4" data-testid="settings-notification-delivery">
				<div class="grid gap-4 md:grid-cols-2">
					<div class="md:col-span-2">
						<p class="text-sm font-semibold">Automation and Delivery</p>
						<p class="text-xs text-muted-foreground">
							Automation flags, SignalWire credentials, and briefing recipients are stored in admin settings. Provider secrets are encrypted at rest.
						</p>
					</div>

					<label class="flex items-start gap-3 rounded border border-border bg-background p-3">
						<Checkbox
							checked={notificationSettingsForm.enableRentCharges}
							onCheckedChange={(v) => (notificationSettingsForm.enableRentCharges = v === true)}
							data-testid="settings-enable-rent-charges"
						/>
						<span class="text-sm leading-tight">
							<span class="font-medium">Auto-post rent charges</span>
							<span class="block text-xs text-muted-foreground">Create scheduled rent rows before each due date.</span>
						</span>
					</label>
					<label class="flex items-start gap-3 rounded border border-border bg-background p-3">
						<Checkbox
							checked={notificationSettingsForm.enableLateFees}
							onCheckedChange={(v) => (notificationSettingsForm.enableLateFees = v === true)}
							data-testid="settings-enable-late-fees"
						/>
						<span class="text-sm leading-tight">
							<span class="font-medium">Auto-assess late fees</span>
							<span class="block text-xs text-muted-foreground">Add lease late fees after the grace period.</span>
						</span>
					</label>
					<label class="flex items-start gap-3 rounded border border-border bg-background p-3">
						<Checkbox
							checked={notificationSettingsForm.enableLeaseExpiryReminders}
							onCheckedChange={(v) => (notificationSettingsForm.enableLeaseExpiryReminders = v === true)}
							data-testid="settings-enable-lease-reminders"
						/>
						<span class="text-sm leading-tight">
							<span class="font-medium">Lease expiry reminders</span>
							<span class="block text-xs text-muted-foreground">Queue renewal and move-out reminders before lease end.</span>
						</span>
					</label>
					<label class="flex items-start gap-3 rounded border border-border bg-background p-3">
						<Checkbox
							checked={notificationSettingsForm.notifyTenants}
							onCheckedChange={(v) => (notificationSettingsForm.notifyTenants = v === true)}
							data-testid="settings-notify-tenants"
						/>
						<span class="text-sm leading-tight">
							<span class="font-medium">Send tenant notices</span>
							<span class="block text-xs text-muted-foreground">Let automations enqueue tenant SMS/email when configured.</span>
						</span>
					</label>

					<div>
						<label for="settings-rent-lead-days" class="mb-1 block text-xs text-muted-foreground">Rent Lead Days</label>
						<Input
							id="settings-rent-lead-days"
							type="number"
							min="0"
							max="31"
							bind:value={notificationSettingsForm.rentChargeLeadDays}
							data-testid="settings-rent-lead-days"
						/>
					</div>
					<div>
						<label for="settings-late-grace-days" class="mb-1 block text-xs text-muted-foreground">Late Fee Grace Days</label>
						<Input
							id="settings-late-grace-days"
							type="number"
							min="0"
							max="60"
							bind:value={notificationSettingsForm.lateFeeGraceDays}
							data-testid="settings-late-grace-days"
						/>
					</div>
					<div>
						<label for="settings-lease-reminder-days" class="mb-1 block text-xs text-muted-foreground">Lease Reminder Days</label>
						<Input
							id="settings-lease-reminder-days"
							type="number"
							min="1"
							max="365"
							bind:value={notificationSettingsForm.leaseExpiryReminderDays}
							data-testid="settings-lease-reminder-days"
						/>
					</div>
					<div class="hidden md:block"></div>

					<div>
						<label for="settings-signalwire-project" class="mb-1 block text-xs text-muted-foreground">SignalWire Project ID</label>
						<Input
							id="settings-signalwire-project"
							bind:value={notificationSettingsForm.signalWireProjectId}
							autocomplete="off"
							data-testid="settings-signalwire-project"
						/>
					</div>
					<div>
						<label for="settings-signalwire-space" class="mb-1 block text-xs text-muted-foreground">SignalWire Space URL</label>
						<Input
							id="settings-signalwire-space"
							bind:value={notificationSettingsForm.signalWireSpaceUrl}
							placeholder="your-space.signalwire.com"
							autocomplete="off"
							data-testid="settings-signalwire-space"
						/>
					</div>
					<div>
						<label for="settings-signalwire-from" class="mb-1 block text-xs text-muted-foreground">SignalWire From Number</label>
						<Input
							id="settings-signalwire-from"
							bind:value={notificationSettingsForm.signalWireFromNumber}
							placeholder="+13302933081"
							autocomplete="off"
							data-testid="settings-signalwire-from"
						/>
					</div>
					<div>
						<label for="settings-signalwire-token" class="mb-1 block text-xs text-muted-foreground">
							SignalWire API Token {notificationSettingsForm.signalWireTokenSet ? '(saved)' : ''}
						</label>
						<Input
							id="settings-signalwire-token"
							type="password"
							bind:value={signalWireTokenInput}
							placeholder={notificationSettingsForm.signalWireTokenSet ? 'Leave blank to keep saved token' : 'Paste API token'}
							autocomplete="new-password"
							data-testid="settings-signalwire-token"
						/>
					</div>

					<label class="flex items-start gap-3 md:col-span-2">
						<Checkbox
							checked={notificationSettingsForm.enableDailyBriefingMessages}
							onCheckedChange={(v) => (notificationSettingsForm.enableDailyBriefingMessages = v === true)}
							data-testid="settings-daily-briefing-enabled"
						/>
						<span class="text-sm leading-tight">
							<span class="font-medium">Send daily briefing messages</span>
							<span class="block text-xs text-muted-foreground">The Engine sends one briefing per portfolio per day after the local send hour.</span>
						</span>
					</label>

					<div>
						<label for="settings-briefing-hour" class="mb-1 block text-xs text-muted-foreground">Send Hour Local</label>
						<Input
							id="settings-briefing-hour"
							type="number"
							min="0"
							max="23"
							bind:value={notificationSettingsForm.dailyBriefingSendHourLocal}
							data-testid="settings-briefing-hour"
						/>
					</div>
					<label class="flex items-start gap-3 self-end pb-2">
						<Checkbox
							checked={notificationSettingsForm.dailyBriefingIncludeEmpty}
							onCheckedChange={(v) => (notificationSettingsForm.dailyBriefingIncludeEmpty = v === true)}
							data-testid="settings-briefing-include-empty"
						/>
						<span class="text-sm leading-tight">
							<span class="font-medium">Send even when empty</span>
							<span class="block text-xs text-muted-foreground">Useful for testing; usually leave this off.</span>
						</span>
					</label>

					<div>
						<label for="settings-briefing-sms" class="mb-1 block text-xs text-muted-foreground">Daily Briefing SMS Recipients</label>
						<textarea
							id="settings-briefing-sms"
							bind:value={notificationSettingsForm.dailyBriefingSmsRecipientsText}
							rows={3}
							class="w-full rounded border border-border bg-background px-3 py-2 text-sm"
							placeholder="+13303966191"
							data-testid="settings-briefing-sms"
						></textarea>
					</div>
					<div>
						<label for="settings-briefing-email" class="mb-1 block text-xs text-muted-foreground">Daily Briefing Email Recipients</label>
						<textarea
							id="settings-briefing-email"
							bind:value={notificationSettingsForm.dailyBriefingEmailRecipientsText}
							rows={3}
							class="w-full rounded border border-border bg-background px-3 py-2 text-sm"
							placeholder="owner@example.com"
							data-testid="settings-briefing-email"
						></textarea>
					</div>

					<div class="md:col-span-2">
						<Button
							onclick={() => saveNotificationSettingsMutation.mutate()}
							disabled={notificationSettingsQuery.isLoading || saveNotificationSettingsMutation.isPending}
							data-testid="settings-notification-delivery-save"
						>
							{saveNotificationSettingsMutation.isPending ? 'Saving…' : 'Save Delivery Settings'}
						</Button>
					</div>
				</div>
			</div>

			<div class="mt-6 rounded-lg border border-border bg-muted/30 p-4" data-testid="settings-broadcast-notification">
				<p class="text-sm font-semibold">Broadcast Notification</p>
				<p class="mb-3 text-xs text-muted-foreground">
					Send an in-app announcement to every user in this portfolio. It appears in their notification bell and tenant dashboard.
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
