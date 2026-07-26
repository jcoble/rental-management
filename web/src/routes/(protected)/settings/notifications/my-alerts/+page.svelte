<script lang="ts">
	import { createMutation, createQuery, useQueryClient } from '@tanstack/svelte-query';
	import { notifications } from '$lib/api/endpoints/notifications';
	import { getAuthState, hasCapability } from '$lib/stores/auth.svelte';
	import { canAccessPathForEnvelope, safeLandingForAccess } from '$lib/auth/experience-policy';
	import { showError, showSuccess, apiErrorMessage } from '$lib/utils/toast';
	import { Button } from '$lib/components/ui/button';
	import { Checkbox } from '$lib/components/ui/checkbox';
	import * as Card from '$lib/components/ui/card';
	import NotificationSetupJourney from '$lib/components/notifications/NotificationSetupJourney.svelte';
	import LoadingState from '$lib/components/shared/LoadingState.svelte';

	const queryClient = useQueryClient();
	const authState = getAuthState();
	const canManageTeamNotifications = $derived(hasCapability('notifications.manage'));
	const returnHref = $derived.by(() => {
		const access = authState.accessEnvelope;
		if (!access) return '/';
		return canAccessPathForEnvelope(access, '/settings')
			? '/settings#notifications'
			: (safeLandingForAccess(access) ?? '/');
	});
	const alertsQuery = createQuery(() => ({
		queryKey: ['notification-settings', 'my-alerts'],
		queryFn: () => notifications.myAlerts.get()
	}));

	let form = $state({ enableInApp: true, enableMobilePush: true, enableEmail: true, enableSms: false });
	let appliedUserId = $state<number | null>(null);

	$effect(() => {
		const data = alertsQuery.data;
		if (!data || data.userId === appliedUserId) return;
		form = {
			enableInApp: data.enableInApp,
			enableMobilePush: data.enableMobilePush,
			enableEmail: data.enableEmail,
			enableSms: data.enableSms
		};
		appliedUserId = data.userId;
	});

	const saveMutation = createMutation(() => ({
		mutationFn: () => notifications.myAlerts.update(form),
		onSuccess: (saved) => {
			queryClient.setQueryData(['notification-settings', 'my-alerts'], saved);
			showSuccess('Your alert choices were saved.');
		},
		onError: (error) => showError(apiErrorMessage(error))
	}));

	const channels = [
		{ key: 'enableInApp', label: 'In Rental Command', detail: 'See alerts in the bell and notification list.' },
		{ key: 'enableMobilePush', label: 'Phone app', detail: 'Receive alerts on phones signed in to your account.' },
		{ key: 'enableEmail', label: 'Email', detail: 'Send alerts to the account email shown below.' },
		{ key: 'enableSms', label: 'Text message', detail: 'Send alerts to the mobile number shown below.' }
	] as const;

	const hasUnsavedChanges = $derived.by(() => {
		const saved = alertsQuery.data;
		if (!saved) return false;
		return form.enableInApp !== saved.enableInApp ||
			form.enableMobilePush !== saved.enableMobilePush ||
			form.enableEmail !== saved.enableEmail ||
			form.enableSms !== saved.enableSms;
	});

	const savedSummary = $derived.by(() => {
		const saved = alertsQuery.data;
		if (!saved) return 'Your saved alert destinations will appear after this step loads.';
		const enabled: string[] = [];
		if (saved.enableInApp) enabled.push('Rental Command');
		if (saved.enableMobilePush) enabled.push('phone app');
		if (saved.enableEmail) enabled.push('email');
		if (saved.enableSms) enabled.push('text message');
		return enabled.length > 0
			? `${saved.displayName} receives personal alerts in ${enabled.join(', ')}.`
			: `${saved.displayName} has paused every personal alert destination.`;
	});
</script>

<svelte:head><title>Your alerts · Rental Command</title></svelte:head>

<NotificationSetupJourney
	currentStep={1}
	description="Choose where you personally receive alerts. This does not change what your team or tenants receive."
	{savedSummary}
	{returnHref}
	canManage={canManageTeamNotifications}
	{hasUnsavedChanges}
	helpTitle="How personal alerts work"
	helpDescription="These destinations belong only to your signed-in account."
	helpGuidance="Choose where you personally receive Rental Command alerts. Team responsibilities and tenant messages have their own steps and are never changed here."
>
<div data-testid="my-alerts-page">
	{#if alertsQuery.isLoading}
		<LoadingState label="Loading your alert destinations" testid="my-alerts-loading" />
	{:else if alertsQuery.isError || !alertsQuery.data}
		<Card.Root><Card.Content class="space-y-4 p-6"><p class="text-sm text-destructive">We couldn't load your alert preferences.</p><Button variant="outline" onclick={() => alertsQuery.refetch()}>Retry</Button></Card.Content></Card.Root>
	{:else}
		<Card.Root class="gap-0 py-0">
			<Card.Content class="space-y-5 p-6">
				<div class="rounded-lg border border-border bg-muted/30 p-4">
					<p class="font-medium">Alert destinations for {alertsQuery.data.displayName}</p>
					<p class="mt-1 text-sm text-muted-foreground">Email: {alertsQuery.data.email || 'No email on this account'}</p>
					<p class="text-sm text-muted-foreground">Mobile number: {alertsQuery.data.phoneNumber || 'No mobile number on this account'}</p>
					{#if !alertsQuery.data.phoneNumber}
						<a href="/profile" class="mt-2 inline-flex min-h-11 items-center text-sm font-medium text-primary hover:underline">
							Add a mobile number in Profile
						</a>
					{/if}
				</div>

				<div class="grid gap-3 sm:grid-cols-2">
					{#each channels as channel (channel.key)}
						<label class="flex min-h-24 items-start gap-3 rounded-lg border border-border p-4">
							<Checkbox checked={form[channel.key]} onCheckedChange={(value) => (form[channel.key] = value === true)} aria-label={channel.label} />
							<span>
								<span class="block font-medium">{channel.label}</span>
								<span class="mt-1 block text-sm text-muted-foreground">{channel.detail}</span>
							</span>
						</label>
					{/each}
				</div>

				<div class="flex items-center gap-3">
					<Button onclick={() => saveMutation.mutate()} disabled={saveMutation.isPending} data-testid="my-alerts-save">
						{saveMutation.isPending ? 'Saving…' : 'Save my alert choices'}
					</Button>
					<p class="text-xs text-muted-foreground" aria-live="polite">Next, choose who handles each kind of work.</p>
				</div>
			</Card.Content>
		</Card.Root>
	{/if}
</div>
</NotificationSetupJourney>
