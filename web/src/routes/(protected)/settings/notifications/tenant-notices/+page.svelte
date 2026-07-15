<script lang="ts">
	import { createMutation, createQuery, useQueryClient } from '@tanstack/svelte-query';
	import { notifications } from '$lib/api/endpoints/notifications';
	import type { TenantNoticeMode, TenantNoticePolicyResponse, UpsertTenantNoticePolicyRequest } from '$lib/api/types/notification';
	import { hasCapability } from '$lib/stores/auth.svelte';
	import { getAuthState } from '$lib/stores/auth.svelte';
	import { canAccessPathForEnvelope, safeLandingForAccess } from '$lib/auth/experience-policy';
	import { showError, showSuccess, apiErrorMessage } from '$lib/utils/toast';
	import { Button } from '$lib/components/ui/button';
	import { Checkbox } from '$lib/components/ui/checkbox';
	import { Input } from '$lib/components/ui/input';
	import * as Select from '$lib/components/ui/select';
	import * as Card from '$lib/components/ui/card';
	import { ArrowLeft, ExternalLink } from '@lucide/svelte';

	const queryClient = useQueryClient();
	const authState = getAuthState();
	const returnHref = $derived.by(() => {
		const access = authState.accessEnvelope;
		if (!access) return '/';
		return canAccessPathForEnvelope(access, '/settings')
			? '/settings#notifications'
			: (safeLandingForAccess(access) ?? '/');
	});
	const canManage = $derived(hasCapability('notifications.tenant-notices.manage'));
	const automationDetails: Record<string, { label: string; detail: string }> = {
		'rent-reminder': { label: 'Rent reminder', detail: 'A courtesy reminder before rent is due.' },
		'lease-renewal-offer': { label: 'Lease renewal offer', detail: 'An operational offer after the lease-ending decision is Offer renewal.' },
		'month-to-month-offer': { label: 'Month-to-month offer', detail: 'An operational offer after the lease-ending decision is Offer month-to-month.' },
		'lease-non-renewal': { label: 'Lease expiration / non-renewal', detail: 'A legal notice after the lease-ending decision is Non-renewal / move-out.' },
		'late-rent-late-fee': { label: 'Past-due rent / late fee', detail: 'A legal notice when rent remains past due and applicable late-fee facts are known.' }
	};
	const suppliedAutomationCount = Object.keys(automationDetails).length;

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

	function deliveryStatusClass(status: string): string {
		if (status === 'Delivered' || status === 'Accepted') return 'border-emerald-500/40 text-emerald-700 dark:text-emerald-300';
		if (status === 'Failed') return 'border-destructive/40 text-destructive';
		if (status === 'Retrying') return 'border-amber-500/40 text-amber-700 dark:text-amber-300';
		return 'border-border text-muted-foreground';
	}

	function deliveryStatusExplanation(status: string): string {
		if (status === 'Queued') return 'Waiting for the delivery worker to make the first attempt.';
		if (status === 'Accepted') return 'The provider accepted the message; final delivery is not confirmed yet.';
		if (status === 'Retrying') return 'A provider attempt failed temporarily and another attempt is scheduled.';
		if (status === 'Delivered') return 'The provider confirmed delivery.';
		if (status === 'Failed') return 'Delivery retries ended. Review the error and recipient details.';
		return 'Current status reported by the delivery provider.';
	}

	let policyDrafts = $state<Record<string, UpsertTenantNoticePolicyRequest>>({});
	let templateDrafts = $state<Record<string, { subject: string; body: string; jurisdictionCode: string; confirmJurisdictionReviewed: boolean }>>({});
	let appliedDataKey = $state('');
	let expandedAutomation = $state<string | null>(null);

	$effect(() => {
		const policies = policiesQuery.data;
		if (!policies) return;
		const key = policies.map((item) => `${item.id}:${item.updatedAtUtc}:${item.templateCreatedAtUtc}`).join('|');
		if (key === appliedDataKey) return;
		const nextPolicies: Record<string, UpsertTenantNoticePolicyRequest> = {};
		const nextTemplates: Record<string, { subject: string; body: string; jurisdictionCode: string; confirmJurisdictionReviewed: boolean }> = {};
		for (const policy of policies) {
			nextPolicies[policy.automationKey] = {
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
			nextTemplates[policy.automationKey] = {
				subject: policy.templateSubject,
				body: policy.templateBody,
				jurisdictionCode: policy.templateJurisdictionCode ?? '',
				confirmJurisdictionReviewed: policy.templateJurisdictionReviewedAtUtc != null
			};
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

	const savePolicyMutation = createMutation(() => ({
		mutationFn: (key: string) => notifications.tenantNotices.updatePolicy(key, policyDrafts[key]),
		onSuccess: (saved) => {
			queryClient.invalidateQueries({ queryKey: ['notification-settings', 'tenant-notices', 'policies'] });
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
			showSuccess(`${copyFor(saved.automationKey).label} now uses the new immutable template version.`);
		},
		onError: (error) => showError(apiErrorMessage(error))
	}));

	const restoreTemplateMutation = createMutation(() => ({
		mutationFn: (key: string) => notifications.tenantNotices.restoreDefault(key),
		onSuccess: (saved) => {
			queryClient.invalidateQueries({ queryKey: ['notification-settings', 'tenant-notices', 'policies'] });
			showSuccess(`${copyFor(saved.automationKey).label} now uses a new version of the current supplied default.`);
		},
		onError: (error) => showError(apiErrorMessage(error))
	}));

	function selectMode(policy: TenantNoticePolicyResponse, mode: TenantNoticeMode) {
		if (mode === 'Auto' && !autoAllowed(policy)) return;
		policyDrafts[policy.automationKey].mode = mode;
	}
</script>

<svelte:head><title>Tenant notices · Rental Command</title></svelte:head>

<div class="mx-auto max-w-5xl space-y-6" data-testid="tenant-notices-page">
	<div>
		<a href={returnHref} class="mb-3 inline-flex min-h-11 items-center gap-2 text-sm text-muted-foreground hover:text-foreground"><ArrowLeft class="size-4" /> Back</a>
		<div class="flex flex-wrap items-start justify-between gap-4">
			<div><h1 class="text-2xl font-semibold tracking-tight">Tenant notices</h1><p class="mt-1 text-sm text-muted-foreground">Each automation has its own mode, timing, tenant channels, recipient roles, and editable supplied template.</p></div>
			<a href="/docs/settings-and-notifications" class="inline-flex min-h-11 items-center gap-2 text-sm font-medium text-primary hover:underline">How this works <ExternalLink class="size-4" /></a>
		</div>
	</div>

	<Card.Root class="gap-0 py-0"><Card.Content class="grid gap-3 p-5 text-sm md:grid-cols-3"><div><p class="font-medium">Off</p><p class="text-muted-foreground">No draft or delivery is created.</p></div><div><p class="font-medium">Draft for review</p><p class="text-muted-foreground">Creates a draft for an authorized team member to check.</p></div><div><p class="font-medium">Send automatically</p><p class="text-muted-foreground">Uses the saved policy only when the notice and legal review permit it.</p></div></Card.Content></Card.Root>

	{#if !canManage}
		<Card.Root><Card.Content class="p-6"><h2 class="font-semibold">Tenant notice access required</h2><p class="mt-2 text-sm text-muted-foreground">Your team assignment does not include permission to manage tenant delivery policies and legal templates.</p></Card.Content></Card.Root>
	{:else if policiesQuery.isLoading}
		<Card.Root><Card.Content class="p-6 text-sm text-muted-foreground">Loading tenant notice policies and supplied templates…</Card.Content></Card.Root>
	{:else if policiesQuery.isError}
		<Card.Root><Card.Content class="space-y-4 p-6"><p class="text-sm text-destructive">We couldn't load tenant notice settings.</p><Button variant="outline" onclick={() => policiesQuery.refetch()}>Retry</Button></Card.Content></Card.Root>
	{:else if (policiesQuery.data?.length ?? 0) < suppliedAutomationCount}
		<Card.Root><Card.Content class="space-y-4 p-6"><div><h2 class="font-semibold">Supplied notices are unavailable</h2><p class="mt-2 text-sm text-muted-foreground">Every new workspace receives five complete editable templates and safe Draft-for-review policies. Reload this page; if they are still missing, workspace setup needs attention.</p></div><Button variant="outline" onclick={() => policiesQuery.refetch()}>Reload notices</Button></Card.Content></Card.Root>
	{:else}
		<div class="space-y-4">
			{#each policiesQuery.data ?? [] as policy (policy.id)}
				{@const automation = copyFor(policy.automationKey)}
				{@const draft = policyDrafts[policy.automationKey]}
				{@const templateDraft = templateDrafts[policy.automationKey]}
				{#if draft && templateDraft}
					<Card.Root class="gap-0 py-0" data-testid={`tenant-notice-${policy.automationKey}`}>
						<Card.Content class="space-y-5 p-6">
							<div class="flex flex-wrap items-start justify-between gap-4"><div><div class="flex flex-wrap items-center gap-2"><h2 class="text-lg font-semibold">{automation.label}</h2><span class="rounded-full border border-border px-2 py-0.5 text-xs text-muted-foreground">{policy.classification}</span></div><p class="mt-1 text-sm text-muted-foreground">{automation.detail}</p></div><div class="max-w-sm text-right text-xs text-muted-foreground"><p>Workspace template v{policy.templateVersion}{policy.templateIsCustomized ? ' · customized' : ' · supplied'}</p><p class="mt-1">{policy.templateProvenance}</p></div></div>

							<div><p class="mb-2 text-sm font-medium">Automation mode</p><div class="inline-flex flex-wrap overflow-hidden rounded-lg border border-border">{#each [{ value: 'Off', label: 'Off' }, { value: 'Draft', label: 'Draft for review' }, { value: 'Auto', label: 'Send automatically' }] as mode (mode.value)}<button type="button" class={`min-h-11 px-4 text-sm font-medium ${draft.mode === mode.value ? 'bg-primary text-primary-foreground' : 'bg-background text-muted-foreground'}`} disabled={mode.value === 'Auto' && !autoAllowed(policy)} onclick={() => selectMode(policy, mode.value as TenantNoticeMode)}>{mode.label}</button>{/each}</div>{#if policy.classification === 'Legal' && !autoAllowed(policy)}<p class="mt-2 text-xs text-amber-700 dark:text-amber-400">Automatic delivery stays unavailable until an administrator saves a jurisdiction-reviewed template version.</p>{/if}</div>

							<div class="grid gap-4 sm:grid-cols-2"><div><label for={`notice-lead-${policy.automationKey}`} class="mb-1 block text-sm font-medium">Lead days</label><Input id={`notice-lead-${policy.automationKey}`} type="number" min="0" max="365" bind:value={draft.leadDays} /></div><div><label for={`notice-hour-${policy.automationKey}`} class="mb-1 block text-sm font-medium">Local send hour</label><Input id={`notice-hour-${policy.automationKey}`} type="number" min="0" max="23" bind:value={draft.sendHourLocal} /></div></div>

							<div><p class="mb-2 text-sm font-medium">Tenant delivery channels</p><div class="grid gap-2 sm:grid-cols-2 lg:grid-cols-4"><label class="flex min-h-14 items-center gap-3 rounded-lg border border-border p-3"><Checkbox checked={draft.sendTenantPortal} onCheckedChange={(value) => (draft.sendTenantPortal = value === true)} /><span class="text-sm">Portal / in-app</span></label><label class="flex min-h-14 items-center gap-3 rounded-lg border border-border p-3"><Checkbox checked={draft.sendMobilePush} onCheckedChange={(value) => (draft.sendMobilePush = value === true)} /><span class="text-sm">Mobile push</span></label><label class="flex min-h-14 items-center gap-3 rounded-lg border border-border p-3"><Checkbox checked={draft.sendEmail} onCheckedChange={(value) => (draft.sendEmail = value === true)} /><span class="text-sm">Email</span></label><label class="flex min-h-14 items-center gap-3 rounded-lg border border-border p-3"><Checkbox checked={draft.sendSms} onCheckedChange={(value) => (draft.sendSms = value === true)} /><span class="text-sm">SMS</span></label></div><p class="mt-2 text-xs text-muted-foreground">These tenant channels are independent of My alerts and Team routing.</p></div>

							<div><p class="mb-2 text-sm font-medium">Eligible relationship roles</p><div class="grid gap-2 sm:grid-cols-2"><label class="flex min-h-14 items-center gap-3 rounded-lg border border-border p-3"><Checkbox checked={draft.includePrimaryTenant} onCheckedChange={(value) => (draft.includePrimaryTenant = value === true)} /><span class="text-sm">Effective primary tenant</span></label><label class="flex min-h-14 items-center gap-3 rounded-lg border border-border p-3"><Checkbox checked={draft.includeCoTenant} onCheckedChange={(value) => (draft.includeCoTenant = value === true)} /><span class="text-sm">Effective co-tenant</span></label><label class="flex min-h-14 items-center gap-3 rounded-lg border border-border p-3"><Checkbox checked={draft.includeEligibleGuarantor} onCheckedChange={(value) => (draft.includeEligibleGuarantor = value === true)} /><span class="text-sm">Explicitly eligible guarantor</span></label><label class="flex min-h-14 items-center gap-3 rounded-lg border border-border p-3 opacity-70"><Checkbox checked={draft.includeOccupant} disabled={policy.classification === 'Legal'} onCheckedChange={(value) => (draft.includeOccupant = value === true)} /><span class="text-sm">Occupant {policy.classification === 'Legal' ? '(not eligible for legal notices)' : ''}</span></label></div><p class="mt-2 text-xs text-muted-foreground">At delivery time the server uses effective Lease Management parties and only channels with a real destination. Guarantors require explicit legal eligibility; occupants are never included in legal notices merely because they live there.</p></div>

							<div><label for={`notice-failure-${policy.automationKey}`} class="mb-1 block text-sm font-medium">If delivery cannot complete</label><Select.Root type="single" value={draft.failureBehavior} onValueChange={(value) => value && (draft.failureBehavior = value as typeof draft.failureBehavior)}><Select.Trigger id={`notice-failure-${policy.automationKey}`} class="w-full sm:w-72">{draft.failureBehavior === 'StopAndRequireReview' ? 'Stop and require review' : draft.failureBehavior === 'RetryThenDraft' ? 'Retry, then keep a draft' : 'Retry, then mark failed'}</Select.Trigger><Select.Content><Select.Item value="StopAndRequireReview" label="Stop and require review">Stop and require review</Select.Item><Select.Item value="RetryThenDraft" label="Retry, then keep a draft">Retry, then keep a draft</Select.Item><Select.Item value="RetryThenFail" label="Retry, then mark failed">Retry, then mark failed</Select.Item></Select.Content></Select.Root></div>

							<div class="flex flex-wrap items-center gap-3"><Button onclick={() => savePolicyMutation.mutate(policy.automationKey)} disabled={savePolicyMutation.isPending}>{savePolicyMutation.isPending ? 'Saving…' : 'Save automation'}</Button><Button variant="outline" onclick={() => (expandedAutomation = expandedAutomation === policy.automationKey ? null : policy.automationKey)}>{expandedAutomation === policy.automationKey ? 'Close template' : 'Review and edit template'}</Button><p class="text-xs text-muted-foreground">Currently bound to workspace template v{policy.templateVersion}.</p></div>

							{#if expandedAutomation === policy.automationKey}
								<div class="space-y-4 border-t border-border pt-5"><div><h3 class="font-semibold">Editable workspace template</h3><p class="mt-1 text-sm text-muted-foreground">{policy.templateProvenance}. Saving appends and binds a new immutable workspace version; it never overwrites the prior version. Restoring the supplied default also creates a new workspace version, so history is preserved.</p>{#if policy.templateUpdateAvailable}<p class="mt-2 text-sm text-amber-700 dark:text-amber-400">A newer Rental Command supplied version is available. Restore it below to create and bind a new workspace version.</p>{/if}</div><div><label for={`template-subject-${policy.automationKey}`} class="mb-1 block text-sm font-medium">Subject</label><Input id={`template-subject-${policy.automationKey}`} bind:value={templateDraft.subject} /></div><div><label for={`template-body-${policy.automationKey}`} class="mb-1 block text-sm font-medium">Message</label><textarea id={`template-body-${policy.automationKey}`} class="min-h-32 w-full rounded-md border border-input bg-background px-3 py-2 text-sm" bind:value={templateDraft.body}></textarea><p class="mt-1 text-xs text-muted-foreground">Merge fields such as <code>{'{{tenant_name}}'}</code> and <code>{'{{property_address}}'}</code> fill from the notice's Lease Management context.</p></div>{#if policy.classification === 'Legal'}<div class="grid gap-3 sm:grid-cols-2"><div><label for={`template-jurisdiction-${policy.automationKey}`} class="mb-1 block text-sm font-medium">Reviewed jurisdiction</label><Input id={`template-jurisdiction-${policy.automationKey}`} bind:value={templateDraft.jurisdictionCode} placeholder="State or local code" /></div><label class="flex min-h-14 items-center gap-3 self-end rounded-lg border border-border p-3"><Checkbox checked={templateDraft.confirmJurisdictionReviewed} onCheckedChange={(value) => (templateDraft.confirmJurisdictionReviewed = value === true)} /><span class="text-sm">I reviewed this template for that jurisdiction</span></label></div>{/if}<div class="flex flex-wrap gap-3"><Button onclick={() => saveTemplateMutation.mutate(policy.automationKey)} disabled={saveTemplateMutation.isPending || !templateDraft.subject.trim() || !templateDraft.body.trim() || (policy.classification === 'Legal' && (!templateDraft.jurisdictionCode.trim() || !templateDraft.confirmJurisdictionReviewed))}>{saveTemplateMutation.isPending ? 'Saving…' : 'Save and use new template version'}</Button><Button variant="outline" onclick={() => restoreTemplateMutation.mutate(policy.automationKey)} disabled={restoreTemplateMutation.isPending}>Restore and use current supplied default</Button></div></div>
							{/if}
						</Card.Content>
					</Card.Root>
				{/if}
			{/each}
		</div>

		<Card.Root class="gap-0 py-0" data-testid="tenant-notice-delivery-status">
			<Card.Content class="space-y-4 p-6">
				<div>
					<h2 class="text-lg font-semibold">Recent delivery status</h2>
					<p class="mt-1 text-sm text-muted-foreground">These statuses come from the durable delivery queue, including provider acceptance, retries, and terminal failures.</p>
				</div>
				{#if deliveriesQuery.isLoading}
					<p class="text-sm text-muted-foreground">Loading delivery status…</p>
				{:else if deliveriesQuery.isError}
					<div class="flex flex-wrap items-center gap-3"><p class="text-sm text-destructive">We couldn't load delivery status.</p><Button variant="outline" size="sm" onclick={() => deliveriesQuery.refetch()}>Retry</Button></div>
				{:else if (deliveriesQuery.data?.length ?? 0) === 0}
					<p class="rounded-md bg-muted/40 p-4 text-sm text-muted-foreground">No tenant notice has been queued yet.</p>
				{:else}
					<div class="space-y-3">
						{#each deliveriesQuery.data ?? [] as delivery (delivery.evidenceId)}
							<div class="grid gap-3 rounded-lg border border-border p-4 md:grid-cols-[minmax(0,1fr)_auto] md:items-start">
								<div>
									<div class="flex flex-wrap items-center gap-2"><p class="font-medium">{delivery.subject}</p><span class={`rounded-full border px-2 py-0.5 text-xs font-medium ${deliveryStatusClass(delivery.status)}`}>{delivery.status}</span></div>
									<p class="mt-1 text-xs text-muted-foreground">{deliveryStatusExplanation(delivery.status)}</p>
									<p class="mt-1 text-sm text-muted-foreground">{delivery.recipientRole} · {delivery.channel} · {delivery.destination}</p>
									{#if delivery.lastError}<p class="mt-2 text-sm text-destructive">{delivery.lastError}</p>{/if}
								</div>
								<div class="text-xs text-muted-foreground md:text-right">
									<p>Attempts: {delivery.attemptCount}</p>
									{#if delivery.provider}<p>{delivery.provider}{delivery.providerMessageId ? ` · ${delivery.providerMessageId}` : ''}</p>{/if}
									{#if delivery.nextAttemptAtUtc}<p>Next try: {new Date(delivery.nextAttemptAtUtc).toLocaleString()}</p>{/if}
								</div>
							</div>
						{/each}
					</div>
				{/if}
			</Card.Content>
		</Card.Root>
	{/if}
</div>
