<script lang="ts">
	import { createMutation, createQuery, useQueryClient } from '@tanstack/svelte-query';
	import { notifications } from '$lib/api/endpoints/notifications';
	import { getAuthState } from '$lib/stores/auth.svelte';
	import { canAccessPathForEnvelope, safeLandingForAccess } from '$lib/auth/experience-policy';
	import { showError, showSuccess, apiErrorMessage } from '$lib/utils/toast';
	import { Button } from '$lib/components/ui/button';
	import { Checkbox } from '$lib/components/ui/checkbox';
	import * as Card from '$lib/components/ui/card';
	import NotificationHelpAction from '$lib/components/notifications/NotificationHelpAction.svelte';
	import LoadingState from '$lib/components/shared/LoadingState.svelte';
	import { ArrowLeft } from '@lucide/svelte';

	const queryClient = useQueryClient();
	const authState = getAuthState();
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
			showSuccess('Your alert preferences were saved.');
		},
		onError: (error) => showError(apiErrorMessage(error))
	}));

	const channels = [
		{ key: 'enableInApp', label: 'In-app', detail: 'Shows in your Rental Command bell and inbox.' },
		{ key: 'enableMobilePush', label: 'Mobile push', detail: 'Goes to mobile devices registered to your account. This is not browser web push.' },
		{ key: 'enableEmail', label: 'Email', detail: 'Goes to the signed-in account email shown below.' },
		{ key: 'enableSms', label: 'SMS', detail: 'Goes to the signed-in account phone number shown below when one is available.' }
	] as const;
</script>

<svelte:head><title>My alerts · Rental Command</title></svelte:head>

<div class="mx-auto max-w-4xl space-y-6" data-testid="my-alerts-page">
	<div>
		<a href={returnHref} class="mb-3 inline-flex min-h-11 items-center gap-2 text-sm text-muted-foreground hover:text-foreground">
			<ArrowLeft class="size-4" /> Back
		</a>
		<div class="flex flex-wrap items-start justify-between gap-4">
			<div>
				<h1 class="text-2xl font-semibold tracking-tight">My alerts</h1>
				<p class="mt-1 text-sm text-muted-foreground">These choices apply only to your signed-in account, not your team or tenants.</p>
			</div>
				<NotificationHelpAction
					title="How My alerts works"
					description="These destinations belong only to the signed-in account."
					guidance="Choose where you personally receive Rental Command alerts. Team responsibilities and tenant notice delivery use their own settings and are never changed here."
				/>
		</div>
	</div>

	{#if alertsQuery.isLoading}
		<LoadingState label="Loading your alert destinations" testid="my-alerts-loading" />
	{:else if alertsQuery.isError || !alertsQuery.data}
		<Card.Root><Card.Content class="space-y-4 p-6"><p class="text-sm text-destructive">We couldn't load your alert preferences.</p><Button variant="outline" onclick={() => alertsQuery.refetch()}>Retry</Button></Card.Content></Card.Root>
	{:else}
		<Card.Root class="gap-0 py-0">
			<Card.Content class="space-y-5 p-6">
				<div class="rounded-lg border border-border bg-muted/30 p-4">
					<p class="font-medium">Configuring alerts for {alertsQuery.data.displayName}</p>
					<p class="mt-1 text-sm text-muted-foreground">Email: {alertsQuery.data.email || 'No email on this account'}</p>
					<p class="text-sm text-muted-foreground">SMS: {alertsQuery.data.phoneNumber || 'No phone number on this account'}</p>
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
						{saveMutation.isPending ? 'Saving…' : 'Save my alerts'}
					</Button>
					<p class="text-xs text-muted-foreground" aria-live="polite">Team routing and tenant delivery are configured separately.</p>
				</div>
			</Card.Content>
		</Card.Root>
	{/if}
</div>
