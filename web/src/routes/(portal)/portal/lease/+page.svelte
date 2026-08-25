<script lang="ts">
	import { createMutation, createQuery } from '@tanstack/svelte-query';
	import { portal } from '$lib/api/endpoints/portal';
	import { FileDown, FileText, MessageCircleQuestionMark } from '@lucide/svelte';
	import * as Card from '$lib/components/ui/card';
	import { Button } from '$lib/components/ui/button';
	import { Input } from '$lib/components/ui/input';
	import LoadingState from '$lib/components/shared/LoadingState.svelte';
	import { formatLeaseAnswerText } from '$lib/portal/lease-answer-display';
	import { formatDateOnly } from '$lib/utils/date';

	const leasesQuery = createQuery(() => ({ queryKey: ['portal-lease-page'], queryFn: () => portal.leases() }));

	// The lease the Q&A is grounded in. Portal leases come back newest-first, so the first one is
	// the tenant's most relevant lease — the same lease the server falls back to when no id is sent.
	const primaryLease = $derived(leasesQuery.data?.[0]);

	function money(value: number | string | null | undefined) {
		return Number(value ?? 0).toLocaleString(undefined, { style: 'currency', currency: 'USD' });
	}

	// --- "Ask This Lease" Q&A (grounded in the tenant's own lease) ---
	let leaseQuestion = $state('');
	let leaseAnswer = $state('');
	let leaseAnswerSources = $state<string[]>([]);
	let downloadingAgreementId = $state<number | null>(null);
	let leaseDocumentMessage = $state('');

	const askLeaseMutation = createMutation(() => ({
		mutationFn: (question: string) => portal.askLease(question, primaryLease?.leaseManagementId),
		onSuccess: (res) => {
			leaseAnswer = res.answer;
			leaseAnswerSources = res.sources ?? [];
		},
		onError: () => {
			leaseAnswer = 'Sorry, we could not answer that right now. Please try again or message your landlord.';
			leaseAnswerSources = [];
		}
	}));

	function askLease(question = leaseQuestion) {
		const q = question.trim();
		if (!q || askLeaseMutation.isPending) return;
		leaseQuestion = q;
		askLeaseMutation.mutate(q);
	}

	const SUGGESTIONS = [
		'Can I have a pet?',
		'When is rent due?',
		"What's the late fee?",
		'How much is my security deposit?'
	];

	function askSuggestion(q: string) {
		askLease(q);
	}

	async function downloadSignedLease(relationship: NonNullable<typeof primaryLease>) {
		const agreement = relationship.agreement;
		if (
			!agreement?.executedDocumentAvailable ||
			!agreement.executedDocumentFileName ||
			!agreement.executedDocumentContentType
		) {
			leaseDocumentMessage = 'Signed lease is not available yet. Contact management.';
			return;
		}

		downloadingAgreementId = agreement.leaseAgreementId;
		leaseDocumentMessage = '';
		try {
			await portal.downloadExecutedAgreement(
				relationship.leaseManagementId,
				agreement.leaseAgreementId,
				agreement.executedDocumentFileName,
				agreement.executedDocumentContentType
			);
		} catch {
			leaseDocumentMessage = 'Could not download the signed lease. Please try again or contact management.';
		} finally {
			downloadingAgreementId = null;
		}
	}
</script>

<svelte:head><title>Lease - Rental Command</title></svelte:head>

