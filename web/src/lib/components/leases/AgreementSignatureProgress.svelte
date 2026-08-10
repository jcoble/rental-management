<script lang="ts">
	import { createMutation, createQuery } from '@tanstack/svelte-query';
	import {
		leaseManagements,
		type LeaseAgreementSignatureProgressSigner,
		type SignatureRequestStatus
	} from '$lib/api/endpoints/lease-managements';
	import { apiErrorMessage, showError, showSuccess } from '$lib/utils/toast';
	import StatusBadge from '$lib/components/shared/StatusBadge.svelte';
	import { Button } from '$lib/components/ui/button';
	import { Loader2, RefreshCw, X } from '@lucide/svelte';

	let {
		leaseManagementId,
		leaseAgreementId,
		agreementNumber,
		onclose
	}: {
		leaseManagementId: number;
		leaseAgreementId: number;
		agreementNumber: string;
		onclose: () => void;
	} = $props();

	const progressQuery = createQuery(() => ({
		queryKey: [
			'lease-managements',
			leaseManagementId,
			'agreements',
			leaseAgreementId,
			'signature-progress'
		],
		queryFn: () =>
			leaseManagements.getAgreementSignatureProgress(leaseManagementId, leaseAgreementId)
	}));
	let resendingSignerId = $state<number | null>(null);

	const resendMutation = createMutation(() => ({
		mutationFn: (signer: LeaseAgreementSignatureProgressSigner) =>
			leaseManagements.resendAgreementInvitation(
				leaseManagementId,
				leaseAgreementId,
				signer.leaseAgreementSignerId,
				crypto.randomUUID()
			),
		onSuccess: async () => {
			showSuccess('Invitation queued again.');
			await progressQuery.refetch();
		},
		onError: (error) =>
			showError(apiErrorMessage(error, 'The invitation could not be queued again.')),
		onSettled: () => {
			resendingSignerId = null;
		}
	}));

	const packetStatusMap = {
		Prepared: { label: 'Preparing', class: 'border-border bg-muted text-muted-foreground' },
		Dispatching: { label: 'Sending', class: 'm3-tone-chip border m3-tone--info' },
		AwaitingSignatures: {
			label: 'Awaiting signatures',
			class: 'm3-tone-chip border m3-tone--info'
		},
		Viewed: { label: 'Viewed', class: 'm3-tone-chip border m3-tone--primary' },
		PartiallySigned: {
			label: 'Partially signed',
			class: 'm3-tone-chip border m3-tone--warning'
		},
		ExecutionPending: {
			label: 'Finalizing document',
			class: 'm3-tone-chip border m3-tone--warning'
		},
		Completed: { label: 'Completed', class: 'm3-tone-chip border m3-tone--success' },
		Declined: { label: 'Declined', class: 'm3-tone-chip border m3-tone--error' },
		Voided: { label: 'Voided', class: 'border-border bg-muted text-muted-foreground' },
		DeliveryFailed: {
			label: 'Delivery failed',
			class: 'm3-tone-chip border m3-tone--error'
		}
	};

	function formatDateTime(value: string | null): string {
		if (!value) return 'Not yet';
		const date = new Date(value);
		return Number.isNaN(date.getTime()) ? 'Recorded' : date.toLocaleString();
	}

	function canResendInvitation(
		packetStatus: SignatureRequestStatus,
		signer: LeaseAgreementSignatureProgressSigner
	): boolean {
		return (
			signer.status !== 'Signed' &&
			signer.status !== 'Declined' &&
			['AwaitingSignatures', 'Viewed', 'PartiallySigned', 'DeliveryFailed'].includes(packetStatus)
		);
	}

	function confirmResend(signer: LeaseAgreementSignatureProgressSigner) {
		const confirmed = globalThis.confirm(
			`Resend the signing invitation to ${signer.nameSnapshot} at ${signer.emailSnapshot}? This queues the same secure signing link again.`
		);
		if (!confirmed) return;
		resendingSignerId = signer.leaseAgreementSignerId;
		resendMutation.mutate(signer);
	}

	function signerMilestones(signer: LeaseAgreementSignatureProgressSigner) {
		return [
			{ label: 'Queued', at: signer.deliveryQueuedAtUtc, tone: 'complete' },
			{ label: 'Viewed', at: signer.viewedAtUtc, tone: signer.viewedAtUtc ? 'complete' : 'pending' },
			{
				label: 'Consent',
				at: signer.consentGivenAtUtc,
				tone: signer.consentGivenAtUtc ? 'complete' : 'pending'
			},
			{ label: 'Signed', at: signer.signedAtUtc, tone: signer.signedAtUtc ? 'complete' : 'pending' },
			{
				label: 'Declined',
				at: signer.declinedAtUtc,
				tone: signer.declinedAtUtc ? 'declined' : 'pending'
			}
		] as const;
	}
</script>

