<script lang="ts">
	import { createMutation, createQuery, useQueryClient } from '@tanstack/svelte-query';
	import { notifications } from '$lib/api/endpoints/notifications';
	import type { NoticeDeliveryStatus, TenantNoticeMode, TenantNoticePolicyResponse, UpsertTenantNoticePolicyRequest } from '$lib/api/types/notification';
	import { hasCapability } from '$lib/stores/auth.svelte';
	import { getAuthState } from '$lib/stores/auth.svelte';
	import { canAccessPathForEnvelope, safeLandingForAccess } from '$lib/auth/experience-policy';
	import { showError, showSuccess, apiErrorMessage } from '$lib/utils/toast';
	import { Button } from '$lib/components/ui/button';
	import { Checkbox } from '$lib/components/ui/checkbox';
	import { Input } from '$lib/components/ui/input';
	import * as Select from '$lib/components/ui/select';
	import * as Card from '$lib/components/ui/card';
	import NotificationPolicyAccordion from '$lib/components/notifications/NotificationPolicyAccordion.svelte';
	import NotificationSetupJourney from '$lib/components/notifications/NotificationSetupJourney.svelte';
	import NoticePreview from '$lib/components/notifications/NoticePreview.svelte';
	import LoadingState from '$lib/components/shared/LoadingState.svelte';

	const queryClient = useQueryClient();
	const authState = getAuthState();
	const returnHref = $derived.by(() => {
		const access = authState.accessEnvelope;
		if (!access) return '/';
		return canAccessPathForEnvelope(access, '/settings')
			? '/settings#notifications'
			: (safeLandingForAccess(access) ?? '/');
	});
	const canManage = $derived(hasCapability('notifications.manage'));
	const automationDetails: Record<string, { label: string; detail: string }> = {
		'rent-reminder': { label: 'Rent is due soon', detail: 'Remind tenants before rent is due.' },
		'lease-renewal-offer': { label: 'Offer a lease renewal', detail: 'Prepare an offer after you choose to renew the lease.' },
		'month-to-month-offer': { label: 'Offer month-to-month', detail: 'Prepare an offer after you choose month-to-month.' },
		'lease-non-renewal': { label: 'Lease will end', detail: 'Prepare the required notice after you choose move-out.' },
		'late-rent-late-fee': { label: 'Rent is late', detail: 'Prepare a notice when rent remains unpaid and late-fee details are known.' }
	};
	const suppliedAutomationCount = Object.keys(automationDetails).length;
	const hourOptions = Array.from({ length: 24 }, (_, hour) => ({
		value: String(hour),
		label: new Date(2026, 0, 1, hour).toLocaleTimeString([], { hour: 'numeric' })
	}));

	const policiesQuery = createQuery(() => ({
		queryKey: ['notification-settings', 'tenant-notices', 'policies'],
		queryFn: () => notifications.tenantNotices.listPolicies(),
		enabled: canManage
	}));
	const deliveriesQuery = createQuery(() => ({
		queryKey: ['notification-settings', 'tenant-notices', 'deliveries', 50],
		queryFn: () => notifications.tenantNotices.listDeliveries(50),
		enabled: canManage
	}));

	function deliveryStatusClass(status: NoticeDeliveryStatus): string {
		if (status === 'Sent') return 'border-emerald-500/40 text-emerald-700 dark:text-emerald-300';
		if (status === 'Accepted') return 'border-sky-500/40 text-sky-700 dark:text-sky-300';
		if (status === 'Retrying') return 'border-amber-500/40 text-amber-700 dark:text-amber-300';
		if (status === 'PermanentlyFailed') return 'border-destructive/40 text-destructive';
		return 'border-border text-muted-foreground';
	}

	function deliveryStatusLabel(status: NoticeDeliveryStatus): string {
		if (status === 'Queued') return 'Waiting';
		if (status === 'Accepted') return 'Accepted for delivery';
		if (status === 'Retrying') return 'Trying again';
		if (status === 'Sent') return 'Sent';
		return 'Needs attention';
	}

	function deliveryStatusExplanation(status: NoticeDeliveryStatus): string {
		if (status === 'Queued') return 'Waiting to send.';
		if (status === 'Accepted') return 'The delivery service accepted the message.';
		if (status === 'Retrying') return 'A temporary delivery problem occurred. Rental Command will try again.';
		if (status === 'Sent') return 'The message was sent.';
		return 'Check the error and the tenant’s contact details before trying again.';
	}

	function deliveryRoleLabel(role: string): string {
		if (role === 'PrimaryTenant') return 'Primary tenant';
		if (role === 'CoTenant') return 'Other tenant on the lease';
		if (role === 'Guarantor') return 'Guarantor';
		return 'Other occupant';
	}

	function deliveryChannelLabel(channel: string): string {
		if (channel === 'TenantPortal') return 'Tenant portal';
		if (channel === 'MobilePush') return 'Phone app';
		if (channel === 'Sms') return 'Text message';
		return 'Email';
	}

	let policyDrafts = $state<Record<string, UpsertTenantNoticePolicyRequest>>({});
	let templateDrafts = $state<Record<string, { subject: string; body: string; jurisdictionCode: string; confirmJurisdictionReviewed: boolean }>>({});
	let appliedDataKey = $state('');
	let expandedAutomation = $state<string | null>(null);
	let expandedTemplateSystemKey = $state<string | null>(null);
	let templateOpen = $state(false);
	let policyDirty = $state<Record<string, boolean>>({});
	let templateDirty = $state<Record<string, boolean>>({});

	const mergeFieldsQuery = createQuery(() => ({
		queryKey: ['notification-settings', 'tenant-notices', 'merge-fields', expandedTemplateSystemKey],
		queryFn: () => notifications.tenantNotices.mergeFields(expandedTemplateSystemKey!),
		enabled: canManage && expandedTemplateSystemKey != null
	}));

	$effect(() => {
		const policies = policiesQuery.data;
		if (!policies) return;
		const key = policies.map((item) => `${item.id}:${item.updatedAtUtc}:${item.templateCreatedAtUtc}`).join('|');
		if (key === appliedDataKey) return;
		const nextPolicies: Record<string, UpsertTenantNoticePolicyRequest> = {};
		const nextTemplates: Record<string, { subject: string; body: string; jurisdictionCode: string; confirmJurisdictionReviewed: boolean }> = {};
		for (const policy of policies) {
			const savedPolicyDraft: UpsertTenantNoticePolicyRequest = {
				automationKey: policy.automationKey,
				mode: policy.mode,
				classification: policy.classification,
				leadDays: policy.leadDays,
				sendHourLocal: policy.sendHourLocal,
				sendTenantPortal: policy.sendTenantPortal,
				sendMobilePush: policy.sendMobilePush,
				sendEmail: policy.sendEmail,
				sendSms: policy.sendSms,
				includePrimaryTenant: policy.includePrimaryTenant,
				includeCoTenant: policy.includeCoTenant,
				includeEligibleGuarantor: policy.includeEligibleGuarantor,
				includeOccupant: policy.classification === 'Legal' ? false : policy.includeOccupant,
				failureBehavior: policy.failureBehavior,
				workspaceNoticeTemplateVersionId: policy.workspaceNoticeTemplateVersionId,
				reviewedJurisdictionCode: policy.templateJurisdictionCode ?? policy.reviewedJurisdictionCode,
				confirmJurisdictionReviewed: policy.templateJurisdictionReviewedAtUtc != null
			};
			const savedTemplateDraft = {
				subject: policy.templateSubject,
				body: policy.templateBody,
				jurisdictionCode: policy.templateJurisdictionCode ?? '',
				confirmJurisdictionReviewed: policy.templateJurisdictionReviewedAtUtc != null
			};
			nextPolicies[policy.automationKey] =
				policyDirty[policy.automationKey] && policyDrafts[policy.automationKey]
					? policyDrafts[policy.automationKey]
					: savedPolicyDraft;
			nextTemplates[policy.automationKey] =
				templateDirty[policy.automationKey] && templateDrafts[policy.automationKey]
					? templateDrafts[policy.automationKey]
					: savedTemplateDraft;
		}
		policyDrafts = nextPolicies;
		templateDrafts = nextTemplates;
		appliedDataKey = key;
	});

	function copyFor(key: string): { label: string; detail: string } {
		return automationDetails[key] ?? { label: key, detail: 'Workspace tenant notice automation.' };
	}
	function autoAllowed(policy: TenantNoticePolicyResponse): boolean {
		return policy.canAutoSend;
	}

	function modeLabel(mode: TenantNoticeMode): string {
		if (mode === 'Off') return 'Off';
		if (mode === 'Draft') return 'Prepare for review';
		return 'Send after safeguards pass';
	}

	function sendTimeLabel(hour: number): string {
		return hourOptions[hour]?.label ?? `${hour}:00`;
	}

	function channelSummary(draft: UpsertTenantNoticePolicyRequest): string {
		const channels: string[] = [];
		if (draft.sendTenantPortal) channels.push('tenant portal');
		if (draft.sendMobilePush) channels.push('phone app');
		if (draft.sendEmail) channels.push('email');
		if (draft.sendSms) channels.push('text message');
		return channels.length > 0 ? channels.join(', ') : 'no destinations';
	}

	function recipientSummary(draft: UpsertTenantNoticePolicyRequest): string {
		const recipients: string[] = [];
		if (draft.includePrimaryTenant) recipients.push('primary tenant');
		if (draft.includeCoTenant) recipients.push('other tenants');
		if (draft.includeEligibleGuarantor) recipients.push('eligible guarantors');
		if (draft.includeOccupant) recipients.push('other occupants');
		return recipients.length > 0 ? recipients.join(', ') : 'no recipients';
	}

	function policySummary(draft: UpsertTenantNoticePolicyRequest): string {
		return `${modeLabel(draft.mode)} · ${draft.leadDays} days ahead · ${sendTimeLabel(draft.sendHourLocal)} · ${channelSummary(draft)} · ${recipientSummary(draft)}`;
	}

	function markPolicyDirty(key: string) {
		policyDirty = { ...policyDirty, [key]: true };
	}

	function markTemplateDirty(key: string) {
		templateDirty = { ...templateDirty, [key]: true };
	}

	const savePolicyMutation = createMutation(() => ({
		mutationFn: (key: string) => notifications.tenantNotices.updatePolicy(key, policyDrafts[key]),
		onSuccess: (saved) => {
			queryClient.invalidateQueries({ queryKey: ['notification-settings', 'tenant-notices', 'policies'] });
			policyDirty = { ...policyDirty, [saved.automationKey]: false };
			showSuccess(`${copyFor(saved.automationKey).label} was saved.`);
		},
		onError: (error) => showError(apiErrorMessage(error))
	}));

	const saveTemplateMutation = createMutation(() => ({
		mutationFn: (key: string) => {
			const draft = templateDrafts[key];
			return notifications.tenantNotices.createTemplateVersion(key, {
				subject: draft.subject,
				body: draft.body,
				jurisdictionCode: draft.jurisdictionCode.trim() || null,
				confirmJurisdictionReviewed: draft.confirmJurisdictionReviewed
			});
		},
		onSuccess: (saved) => {
			queryClient.invalidateQueries({ queryKey: ['notification-settings', 'tenant-notices', 'policies'] });
			templateDirty = { ...templateDirty, [saved.automationKey]: false };
			showSuccess(`${copyFor(saved.automationKey).label} now uses the updated message.`);
		},
		onError: (error) => showError(apiErrorMessage(error))
	}));

	const restoreTemplateMutation = createMutation(() => ({
		mutationFn: (key: string) => notifications.tenantNotices.restoreDefault(key),
		onSuccess: (saved) => {
			queryClient.invalidateQueries({ queryKey: ['notification-settings', 'tenant-notices', 'policies'] });
			templateDirty = { ...templateDirty, [saved.automationKey]: false };
			showSuccess(`${copyFor(saved.automationKey).label} now uses the supplied message.`);
		},
		onError: (error) => showError(apiErrorMessage(error))
	}));

	function selectMode(policy: TenantNoticePolicyResponse, mode: TenantNoticeMode) {
		if (mode === 'Auto' && !autoAllowed(policy)) return;
		policyDrafts[policy.automationKey].mode = mode;
		markPolicyDirty(policy.automationKey);
	}

	function togglePolicy(policy: TenantNoticePolicyResponse) {
		if (expandedAutomation === policy.automationKey) {
			expandedAutomation = null;
			templateOpen = false;
			expandedTemplateSystemKey = null;
			return;
		}
		expandedAutomation = policy.automationKey;
		templateOpen = false;
		expandedTemplateSystemKey = null;
	}

	function toggleTemplate(policy: TenantNoticePolicyResponse) {
		templateOpen = !templateOpen;
		expandedTemplateSystemKey = templateOpen ? policy.templateSystemKey : null;
	}

	const hasUnsavedChanges = $derived(
		Object.values(policyDirty).some(Boolean) || Object.values(templateDirty).some(Boolean)
	);
	const savedSummary = $derived(
		canManage
			? 'Each saved tenant message is summarized below. Open one only when you need to change it.'
			: 'An administrator manages tenant-message timing, recipients, and delivery.'
	);