<div class="h-full overflow-y-auto p-6">
	<div class="mb-5 flex items-center gap-2"><FileText class="h-5 w-5 text-primary" /><h1 class="text-2xl font-semibold">Lease</h1></div>
	<div class="space-y-3">
		{#if leasesQuery.isLoading}
			<LoadingState label="Loading lease information" variant="page" testid="portal-lease-loading" />
		{:else if leasesQuery.isError}
			<div class="rounded-lg border border-destructive/40 p-6 text-center" data-testid="portal-lease-error">
				<p class="text-sm font-medium text-destructive">Couldn't load your lease information.</p>
				<Button type="button" variant="outline" size="sm" class="mt-3" onclick={() => leasesQuery.refetch()}>Try again</Button>
			</div>
		{:else}
			{#each leasesQuery.data ?? [] as relationship}
				<div class="rounded-lg border border-border bg-card p-4">
					<p class="font-medium">{relationship.agreement?.agreementNumber ?? relationship.relationshipNumber}</p>
					<p class="mt-1 text-sm text-muted-foreground">{relationship.propertyName} {relationship.unitNumber ? `Unit ${relationship.unitNumber}` : ''}</p>
					<p class="mt-3 text-sm">{relationship.agreement ? `Rent ${money(relationship.agreement.baseRentAmount)} · ${relationship.agreement.termEndOn ? `Ends ${formatDateOnly(relationship.agreement.termEndOn)}` : 'Month-to-month'}` : 'No lease in effect'}</p>
					{#if relationship.agreement?.executedDocumentAvailable}
						<Button
							type="button"
							variant="outline"
							size="sm"
							class="mt-4 gap-2"
							onclick={() => downloadSignedLease(relationship)}
							disabled={downloadingAgreementId === relationship.agreement?.leaseAgreementId}
							data-testid="portal-lease-download-signed-pdf"
						>
							<FileDown class="h-4 w-4" />
							{downloadingAgreementId === relationship.agreement.leaseAgreementId ? 'Downloading…' : 'Download signed lease'}
						</Button>
					{:else if relationship.agreement}
						<p class="mt-4 text-sm text-muted-foreground" data-testid="portal-lease-signed-pdf-missing">
							Signed lease is not available yet. Contact management.
						</p>
					{/if}
				</div>
			{:else}
				<p class="text-sm text-muted-foreground">No lease is linked to this account.</p>
			{/each}
		{/if}
		{#if leaseDocumentMessage}
			<p class="text-sm text-destructive" data-testid="portal-lease-document-message">{leaseDocumentMessage}</p>
		{/if}
	</div>

	{#if primaryLease}
		<!-- Ask This Lease: self-serve answers grounded in the tenant's own lease -->
		<Card.Root class="mt-6" data-testid="portal-lease-qa-card">
			<Card.Header>
				<Card.Title class="flex items-center gap-2 text-base">
					<MessageCircleQuestionMark class="h-5 w-5 text-primary" />
					Ask this lease
				</Card.Title>
				<Card.Description>
					Get quick answers about your lease — rent, dates, deposit, late fee, and more. Answers are
					based on your stored lease details.
				</Card.Description>
			</Card.Header>
			<Card.Content class="space-y-3">
				<div class="flex flex-col gap-2 sm:flex-row">
					<Input
						data-testid="portal-lease-question-input"
						bind:value={leaseQuestion}
						placeholder="Can I have a pet? When is rent due?"
						onkeydown={(e) => { if (e.key === 'Enter') askLease(); }}
					/>
					<Button
						data-testid="portal-lease-question-submit"
						onclick={() => askLease()}
						disabled={askLeaseMutation.isPending || !leaseQuestion.trim()}
					>
						{askLeaseMutation.isPending ? 'Answering…' : 'Ask'}
					</Button>
				</div>

				<div class="flex flex-wrap gap-2" data-testid="portal-lease-suggestions">
					{#each SUGGESTIONS as s}
						<button
							type="button"
							onclick={() => askSuggestion(s)}
							disabled={askLeaseMutation.isPending}
							class="rounded-full border border-border bg-muted/40 px-3 py-1 text-xs text-muted-foreground transition-colors hover:bg-muted hover:text-foreground disabled:opacity-50"
						>
							{s}
						</button>
					{/each}
				</div>

				{#if leaseAnswer}
					<div class="rounded-md border border-border bg-muted/30 p-3" data-testid="portal-lease-question-answer">
						<p class="text-sm leading-6">{formatLeaseAnswerText(leaseAnswer)}</p>
						{#if leaseAnswerSources.length > 0}
							<details class="mt-2 text-xs text-muted-foreground">
								<summary class="cursor-pointer">Lease facts used</summary>
								<ul class="mt-2 list-disc space-y-1 pl-5">
									{#each leaseAnswerSources as source}
										<li>{source}</li>
									{/each}
								</ul>
							</details>
						{/if}
					</div>
				{/if}

				<p class="text-xs text-muted-foreground">
					This is a quick reference, not legal advice. For anything not covered here, message your landlord.
				</p>
			</Card.Content>
		</Card.Root>
	{/if}
</div>
