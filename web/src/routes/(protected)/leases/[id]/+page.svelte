<script lang="ts">
	import { createQuery } from '@tanstack/svelte-query';
	import { page } from '$app/state';
	import { leaseManagements } from '$lib/api/endpoints/lease-managements';
	import PageHeader from '$lib/components/m3/PageHeader.svelte';
	import StatusBadge from '$lib/components/shared/StatusBadge.svelte';
	import { Button } from '$lib/components/ui/button';
	import { Card, CardContent, CardHeader, CardTitle } from '$lib/components/ui/card';
	import { FileDown, Home, Users } from '@lucide/svelte';
	import { showError } from '$lib/utils/toast';

	const leaseManagementId = $derived(Number(page.params.id));
	const relationshipQuery = createQuery(() => ({
		queryKey: ['lease-managements', leaseManagementId],
		queryFn: () => leaseManagements.get(leaseManagementId),
		enabled: Number.isInteger(leaseManagementId) && leaseManagementId > 0
	}));
	const agreementsQuery = createQuery(() => ({
		queryKey: ['lease-managements', leaseManagementId, 'agreements'],
		queryFn: () => leaseManagements.agreements(leaseManagementId, { take: 100, sort: '-versionNumber' }),
		enabled: Number.isInteger(leaseManagementId) && leaseManagementId > 0
	}));

	async function downloadAgreement(agreementId: number, artifactId: number, fileName: string) {
		try {
			const blob = await leaseManagements.downloadArtifact(leaseManagementId, agreementId, artifactId);
			const url = URL.createObjectURL(blob);
			const anchor = document.createElement('a');
			anchor.href = url;
			anchor.download = fileName;
			anchor.click();
			URL.revokeObjectURL(url);
		} catch (error) {
			showError(error instanceof Error ? error.message : 'Agreement download failed.');
		}
	}
</script>

{#if relationshipQuery.isLoading}
	<p class="text-muted-foreground">Loading tenant and lease relationship…</p>
{:else if relationshipQuery.error || !relationshipQuery.data}
	<div class="rounded-xl border border-destructive/30 bg-destructive/5 p-6">
		<h1 class="text-xl font-semibold">Tenant and lease relationship unavailable</h1>
		<p class="mt-2 text-sm text-muted-foreground">It may no longer exist or you may not have access.</p>
		<Button href="/leases" variant="outline" class="mt-4">Back to leases</Button>
	</div>
{:else}
	{@const detail = relationshipQuery.data}
	{@const summary = detail.summary}
	<div class="space-y-6">
		<PageHeader
			title={summary.primaryTenantName ?? 'Tenant & lease'}
			subtitle={`${summary.propertyName}${summary.unitNumber ? ` · ${summary.unitNumber}` : ''} · ${summary.relationshipNumber}`}
		>
			{#snippet actions()}
				<Button href={`/units/${summary.unitId}?tab=lease`} variant="outline" class="gap-2"><Home class="h-4 w-4" /> Open rental</Button>
			{/snippet}
		</PageHeader>

		<div class="grid gap-4 md:grid-cols-3">
			<Card><CardHeader><CardTitle class="text-base">Relationship</CardTitle></CardHeader><CardContent class="space-y-2"><StatusBadge status={summary.lifecycle} /><p class="text-sm text-muted-foreground">Possession and account lifecycle</p></CardContent></Card>
			<Card><CardHeader><CardTitle class="text-base">Governing agreement</CardTitle></CardHeader><CardContent><p class="font-medium">{summary.agreementNumber ?? 'No governing agreement'}</p><p class="text-sm text-muted-foreground">{summary.agreementStatus ?? 'Not issued'}{summary.termEndOn ? ` · ends ${summary.termEndOn}` : ''}</p></CardContent></Card>
			<Card><CardHeader><CardTitle class="text-base">Tenant account</CardTitle></CardHeader><CardContent><p class="font-medium">{summary.tenantAccountId ? `Account #${summary.tenantAccountId}` : 'Not opened'}</p><p class="text-sm text-muted-foreground">Continues across renewals and corrections</p></CardContent></Card>
		</div>

		<Card>
			<CardHeader><CardTitle class="flex items-center gap-2"><Users class="h-5 w-5" /> Household and responsibility</CardTitle></CardHeader>
			<CardContent>
				{#if detail.parties.length === 0}<p class="text-sm text-muted-foreground">No effective parties.</p>{:else}
					<div class="divide-y">
						{#each detail.parties as party}
							<div class="flex items-center justify-between gap-4 py-3"><div><p class="font-medium">{party.tenantName}</p><p class="text-sm text-muted-foreground">{party.email ?? party.phone ?? 'No contact information'}</p></div><StatusBadge status={party.role} /></div>
						{/each}
					</div>
				{/if}
			</CardContent>
		</Card>

		<Card>
			<CardHeader><CardTitle>Agreement history</CardTitle></CardHeader>
			<CardContent>
				{#if agreementsQuery.isLoading}<p class="text-sm text-muted-foreground">Loading agreement versions…</p>
				{:else if (agreementsQuery.data?.items.length ?? 0) === 0}<p class="text-sm text-muted-foreground">No agreement versions yet.</p>
				{:else}<div class="divide-y">
					{#each agreementsQuery.data?.items ?? [] as agreement}
						<div class="flex flex-col gap-3 py-4 sm:flex-row sm:items-center sm:justify-between"><div><div class="flex items-center gap-2"><p class="font-medium">{agreement.agreementNumber} · version {agreement.versionNumber}</p><StatusBadge status={agreement.agreementStatus} /></div><p class="text-sm text-muted-foreground">{agreement.changeType} · {agreement.termStartOn} to {agreement.termEndOn ?? 'month-to-month'} · ${agreement.baseRentAmount.toLocaleString()}/month</p></div>{@const artifact = agreement.executedArtifact ?? agreement.issuedArtifact}{#if artifact}<Button variant="outline" size="sm" class="gap-2" onclick={() => downloadAgreement(agreement.leaseAgreementId, artifact.legalDocumentArtifactId, artifact.fileName)}><FileDown class="h-4 w-4" /> {agreement.executedArtifact ? 'Executed PDF' : 'Issued PDF'}</Button>{/if}</div>
					{/each}
				</div>{/if}
			</CardContent>
		</Card>
	</div>
{/if}