</script>

<svelte:head><title>Tenant messages · Rental Command</title></svelte:head>

<NotificationSetupJourney
	currentStep={3}
	description="Choose which reminders Rental Command prepares, when they are due, and who reviews them before delivery."
	{savedSummary}
	{returnHref}
	{canManage}
	{hasUnsavedChanges}
	helpTitle="How tenant messages work"
	helpDescription="Each reminder keeps its own timing, recipients, destinations, and message."
	helpGuidance="Keep legal notices set to Prepare for review until the message has been checked for the correct state or local rules. Rental Command keeps prior message versions for the audit history."
	helpHref="/docs/notices"
	helpLinkLabel="Open the tenant notices guide"
>
	<div class="space-y-5" data-testid="tenant-notices-page">
		<Card.Root class="gap-0 py-0">
			<Card.Content class="grid gap-3 p-5 text-sm md:grid-cols-3">
				<div><p class="font-medium">Off</p><p class="text-muted-foreground">Do not prepare this message.</p></div>
				<div><p class="font-medium">Prepare for review</p><p class="text-muted-foreground">Create a draft for an authorized team member to check.</p></div>
				<div><p class="font-medium">Send after safeguards pass</p><p class="text-muted-foreground">Send only when the saved rules and required legal review allow it.</p></div>
			</Card.Content>
		</Card.Root>

		{#if !canManage}
			<Card.Root>
				<Card.Content class="p-6">
					<h2 class="font-semibold">An administrator manages tenant messages</h2>
					<p class="mt-2 text-sm text-muted-foreground">
						Your team assignment does not allow changes to tenant-message timing, recipients, or legal review.
					</p>
				</Card.Content>
			</Card.Root>
		{:else if policiesQuery.isLoading}
			<LoadingState label="Loading tenant messages" testid="tenant-notices-loading" />
		{:else if policiesQuery.isError}
			<Card.Root>
				<Card.Content class="space-y-4 p-6">
					<p class="text-sm text-destructive">We couldn't load tenant-message settings.</p>
					<Button variant="outline" onclick={() => policiesQuery.refetch()}>Retry</Button>
				</Card.Content>
			</Card.Root>
		{:else if (policiesQuery.data?.length ?? 0) < suppliedAutomationCount}
			<Card.Root>
				<Card.Content class="space-y-4 p-6">
					<div>
						<h2 class="font-semibold">Some supplied messages are unavailable</h2>
						<p class="mt-2 text-sm text-muted-foreground">
							Reload this page. If messages are still missing, workspace setup needs attention.
						</p>
					</div>
					<Button variant="outline" onclick={() => policiesQuery.refetch()}>Reload messages</Button>
				</Card.Content>
			</Card.Root>
		{:else}
			<div class="space-y-3">
				{#each policiesQuery.data ?? [] as policy (policy.id)}
					{@const automation = copyFor(policy.automationKey)}
					{@const draft = policyDrafts[policy.automationKey]}
					{@const templateDraft = templateDrafts[policy.automationKey]}
					{#if draft && templateDraft}
						<NotificationPolicyAccordion
							id={`tenant-notice-${policy.automationKey}`}
							title={automation.label}
							description={automation.detail}
							summary={`${policyDirty[policy.automationKey] ? 'Unsaved changes · ' : ''}${policySummary(draft)}`}
							badge={policy.classification === 'Legal' ? 'Legal notice' : policy.classification === 'Courtesy' ? 'Courtesy reminder' : 'Lease message'}
							open={expandedAutomation === policy.automationKey}
							ontoggle={() => togglePolicy(policy)}
						>
							<div class="space-y-6">
								<section>
									<h3 class="text-sm font-semibold">1. What should Rental Command do?</h3>
									<div class="mt-3 grid gap-2 sm:grid-cols-3">
										{#each [
											{ value: 'Off', label: 'Off', detail: 'Do not prepare this message.' },
											{ value: 'Draft', label: 'Prepare for review', detail: 'Create a draft for your team to check.' },
											{ value: 'Auto', label: 'Send after safeguards pass', detail: 'Send only when every saved safeguard allows it.' }
										] as mode (mode.value)}
											<button
												type="button"
												class={`min-h-24 rounded-lg border p-3 text-left ${
													draft.mode === mode.value
														? 'border-primary bg-primary/5'
														: 'border-border hover:bg-muted/40'
												}`}
												disabled={mode.value === 'Auto' && !autoAllowed(policy)}
												onclick={() => selectMode(policy, mode.value as TenantNoticeMode)}
											>
												<span class="block text-sm font-medium">{mode.label}</span>
												<span class="mt-1 block text-xs text-muted-foreground">{mode.detail}</span>
											</button>
										{/each}
									</div>
									{#if policy.classification === 'Legal' && !autoAllowed(policy)}
										<p class="mt-2 text-xs text-amber-700 dark:text-amber-400">
											Sending stays unavailable until an administrator saves a message reviewed for the correct state or local rules.
										</p>
									{/if}
								</section>

								<section>
									<h3 class="text-sm font-semibold">2. When should it prepare the message?</h3>
									<div class="mt-3 grid gap-4 sm:grid-cols-2">
										<div>
											<label for={`notice-lead-${policy.automationKey}`} class="mb-1 block text-sm font-medium">Days before the event</label>
											<Input
												id={`notice-lead-${policy.automationKey}`}
												type="number"
												min="0"
												max="365"
												bind:value={draft.leadDays}
												oninput={() => markPolicyDirty(policy.automationKey)}
											/>
											<p class="mt-1 text-xs text-muted-foreground">Use 0 for the same day.</p>
										</div>
										<div>
											<label for={`notice-hour-${policy.automationKey}`} class="mb-1 block text-sm font-medium">Prepare at</label>
											<Select.Root
												type="single"
												value={String(draft.sendHourLocal)}
												onValueChange={(value) => {
													if (!value) return;
													draft.sendHourLocal = Number(value);
													markPolicyDirty(policy.automationKey);
												}}
											>
												<Select.Trigger id={`notice-hour-${policy.automationKey}`} class="w-full">
													{sendTimeLabel(draft.sendHourLocal)}
												</Select.Trigger>
												<Select.Content>
													{#each hourOptions as option (option.value)}
														<Select.Item value={option.value} label={option.label}>{option.label}</Select.Item>
													{/each}
												</Select.Content>
											</Select.Root>
											<p class="mt-1 text-xs text-muted-foreground">Uses the rental's local time.</p>
										</div>
									</div>
								</section>

								<section>
									<h3 class="text-sm font-semibold">3. Where should it be sent?</h3>
									<div class="mt-3 grid gap-2 sm:grid-cols-2 lg:grid-cols-4">
										<label class="flex min-h-14 items-center gap-3 rounded-lg border border-border p-3">
											<Checkbox checked={draft.sendTenantPortal} onCheckedChange={(value) => { draft.sendTenantPortal = value === true; markPolicyDirty(policy.automationKey); }} />
											<span class="text-sm">Tenant portal</span>
										</label>
										<label class="flex min-h-14 items-center gap-3 rounded-lg border border-border p-3">
											<Checkbox checked={draft.sendMobilePush} onCheckedChange={(value) => { draft.sendMobilePush = value === true; markPolicyDirty(policy.automationKey); }} />
											<span class="text-sm">Phone app</span>
										</label>
										<label class="flex min-h-14 items-center gap-3 rounded-lg border border-border p-3">
											<Checkbox checked={draft.sendEmail} onCheckedChange={(value) => { draft.sendEmail = value === true; markPolicyDirty(policy.automationKey); }} />
											<span class="text-sm">Email</span>
										</label>
										<label class="flex min-h-14 items-center gap-3 rounded-lg border border-border p-3">
											<Checkbox checked={draft.sendSms} onCheckedChange={(value) => { draft.sendSms = value === true; markPolicyDirty(policy.automationKey); }} />
											<span class="text-sm">Text message</span>
										</label>
									</div>
								</section>

								<section>
									<h3 class="text-sm font-semibold">4. Who should receive it?</h3>
									<div class="mt-3 grid gap-2 sm:grid-cols-2">
										<label class="flex min-h-14 items-center gap-3 rounded-lg border border-border p-3">
											<Checkbox checked={draft.includePrimaryTenant} onCheckedChange={(value) => { draft.includePrimaryTenant = value === true; markPolicyDirty(policy.automationKey); }} />
											<span class="text-sm">Primary tenant</span>
										</label>
										<label class="flex min-h-14 items-center gap-3 rounded-lg border border-border p-3">
											<Checkbox checked={draft.includeCoTenant} onCheckedChange={(value) => { draft.includeCoTenant = value === true; markPolicyDirty(policy.automationKey); }} />
											<span class="text-sm">Other tenants on the lease</span>
										</label>
										<label class="flex min-h-14 items-center gap-3 rounded-lg border border-border p-3">
											<Checkbox checked={draft.includeEligibleGuarantor} onCheckedChange={(value) => { draft.includeEligibleGuarantor = value === true; markPolicyDirty(policy.automationKey); }} />
											<span class="text-sm">Guarantors marked eligible for this notice</span>
										</label>
										<label class="flex min-h-14 items-center gap-3 rounded-lg border border-border p-3 opacity-70">
											<Checkbox checked={draft.includeOccupant} disabled={policy.classification === 'Legal'} onCheckedChange={(value) => { draft.includeOccupant = value === true; markPolicyDirty(policy.automationKey); }} />
											<span class="text-sm">Other occupants {policy.classification === 'Legal' ? '(not allowed for legal notices)' : ''}</span>
										</label>
									</div>
									<p class="mt-2 text-xs text-muted-foreground">
										Rental Command sends only to people currently connected to the lease and only when a real destination is available.
									</p>
								</section>

								<details class="rounded-lg border border-border p-4">
									<summary class="cursor-pointer text-sm font-medium">Advanced delivery settings</summary>
									<div class="mt-4">
										<label for={`notice-failure-${policy.automationKey}`} class="mb-1 block text-sm font-medium">If delivery cannot finish</label>
										<Select.Root
											type="single"
											value={draft.failureBehavior}
											onValueChange={(value) => {
												if (!value) return;
												draft.failureBehavior = value as typeof draft.failureBehavior;
												markPolicyDirty(policy.automationKey);
											}}
										>
											<Select.Trigger id={`notice-failure-${policy.automationKey}`} class="w-full sm:w-80">
												{draft.failureBehavior === 'StopAndRequireReview'
													? 'Pause and ask me'
													: draft.failureBehavior === 'RetryThenDraft'
														? 'Try again, then keep a draft'
														: 'Try again, then mark it failed'}
											</Select.Trigger>
											<Select.Content>
												<Select.Item value="StopAndRequireReview" label="Pause and ask me">Pause and ask me</Select.Item>
												<Select.Item value="RetryThenDraft" label="Try again, then keep a draft">Try again, then keep a draft</Select.Item>
												<Select.Item value="RetryThenFail" label="Try again, then mark it failed">Try again, then mark it failed</Select.Item>
											</Select.Content>
										</Select.Root>
									</div>
								</details>

								<div class="flex flex-wrap items-center gap-3">
									<Button onclick={() => savePolicyMutation.mutate(policy.automationKey)} disabled={savePolicyMutation.isPending}>
										{savePolicyMutation.isPending ? 'Saving…' : 'Save timing and recipients'}
									</Button>
									<Button variant="outline" onclick={() => toggleTemplate(policy)}>
										{templateOpen ? 'Close message editor' : 'Review and edit the message'}
									</Button>
								</div>

								{#if templateOpen}
									<section class="space-y-4 border-t border-border pt-5">
										<div>
											<h3 class="font-semibold">5. Review the message</h3>
											<p class="mt-1 text-sm text-muted-foreground">
												Edit the supplied starting message. Saving creates a new version so prior wording remains in the audit history.
											</p>
											{#if policy.templateUpdateAvailable}
												<p class="mt-2 text-sm text-amber-700 dark:text-amber-400">
													A newer Rental Command starting message is available.
												</p>
											{/if}
										</div>
										<div>
											<label for={`template-subject-${policy.automationKey}`} class="mb-1 block text-sm font-medium">Subject</label>
											<Input id={`template-subject-${policy.automationKey}`} bind:value={templateDraft.subject} oninput={() => markTemplateDirty(policy.automationKey)} />
										</div>
										<div>
											<label for={`template-body-${policy.automationKey}`} class="mb-1 block text-sm font-medium">Message</label>
											<textarea
												id={`template-body-${policy.automationKey}`}
												class="min-h-32 w-full rounded-md border border-input bg-background px-3 py-2 text-sm"
												bind:value={templateDraft.body}
												oninput={() => markTemplateDirty(policy.automationKey)}
											></textarea>
										</div>
										<details class="rounded-lg border border-border bg-muted/30 p-4">
											<summary class="cursor-pointer text-sm font-medium">Personalize with rental details</summary>
											{#if mergeFieldsQuery.isLoading}
												<LoadingState label="Loading available rental details" variant="spinner" testid="tenant-notice-merge-fields-loading" />
											{:else if mergeFieldsQuery.isError}
												<div class="mt-2 flex flex-wrap items-center gap-2">
													<p class="text-xs text-destructive">We couldn't load the available details.</p>
													<Button size="sm" variant="outline" onclick={() => mergeFieldsQuery.refetch()}>Retry</Button>
												</div>
											{:else}
												<div class="mt-3 grid gap-2 sm:grid-cols-2">
													{#each mergeFieldsQuery.data ?? [] as field (field.key)}
														<div class="rounded-md bg-background p-3">
															<p class="text-sm font-medium">{field.label} <code class="ml-1 text-xs">{field.token}</code></p>
															<p class="mt-1 text-xs text-muted-foreground">{field.description}</p>
															<p class="mt-1 text-xs">Example: {field.example}</p>
														</div>
													{/each}
												</div>
											{/if}
										</details>
										{#if !mergeFieldsQuery.isLoading && !mergeFieldsQuery.isError}
											<NoticePreview systemKey={policy.templateSystemKey} subject={templateDraft.subject} body={templateDraft.body} mergeFields={mergeFieldsQuery.data ?? []} />
										{/if}
										{#if policy.classification === 'Legal'}
											<div class="grid gap-3 sm:grid-cols-2">
												<div>
													<label for={`template-jurisdiction-${policy.automationKey}`} class="mb-1 block text-sm font-medium">State or local rules reviewed</label>
													<Input id={`template-jurisdiction-${policy.automationKey}`} bind:value={templateDraft.jurisdictionCode} placeholder="For example, Ohio" oninput={() => markTemplateDirty(policy.automationKey)} />
												</div>
												<label class="flex min-h-14 items-center gap-3 self-end rounded-lg border border-border p-3">
													<Checkbox checked={templateDraft.confirmJurisdictionReviewed} onCheckedChange={(value) => { templateDraft.confirmJurisdictionReviewed = value === true; markTemplateDirty(policy.automationKey); }} />
													<span class="text-sm">I reviewed this message for those rules</span>
												</label>
											</div>
										{/if}
										<div class="flex flex-wrap gap-3">
											<Button
												onclick={() => saveTemplateMutation.mutate(policy.automationKey)}
												disabled={saveTemplateMutation.isPending || !templateDraft.subject.trim() || !templateDraft.body.trim() || (policy.classification === 'Legal' && (!templateDraft.jurisdictionCode.trim() || !templateDraft.confirmJurisdictionReviewed))}
											>
												{saveTemplateMutation.isPending ? 'Saving…' : 'Save and use this message'}
											</Button>
											<Button variant="outline" onclick={() => restoreTemplateMutation.mutate(policy.automationKey)} disabled={restoreTemplateMutation.isPending}>
												Use the current Rental Command starting message
											</Button>
										</div>
										<details class="text-xs text-muted-foreground">
											<summary class="cursor-pointer font-medium">Technical details</summary>
											<p class="mt-2">Message version {policy.templateVersion} · {policy.templateIsCustomized ? 'customized' : 'supplied'}</p>
											<p class="mt-1">{policy.templateProvenance}</p>
										</details>
									</section>
								{/if}
							</div>
						</NotificationPolicyAccordion>
					{/if}
				{/each}
			</div>

			<Card.Root class="gap-0 py-0" data-testid="tenant-notice-delivery-status">
				<Card.Content class="space-y-4 p-6">
					<div>
						<h2 class="text-lg font-semibold">Recent tenant messages</h2>
						<p class="mt-1 text-sm text-muted-foreground">See whether recent messages were sent or need attention.</p>
					</div>
					{#if deliveriesQuery.isLoading}
						<LoadingState label="Loading recent tenant messages" variant="spinner" testid="tenant-notice-deliveries-loading" />
					{:else if deliveriesQuery.isError}
						<div class="flex flex-wrap items-center gap-3">
							<p class="text-sm text-destructive">We couldn't load recent tenant messages.</p>
							<Button variant="outline" size="sm" onclick={() => deliveriesQuery.refetch()}>Retry</Button>
						</div>
					{:else if (deliveriesQuery.data?.length ?? 0) === 0}
						<p class="rounded-md bg-muted/40 p-4 text-sm text-muted-foreground">No tenant message has been prepared yet.</p>
					{:else}
						<div class="space-y-3">
							{#each deliveriesQuery.data ?? [] as delivery (delivery.evidenceId)}
								<div class="rounded-lg border border-border p-4">
									<div class="flex flex-wrap items-center gap-2">
										<p class="font-medium">{delivery.subject}</p>
										<span class={`rounded-full border px-2 py-0.5 text-xs font-medium ${deliveryStatusClass(delivery.status)}`}>
											{deliveryStatusLabel(delivery.status)}
										</span>
									</div>
									<p class="mt-1 text-xs text-muted-foreground">{deliveryStatusExplanation(delivery.status)}</p>
									<p class="mt-1 text-sm text-muted-foreground">
										{deliveryRoleLabel(delivery.recipientRole)} · {deliveryChannelLabel(delivery.channel)} · {delivery.destination}
									</p>
									{#if delivery.lastError}<p class="mt-2 text-sm text-destructive">{delivery.lastError}</p>{/if}
									<details class="mt-3 text-xs text-muted-foreground">
										<summary class="cursor-pointer font-medium">Technical details</summary>
										<p class="mt-2">Attempts: {delivery.attemptCount}</p>
										{#if delivery.provider}<p>{delivery.provider}{delivery.providerMessageId ? ` · ${delivery.providerMessageId}` : ''}</p>{/if}
										{#if delivery.nextAttemptAtUtc}<p>Next try: {new Date(delivery.nextAttemptAtUtc).toLocaleString()}</p>{/if}
									</details>
								</div>
							{/each}
						</div>
					{/if}
				</Card.Content>
			</Card.Root>
		{/if}
	</div>
</NotificationSetupJourney>
