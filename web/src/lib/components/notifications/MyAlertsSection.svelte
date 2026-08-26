<script lang="ts">
	import { beforeNavigate } from '$app/navigation';
	import { createMutation, createQuery, useQueryClient } from '@tanstack/svelte-query';
	import { notifications } from '$lib/api/endpoints/notifications';
	import { showError, showSuccess, apiErrorMessage } from '$lib/utils/toast';
	import { Button } from '$lib/components/ui/button';
	import { Checkbox } from '$lib/components/ui/checkbox';
	import * as Card from '$lib/components/ui/card';
	import NotificationHelpAction from '$lib/components/notifications/NotificationHelpAction.svelte';
	import LoadingState from '$lib/components/shared/LoadingState.svelte';

	const queryClient = useQueryClient();
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

	beforeNavigate(({ cancel }) => {
		if (!hasUnsavedChanges) return;
		if (!window.confirm('You have unsaved notification changes. Leave this page without saving them?')) {
			cancel();
		}
	});
</script>

<section id="my-alerts" class="scroll-mt-6 space-y-4" data-testid="notifications-my-alerts">
	<div class="flex flex-wrap items-start justify-between gap-4">
		<div class="max-w-3xl">
			<h2 class="text-lg font-semibold tracking-tight">My alerts</h2>
			<p class="mt-1 text-sm leading-6 text-muted-foreground">
				Choose where you personally receive alerts. This does not change what your team or tenants receive.
			</p>
		</div>
		<NotificationHelpAction
			title="How personal alerts work"
			description="These destinations belong only to your signed-in account."
			guidance="Choose where you personally receive Rental Command alerts. Team responsibilities and tenant messages are set further down this page and are never changed here."
			href="/docs/settings-and-notifications"
			linkLabel="Open notification documentation"
		/>
	</div>

	<div class="rounded-lg border border-border bg-muted/30 px-4 py-3" aria-live="polite">
		<p class="text-xs font-medium uppercase tracking-wide text-muted-foreground">Saved summary</p>
		<p class="mt-1 text-sm text-foreground">{savedSummary}</p>
	</div>

	<div class="space-y-5" data-testid="my-alerts-page">
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
				</div>
			</Card.Content>
		</Card.Root>
	{/if}
	</div>
</section>
