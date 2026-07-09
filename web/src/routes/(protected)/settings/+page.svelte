<script lang="ts">
	import { createMutation, createQuery, useQueryClient } from '@tanstack/svelte-query';
	import { portfolios } from '$lib/api/endpoints/portfolios';
	import { notifications } from '$lib/api/endpoints/notifications';
	import type {
		NotificationChannelPreference,
		NotificationChannelType,
		NotificationSeverity,
		SmsProviderMeta,
		LeaseEndAutoAction,
	} from '$lib/api/types/notification';
	import { getAuthState, currentUserIsAdmin, hasAnyRole } from '$lib/stores/auth.svelte';
	import { notificationStore } from '$lib/stores/notifications.svelte';
	import { getCurrentPortfolioId } from '$lib/stores/portfolio.svelte';
	import { showSuccess, showError, apiErrorMessage } from '$lib/utils/toast';
	import { settingsSchema, parseForm } from '$lib/schemas';
	import { Button } from '$lib/components/ui/button';
	import { Input } from '$lib/components/ui/input';
	import { Checkbox } from '$lib/components/ui/checkbox';
	import * as Select from '$lib/components/ui/select';
	import * as Card from '$lib/components/ui/card';
	import * as Tabs from '$lib/components/ui/tabs';
	import TimeZoneSelect from '$lib/components/shared/TimeZoneSelect.svelte';
	import NoticeTemplatesSection from '$lib/components/settings/NoticeTemplatesSection.svelte';
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
	const isAdmin = $derived(currentUserIsAdmin());
	const canBroadcast = $derived(hasAnyRole('Admin', 'Manager', 'Owner'));

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
	let notificationEmail = $state('');
	// Write-only credential inputs (blank = keep saved secret). Cleared on (re)load.
	let smsCredentialAInput = $state('');
	let smsCredentialBInput = $state('');
	let smsCredentialCInput = $state('');
	// Test-SMS verification state.
	let testSmsNumber = $state('');
	let testSmsResult = $state<{ success: boolean; message: string } | null>(null);
	let broadcastForm = $state<{
		title: string;
		message: string;
		severity: NotificationSeverity;
	}>({
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
		smsProvider: 'None',
		smsFromNumber: '',
		smsCredentialASet: false,
		smsCredentialBSet: false,
		smsCredentialCSet: false,
		autoSendRentReminder: false,
		autoSendLateRent: false,
		leaseEndAutoAction: 'Draft' as LeaseEndAutoAction,
	});

	// Provider catalogue: drives the <Select> and the labels of the three generic credential slots.
	// Adding a provider here + a backend ISmsProvider is the whole extension surface — no other UI edits.
	const smsProviders: SmsProviderMeta[] = [
		{ key: 'None', label: 'None (use platform default)', credentialA: null, credentialB: null, credentialC: null },
		{
			key: 'SignalWire',
			label: 'SignalWire',
			credentialA: { label: 'Project ID' },
			credentialB: { label: 'API Token' },
			credentialC: { label: 'Space URL', placeholder: 'your-space.signalwire.com' },
		},
		{
			key: 'Twilio',
			label: 'Twilio',
			credentialA: { label: 'Account SID' },
			credentialB: { label: 'Auth Token' },
			credentialC: null,
		},
		{
			key: 'Telnyx',
			label: 'Telnyx',
			credentialA: { label: 'API Key' },
			credentialB: null,
			credentialC: null,
		},
		{
			key: 'Vonage',
			label: 'Vonage',
			credentialA: { label: 'API Key' },
			credentialB: { label: 'API Secret' },
			credentialC: null,
		},
	];
	const selectedProvider = $derived(
		smsProviders.find((p) => p.key === notificationSettingsForm.smsProvider) ?? smsProviders[0]
	);

	// Per-notification × per-channel matrix. Ordered list of rows with plain-language
	// labels for the non-technical landlord. The API always returns one entry per type,
	// but we key by notificationType so order/labels are controlled here, not by the API.
	const channelRows: { type: NotificationChannelType; label: string; description: string }[] = [
		{ type: 'RentCharge', label: 'Rent reminders', description: 'Heads-up before rent is due.' },
		{ type: 'LateFee', label: 'Late fees', description: 'When a late fee is added to a lease.' },
		{ type: 'LeaseExpiry', label: 'Lease expiry / renewal', description: 'When a lease is coming up for renewal or move-out.' },
		{ type: 'RentConfirmation', label: 'Rent confirmations', description: 'When a rent payment is recorded.' },
		{ type: 'NoticeAutopilot', label: 'Lease notices (autopilot)', description: 'Drafted renewal, late-rent, and move-out notices that staff approves before sending.' },
		{ type: 'DailyBriefing', label: 'Daily briefing', description: 'Your once-a-day summary of what needs attention.' },
	];

	type ChannelPrefs = {
		enableInApp: boolean;
		enableEmail: boolean;
		enableSms: boolean;
		enablePush: boolean;
	};
	type ChannelKey = keyof ChannelPrefs;

	const channelColumns: { key: ChannelKey; label: string; description: string; testSuffix: string }[] = [
		{ key: 'enableInApp', label: 'In-app', description: 'The Rental Command bell and inbox.', testSuffix: 'inapp' },
		{ key: 'enablePush', label: 'Mobile push', description: 'Push alerts to registered mobile app devices.', testSuffix: 'push' },
		{ key: 'enableEmail', label: 'Email', description: 'Email delivery using the saved notification address or tenant contact.', testSuffix: 'email' },
		{ key: 'enableSms', label: 'Text (SMS)', description: 'Text messages through the configured SMS provider.', testSuffix: 'sms' },
	];

	const tenantFacingNotificationTypes = new Set<NotificationChannelType>(['RentCharge', 'LateFee']);

	function isTenantFacingNotification(type: NotificationChannelType) {
		return tenantFacingNotificationTypes.has(type);
	}

	function tenantChannelStatus(type: NotificationChannelType, key: ChannelKey) {
		if (!notificationSettingsForm.notifyTenants) return 'Off';
		return channelPreferences[type][key] ? 'On' : 'Off';
	}

	function tenantChannelStatusClass(type: NotificationChannelType, key: ChannelKey) {
		return notificationSettingsForm.notifyTenants && channelPreferences[type][key]
			? 'border-emerald-400/40 bg-emerald-400/10 text-emerald-700 dark:text-emerald-300'
			: 'border-border bg-muted text-muted-foreground';
	}

	function emptyChannelPrefs(): Record<NotificationChannelType, ChannelPrefs> {
		return Object.fromEntries(
			channelRows.map((row) => [
				row.type,
				{ enableInApp: false, enableEmail: false, enableSms: false, enablePush: true },
			]),
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
					enablePush: pref.enablePush ?? true,
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
			enablePush: channelPreferences[row.type].enablePush,
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
	});

	// Structured view of the portfolio settings JSON. The raw JSON blob is never shown to the
	// landlord — known keys are real inputs and anything else is an "advanced" key/value row.
	//   - rentCollectionDay → number field
	//   - prorationConvention → rent proration rule for partial first/final periods
	//   - messaging.{email,sms} → toggles (also read by the messages composer)
	//   - everything else (one level deep) → labeled "advanced" rows OR preserved untouched
	// Keys owned by OTHER save flows on this page (notifications.email is written by the separate
	// "Notification Email" endpoint, which read-modify-writes this same JSON) are kept verbatim in
	// `preserved` so saving the portfolio form never clobbers them.
	type CustomRow = { id: number; key: string; value: string };
	type ProrationConvention = 'ActualDays' | 'ThirtyDay';
	const MANAGED_KEYS = ['rentCollectionDay', 'prorationConvention', 'messaging'];
	// Top-level keys that other UI sections / endpoints own. Round-tripped untouched, hidden here.
	const PRESERVED_KEYS = ['notifications'];

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

	let showAdvanced = $state(false);
	let nextRowId = 0;

	// Parse the stored settings JSON into the structured model. Tolerant of malformed JSON
	// (falls back to defaults) so a landlord is never locked out by a bad legacy value.
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
			if (PRESERVED_KEYS.includes(key)) {
				preserved[key] = value;
				continue;
			}
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
			if (!key || MANAGED_KEYS.includes(key) || PRESERVED_KEYS.includes(key)) continue;
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
				smsProvider: data.smsProvider || 'None',
				smsFromNumber: data.smsFromNumber ?? '',
				smsCredentialASet: data.smsCredentialASet,
				smsCredentialBSet: data.smsCredentialBSet,
				smsCredentialCSet: data.smsCredentialCSet,
				autoSendRentReminder: data.autoSendRentReminder,
				autoSendLateRent: data.autoSendLateRent,
				leaseEndAutoAction: data.leaseEndAutoAction,
			};
			applyChannelPreferences(data.channelPreferences ?? []);
			smsCredentialAInput = '';
			smsCredentialBInput = '';
			smsCredentialCInput = '';
			testSmsResult = null;
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
				smsProvider: notificationSettingsForm.smsProvider,
				smsFromNumber: notificationSettingsForm.smsFromNumber.trim() || null,
				// Blank input = keep saved secret (send undefined); a value = set it.
				smsCredentialA: smsCredentialAInput.length > 0 ? smsCredentialAInput : undefined,
				smsCredentialB: smsCredentialBInput.length > 0 ? smsCredentialBInput : undefined,
				smsCredentialC: smsCredentialCInput.length > 0 ? smsCredentialCInput : undefined,
				autoSendRentReminder: notificationSettingsForm.autoSendRentReminder,
				autoSendLateRent: notificationSettingsForm.autoSendLateRent,
				leaseEndAutoAction: notificationSettingsForm.leaseEndAutoAction,
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
				smsProvider: result.smsProvider || 'None',
				smsFromNumber: result.smsFromNumber ?? '',
				smsCredentialASet: result.smsCredentialASet,
				smsCredentialBSet: result.smsCredentialBSet,
				smsCredentialCSet: result.smsCredentialCSet,
				autoSendRentReminder: result.autoSendRentReminder,
				autoSendLateRent: result.autoSendLateRent,
				leaseEndAutoAction: result.leaseEndAutoAction,
			};
			applyChannelPreferences(result.channelPreferences ?? []);
			smsCredentialAInput = '';
			smsCredentialBInput = '';
			smsCredentialCInput = '';
			queryClient.invalidateQueries({ queryKey: ['notification-settings'] });
			showSuccess('Notification delivery settings saved.');
		},
		onError: (err) => showError(apiErrorMessage(err)),
	}));

	// Send a one-off test SMS using the currently-entered provider + credentials (blank slots fall
	// back to the saved secrets server-side). Fail-soft: the API always returns a success flag.
	const testSmsMutation = createMutation(() => ({
		mutationFn: () =>
			notifications.testSms({
				smsProvider: notificationSettingsForm.smsProvider,
				toPhoneNumber: testSmsNumber.trim(),
				smsFromNumber: notificationSettingsForm.smsFromNumber.trim() || undefined,
				smsCredentialA: smsCredentialAInput.length > 0 ? smsCredentialAInput : undefined,
				smsCredentialB: smsCredentialBInput.length > 0 ? smsCredentialBInput : undefined,
				smsCredentialC: smsCredentialCInput.length > 0 ? smsCredentialCInput : undefined,
			}),
		onSuccess: (result) => {
			testSmsResult = result;
			if (result.success) showSuccess(result.message);
			else showError(result.message);
		},
		onError: (err) => {
			testSmsResult = { success: false, message: apiErrorMessage(err) };
			showError(apiErrorMessage(err));
		},
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

				<!-- Notification email — its own save endpoint (read-modify-writes the portfolio JSON). -->
				<Card.Root class="gap-0 py-0" id="settings-notification-email" data-coach="settings-notifications" data-testid="settings-notification-email">
					<Card.Content class="p-5">
						<p class="text-sm font-semibold">Where alerts go</p>
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
					</Card.Content>
				</Card.Root>

				<!-- Per-notification × per-channel matrix + daily-briefing recipients. Saved together via
				     the shared delivery-settings save (same model the Automations tab writes). -->
				<Card.Root class="gap-0 py-0" data-testid="settings-notification-channels">
					<Card.Content class="p-5">
						<p class="text-sm font-semibold">How you get notified</p>
						<p class="mb-3 text-xs text-muted-foreground">
							Pick how staff and owner users hear about each kind of update. Tenant rows show the
							automations that can also notify renters when tenant delivery is on.
						</p>

						<div
							class="mb-4 rounded-lg border border-border bg-muted/30 p-4"
							data-testid="settings-notification-audience-summary"
						>
							<div class="grid gap-4 md:grid-cols-[1fr_18rem]">
								<div class="space-y-2 text-xs text-muted-foreground">
									<p>
										<span class="font-medium text-foreground">Channels:</span>
										In-app is the Rental Command bell and inbox. Mobile push goes to registered mobile app devices.
										Email uses the saved notification address or tenant contact. SMS uses the provider on the Messaging tab.
									</p>
									<p>
										Browser web push is not available yet. Turning on mobile push will not send browser notifications.
									</p>
								</div>
								<label class="flex items-start gap-3 rounded border border-border bg-background p-3">
									<Checkbox
										checked={notificationSettingsForm.notifyTenants}
										onCheckedChange={(v) => (notificationSettingsForm.notifyTenants = v === true)}
										data-testid="settings-tenant-delivery-toggle"
									/>
									<span class="text-sm leading-tight">
										<span class="font-medium">Send tenant notices</span>
										<span class="block text-xs text-muted-foreground">
											Rent reminders and late fees also go to renters using the enabled channels below.
										</span>
									</span>
								</label>
							</div>
						</div>

						<div class="overflow-x-auto" data-testid="settings-notification-audience-matrix">
							<table class="w-full min-w-[48rem] border-collapse text-sm">
								<thead>
									<tr class="border-b border-border text-xs text-muted-foreground">
										<th class="py-2 pr-3 text-left font-medium">Audience / event</th>
										{#each channelColumns as column (column.key)}
											<th class="px-3 py-2 text-center font-medium">
												<span class="block">{column.label}</span>
												<span class="block text-[0.68rem] font-normal text-muted-foreground">{column.description}</span>
											</th>
										{/each}
									</tr>
								</thead>
								<tbody>
									{#each channelRows as row (row.type)}
										<tr class="border-b border-border/60" data-testid={`settings-audience-row-${row.type}-staff`}>
											<td class="py-2 pr-3 align-top">
												<span class="mb-1 inline-flex rounded-full border border-border bg-background px-2 py-0.5 text-[0.68rem] font-medium text-muted-foreground">
													Staff / owner
												</span>
												<span class="block font-medium leading-tight">{row.label}</span>
												<span class="block text-xs text-muted-foreground">{row.description}</span>
											</td>
											{#each channelColumns as column (column.key)}
												<td class="px-3 py-2 text-center align-top">
													<Checkbox
														checked={channelPreferences[row.type][column.key]}
														onCheckedChange={(v) => (channelPreferences[row.type][column.key] = v === true)}
														aria-label={`${row.label} ${column.label}`}
														data-testid={`settings-channel-${row.type}-${column.testSuffix}`}
													/>
												</td>
											{/each}
										</tr>
										{#if isTenantFacingNotification(row.type)}
											<tr class="border-b border-border/60 bg-muted/20 last:border-0" data-testid={`settings-audience-row-${row.type}-tenant`}>
												<td class="py-2 pl-4 pr-3 align-top">
													<span class="mb-1 inline-flex rounded-full border border-border bg-background px-2 py-0.5 text-[0.68rem] font-medium text-muted-foreground">
														Tenant
													</span>
													<span class="block font-medium leading-tight">{row.label}</span>
													<span class="block text-xs text-muted-foreground">
														{notificationSettingsForm.notifyTenants
															? 'Tenant delivery follows the enabled channels in this event row.'
															: 'Tenant delivery is off until Send tenant notices is enabled.'}
													</span>
												</td>
												{#each channelColumns as column (column.key)}
													<td class="px-3 py-2 text-center align-top">
														<span
															class={`inline-flex min-w-10 justify-center rounded-full border px-2 py-0.5 text-xs font-medium ${tenantChannelStatusClass(row.type, column.key)}`}
															data-testid={`settings-tenant-channel-${row.type}-${column.testSuffix}`}
														>
															{tenantChannelStatus(row.type, column.key)}
														</span>
													</td>
												{/each}
											</tr>
										{/if}
									{/each}
								</tbody>
							</table>
						</div>

						<!-- Daily briefing schedule + recipients live with the notification matrix. -->
						<div class="mt-6 grid gap-4 md:grid-cols-2">
							<label class="flex items-start gap-3 md:col-span-2">
								<Checkbox
									checked={notificationSettingsForm.enableDailyBriefingMessages}
									onCheckedChange={(v) => (notificationSettingsForm.enableDailyBriefingMessages = v === true)}
									data-testid="settings-daily-briefing-enabled"
								/>
								<span class="text-sm leading-tight">
									<span class="font-medium">Send daily briefing messages</span>
									<span class="block text-xs text-muted-foreground">One short summary per day after the local send hour — what needs your attention.</span>
								</span>
							</label>

							<div>
								<label for="settings-briefing-hour" class="mb-1 block text-xs text-muted-foreground">Send Hour Local</label>
								<Input
									id="settings-briefing-hour"
									type="text"
									inputmode="numeric"
									maxlength={2}
									mask="integer"
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
						</div>

						<div class="mt-4 flex items-center gap-3">
							<Button
								onclick={() => saveNotificationSettingsMutation.mutate()}
								disabled={notificationSettingsQuery.isLoading || saveNotificationSettingsMutation.isPending}
								data-testid="settings-notification-channels-save"
							>
								{saveNotificationSettingsMutation.isPending ? 'Saving…' : 'Save notification settings'}
							</Button>
						</div>
						<p class="mt-2 text-xs text-muted-foreground">
							Your delivery choices and automation settings save together.
						</p>
					</Card.Content>
				</Card.Root>

				<!-- Notice message templates + recurring auto-send toggles + the single lease-end auto action.
				     These bind to notificationSettingsForm and persist via the same delivery-settings save. -->
				<NoticeTemplatesSection
					settings={notificationSettingsForm}
					onSaveSettings={() => saveNotificationSettingsMutation.mutate()}
					savingSettings={saveNotificationSettingsMutation.isPending}
					settingsDisabled={notificationSettingsQuery.isLoading}
				/>

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

				<Card.Root class="gap-0 py-0" id="settings-notification-delivery" data-coach="settings-automations" data-testid="settings-notification-delivery">
					<Card.Content class="p-5">
						<p class="mb-3 text-sm font-semibold">Routine work the app handles for you</p>
						<div class="grid gap-4 md:grid-cols-2">
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
									type="text"
									inputmode="numeric"
									maxlength={2}
									mask="integer"
									bind:value={notificationSettingsForm.rentChargeLeadDays}
									data-testid="settings-rent-lead-days"
								/>
								<p class="mt-1 text-xs text-muted-foreground">How many days before the due date to post the charge.</p>
							</div>
							<div>
								<label for="settings-late-grace-days" class="mb-1 block text-xs text-muted-foreground">Late Fee Grace Days</label>
								<Input
									id="settings-late-grace-days"
									type="text"
									inputmode="numeric"
									maxlength={2}
									mask="integer"
									bind:value={notificationSettingsForm.lateFeeGraceDays}
									data-testid="settings-late-grace-days"
								/>
								<p class="mt-1 text-xs text-muted-foreground">Days after the due date before a late fee is added.</p>
							</div>
							<div>
								<label for="settings-lease-reminder-days" class="mb-1 block text-xs text-muted-foreground">Lease Reminder Days</label>
								<Input
									id="settings-lease-reminder-days"
									type="text"
									inputmode="numeric"
									maxlength={3}
									mask="integer"
									bind:value={notificationSettingsForm.leaseExpiryReminderDays}
									data-testid="settings-lease-reminder-days"
								/>
								<p class="mt-1 text-xs text-muted-foreground">How far ahead of lease end to start reminders.</p>
							</div>
							<div class="hidden md:block"></div>

							<div class="md:col-span-2">
								<Button
									onclick={() => saveNotificationSettingsMutation.mutate()}
									disabled={notificationSettingsQuery.isLoading || saveNotificationSettingsMutation.isPending}
									data-testid="settings-notification-delivery-save"
								>
									{saveNotificationSettingsMutation.isPending ? 'Saving…' : 'Save automations'}
								</Button>
								<p class="mt-2 text-xs text-muted-foreground">
									Automations save together with your notification choices.
								</p>
							</div>
						</div>
					</Card.Content>
				</Card.Root>
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
										Texting requires an SMS provider below — until then, texts won't go out even if this is on.
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

				<!-- Connected service: Text messaging (SMS) provider. Bring-your-own provider + creds.
				     Saved by the delivery-settings endpoint (the same model the matrix uses). -->
				<Card.Root class="gap-0 py-0" data-coach="settings-messaging" data-testid="settings-sms-provider-section">
					<Card.Content class="p-5">
						<div class="mb-1 flex items-center justify-between gap-2">
							<p class="text-sm font-semibold">Text messaging (SMS)</p>
							<WalkMeThrough step="texting" label="Set up texting" />
						</div>
						<p class="mb-3 text-xs text-muted-foreground">
							Pick an SMS provider and enter your own account credentials. Texts are sent from your
							account. Leave on "None" to use the platform default. Secrets are encrypted at rest.
						</p>

						<div class="grid gap-4 md:grid-cols-2">
							<div>
								<label for="settings-sms-provider" class="mb-1 block text-xs text-muted-foreground">Provider</label>
								<Select.Root type="single" bind:value={notificationSettingsForm.smsProvider}>
									<Select.Trigger class="w-full" id="settings-sms-provider" data-testid="settings-sms-provider">
										{selectedProvider.label}
									</Select.Trigger>
									<Select.Content>
										{#each smsProviders as p (p.key)}
											<Select.Item value={p.key} label={p.label}>{p.label}</Select.Item>
										{/each}
									</Select.Content>
								</Select.Root>
							</div>

							{#if notificationSettingsForm.smsProvider !== 'None'}
								<div>
									<label for="settings-sms-from" class="mb-1 block text-xs text-muted-foreground">From Number</label>
									<Input
										id="settings-sms-from"
										bind:value={notificationSettingsForm.smsFromNumber}
										placeholder="+13302933081"
										autocomplete="off"
										data-testid="settings-sms-from"
									/>
								</div>

								{#if selectedProvider.credentialA}
									<div>
										<label for="settings-sms-cred-a" class="mb-1 block text-xs text-muted-foreground">
											{selectedProvider.credentialA.label} {notificationSettingsForm.smsCredentialASet ? '(saved)' : ''}
										</label>
										<Input
											id="settings-sms-cred-a"
											type="password"
											bind:value={smsCredentialAInput}
											placeholder={notificationSettingsForm.smsCredentialASet ? 'Leave blank to keep saved value' : (selectedProvider.credentialA.placeholder ?? '')}
											autocomplete="new-password"
											data-testid="settings-sms-cred-a"
										/>
									</div>
								{/if}

								{#if selectedProvider.credentialB}
									<div>
										<label for="settings-sms-cred-b" class="mb-1 block text-xs text-muted-foreground">
											{selectedProvider.credentialB.label} {notificationSettingsForm.smsCredentialBSet ? '(saved)' : ''}
										</label>
										<Input
											id="settings-sms-cred-b"
											type="password"
											bind:value={smsCredentialBInput}
											placeholder={notificationSettingsForm.smsCredentialBSet ? 'Leave blank to keep saved value' : (selectedProvider.credentialB.placeholder ?? '')}
											autocomplete="new-password"
											data-testid="settings-sms-cred-b"
										/>
									</div>
								{/if}

								{#if selectedProvider.credentialC}
									<div>
										<label for="settings-sms-cred-c" class="mb-1 block text-xs text-muted-foreground">
											{selectedProvider.credentialC.label} {notificationSettingsForm.smsCredentialCSet ? '(saved)' : ''}
										</label>
										<Input
											id="settings-sms-cred-c"
											bind:value={smsCredentialCInput}
											placeholder={notificationSettingsForm.smsCredentialCSet ? 'Leave blank to keep saved value' : (selectedProvider.credentialC.placeholder ?? '')}
											autocomplete="off"
											data-testid="settings-sms-cred-c"
										/>
									</div>
								{/if}
							{/if}
						</div>

						{#if notificationSettingsForm.smsProvider !== 'None'}
							<div class="mt-4 border-t border-border pt-3">
								<p class="mb-2 text-xs text-muted-foreground">
									Send a test text to confirm it works. Blank credentials above use your saved values.
								</p>
								<div class="flex flex-wrap items-end gap-2">
									<div class="flex-1 min-w-[12rem]">
										<label for="settings-sms-test-number" class="mb-1 block text-xs text-muted-foreground">Your phone number</label>
										<Input
											id="settings-sms-test-number"
											bind:value={testSmsNumber}
											placeholder="+13303966191"
											autocomplete="off"
											data-testid="settings-sms-test-number"
										/>
									</div>
									<Button
										type="button"
										variant="outline"
										disabled={testSmsMutation.isPending || testSmsNumber.trim().length === 0}
										onclick={() => testSmsMutation.mutate()}
										data-testid="settings-sms-test-button"
									>
										{testSmsMutation.isPending ? 'Sending…' : 'Send test SMS'}
									</Button>
								</div>
								{#if testSmsResult}
									<p
										class="mt-2 text-xs {testSmsResult.success ? 'text-green-600' : 'text-destructive'}"
										data-testid="settings-sms-test-result"
									>
										{testSmsResult.message}
									</p>
								{/if}
							</div>
						{/if}

						<div class="mt-4 flex items-center gap-3">
							<Button
								onclick={() => saveNotificationSettingsMutation.mutate()}
								disabled={notificationSettingsQuery.isLoading || saveNotificationSettingsMutation.isPending}
								data-testid="settings-sms-provider-save"
							>
								{saveNotificationSettingsMutation.isPending ? 'Saving…' : 'Save SMS provider'}
							</Button>
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