<section class="rounded-xl border bg-background p-4" data-testid="agreement-signature-progress">
	<div class="flex items-start justify-between gap-4">
		<div>
			<h3 class="font-medium">Signature progress · {agreementNumber}</h3>
			<p class="text-xs text-muted-foreground">Loaded only for this selected lease version.</p>
		</div>
		<Button variant="ghost" size="icon" aria-label="Close signature progress" onclick={onclose}>
			<X class="h-4 w-4" />
		</Button>
	</div>

	{#if progressQuery.isLoading}
		<div class="flex items-center gap-2 py-6 text-sm text-muted-foreground">
			<Loader2 class="h-4 w-4 animate-spin" /> Loading signature progress…
		</div>
	{:else if progressQuery.isError || !progressQuery.data}
		<div class="mt-4 rounded-lg border border-destructive/30 bg-destructive/5 p-4 text-sm">
			<p class="font-medium">Signature progress is unavailable</p>
			<p class="mt-1 text-muted-foreground">
				The packet may have changed, or you may no longer have access. No signing-link details were loaded.
			</p>
			<Button variant="outline" size="sm" class="mt-3 gap-2" onclick={() => progressQuery.refetch()}>
				<RefreshCw class="h-4 w-4" /> Try again
			</Button>
		</div>
	{:else}
		{@const progress = progressQuery.data}
		<div class="mt-4 space-y-4">
			<div class="grid gap-3 sm:grid-cols-2 lg:grid-cols-4">
				<div class="rounded-lg border p-3">
					<p class="text-xs text-muted-foreground">Packet status</p>
					<div class="mt-2"><StatusBadge status={progress.status} map={packetStatusMap} /></div>
				</div>
				<div class="rounded-lg border p-3">
					<p class="text-xs text-muted-foreground">Signatures</p>
					<p class="mt-1 text-lg font-semibold">{progress.signedSignerCount} of {progress.requiredSignerCount} required</p>
					<p class="text-xs text-muted-foreground">{progress.totalSignerCount} signer{progress.totalSignerCount === 1 ? '' : 's'} total</p>
				</div>
				<div class="rounded-lg border p-3">
					<p class="text-xs text-muted-foreground">Issued artifact</p>
					<p class="mt-1 font-medium">{progress.issuedArtifactReady ? 'Ready' : 'Not available'}</p>
					<p class="text-xs text-muted-foreground">Original signature packet PDF</p>
				</div>
				<div class="rounded-lg border p-3">
					<p class="text-xs text-muted-foreground">Executed artifact</p>
					<p class="mt-1 font-medium">{progress.executedArtifactReady ? 'Ready' : 'Not ready'}</p>
					<p class="text-xs text-muted-foreground">Created after required signing completes</p>
				</div>
			</div>

			{#if progress.status === 'DeliveryFailed' || progress.failureCode}
				<div class="rounded-lg border border-destructive/30 bg-destructive/5 p-4 text-sm" data-testid="delivery-failed-resend-actions">
					<p class="font-medium">Signature delivery needs attention</p>
					<p class="mt-1 text-muted-foreground">
						Automatic delivery could not complete. Choose an unsigned signer to queue the same secure invitation again.
					</p>
					{#if progress.failureCode}<p class="mt-1 text-xs text-muted-foreground">Reference: {progress.failureCode}</p>{/if}
					{#if progress.status === 'DeliveryFailed'}
						<div class="mt-3 flex flex-wrap gap-2">
							{#each progress.signers.filter((signer) => canResendInvitation(progress.status, signer)) as signer (signer.leaseAgreementSignerId)}
								<Button
									size="sm"
									class="gap-2"
									disabled={resendMutation.isPending}
									onclick={() => confirmResend(signer)}
								>
									{#if resendingSignerId === signer.leaseAgreementSignerId}<Loader2 class="h-4 w-4 animate-spin" />{/if}
									Resend invitation to {signer.nameSnapshot}
								</Button>
							{/each}
						</div>
					{/if}
				</div>
			{/if}

			<div class="space-y-3">
				<div>
					<h4 class="font-medium">Signer timeline</h4>
					<p class="text-xs text-muted-foreground">Ordered by the packet’s required signing sequence.</p>
				</div>
				{#each progress.signers as signer (signer.leaseAgreementSignerId)}
					<div class="rounded-lg border p-4" data-testid="agreement-signature-signer">
						<div class="flex flex-wrap items-start justify-between gap-3">
							<div>
								<p class="font-medium">{signer.signingOrder}. {signer.nameSnapshot}</p>
								<p class="text-xs text-muted-foreground">{signer.emailSnapshot} · {signer.signerRole}{signer.isRequired ? ' · Required' : ' · Optional'}</p>
							</div>
							<div class="flex items-center gap-2">
								<StatusBadge status={signer.status} />
								{#if canResendInvitation(progress.status, signer)}
									<Button
										variant="outline"
										size="sm"
										class="gap-2"
										disabled={resendMutation.isPending}
										onclick={() => confirmResend(signer)}
									>
										{#if resendingSignerId === signer.leaseAgreementSignerId}<Loader2 class="h-4 w-4 animate-spin" />{/if}
										Resend invitation
									</Button>
								{/if}
							</div>
						</div>
						<ol class="mt-4 grid gap-2 sm:grid-cols-5">
							{#each signerMilestones(signer) as milestone}
								<li class="rounded-lg border p-2 text-xs" data-state={milestone.tone}>
									<div class="flex items-center gap-2">
										<span class={`h-2 w-2 rounded-full ${milestone.tone === 'complete' ? 'bg-primary' : milestone.tone === 'declined' ? 'bg-destructive' : 'bg-muted-foreground/30'}`}></span>
										<span class="font-medium">{milestone.label}</span>
									</div>
									<p class="mt-1 text-muted-foreground">{formatDateTime(milestone.at)}</p>
								</li>
							{/each}
						</ol>
					</div>
				{/each}
			</div>
		</div>
	{/if}
</section>
