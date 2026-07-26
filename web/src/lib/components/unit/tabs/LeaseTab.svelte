<script lang="ts">
	import { untrack } from 'svelte';
	import { createMutation, createQuery, useQueryClient } from '@tanstack/svelte-query';
	import {
		leaseManagements,
		type CancelPlannedAccessDisposition,
		type LeaseManagementCurrentPartiesContext,
	} from '$lib/api/endpoints/lease-managements';
	import { documentTemplates } from '$lib/api/endpoints/document-templates';
	import { units } from '$lib/api/endpoints/units';
	import { hasCapability } from '$lib/stores/auth.svelte';
	import type { LeaseManagementSummary, UnitDashboard } from '$lib/types';
	import { apiErrorMessage, showError, showSuccess } from '$lib/utils/toast';
	import DetailCard from '$lib/components/shared/DetailCard.svelte';
	import LoadingState from '$lib/components/shared/LoadingState.svelte';
	import RemoteRecordSelect from '$lib/components/shared/RemoteRecordSelect.svelte';
	import StatusBadge from '$lib/components/shared/StatusBadge.svelte';
	import DatePicker from '$lib/components/shared/DatePicker.svelte';
	import DateTimePicker from '$lib/components/shared/DateTimePicker.svelte';
	import * as Dialog from '$lib/components/ui/dialog';
	import * as Select from '$lib/components/ui/select';
	import { Button } from '$lib/components/ui/button';
	import { ArrowRight, FileText, ScanLine, Users } from '@lucide/svelte';

	let { dashboard, onScan }: { dashboard: UnitDashboard; onScan: () => void } = $props();
	const queryClient = useQueryClient();
	const canManageLifecycle = $derived(hasCapability('leasing.onboarding.manage'));
	let selected = $state<LeaseManagementSummary | null>(null);
	let cancelOpen = $state(false);
	let transferOpen = $state(false);
	let closeOpen = $state(false);
	let cancellationReasonCode = $state('');
	let draftCancellationReason = $state('');
	let cancellationNote = $state('');
	let accessDispositions = $state<Record<number, CancelPlannedAccessDisposition>>({});
	let destinationUnitValue = $state('');
	let destinationTemplateId = $state(0);
	let effectiveOn = $state(new Date().toISOString().slice(0, 10));
	let giveDestinationPossessionNow = $state(false);
	let plannedDestinationPossessionAtUtc = $state('');
	let possessionAgreementExceptionReason = $state('');
	let carryTenantBalance = $state(true);
	let carrySecurityDeposit = $state(true);
	let transferReason = $state('');
	let closeReasonCode = $state('');
	let closeNote = $state('');

	const relationshipsQuery = createQuery(() => ({
		queryKey: ['lease-managements', 'unit', dashboard.unit.id],
		queryFn: () => leaseManagements.listPage({ unitId: dashboard.unit.id, take: 50, sort: '-updatedAtUtc' }),
		retry: false,
	}));
	const cancelContextQuery = createQuery<LeaseManagementCurrentPartiesContext>(() => ({
		queryKey: ['lease-management', selected?.leaseManagementId, 'cancel-context'],
		queryFn: () => leaseManagements.getCurrentPartiesContext(selected!.leaseManagementId),
		enabled: cancelOpen && Boolean(selected),
	}));
	const destinationTemplatesQuery = createQuery(() => ({
		queryKey: ['document-templates', 'lease-transfer'],
		queryFn: () => documentTemplates.listPage({ kind: 'Lease', status: 'Active', sort: 'name', take: 100 }),
		enabled: transferOpen,
	}));

	$effect(() => {
		const accesses = cancelContextQuery.data?.activeTenantUserAccesses ?? [];
		const current = untrack(() => accessDispositions);
		const next = { ...current };
		let changed = false;
		for (const access of accesses) {
			if (next[access.tenantUserAccessId]) continue;
			next[access.tenantUserAccessId] = 'RevokeNow';
			changed = true;
		}
		if (changed) accessDispositions = next;
	});

	async function refresh() {
		await queryClient.invalidateQueries({ queryKey: ['lease-managements'] });
		await queryClient.invalidateQueries({ queryKey: ['unit-dashboard', dashboard.unit.id] });
	}

	async function loadDestinationUnits(params: { search?: string; skip: number; take: number }) {
		const page = await units.listPage({
			...params,
			sort: 'unitNumber',
			availableForLease: true,
			excludeUnitId: dashboard.unit.id,
		});
		return {
			items: page.items.map((unit) => ({
				id: unit.id,
				label: `Unit ${unit.unitNumber}`,
				description: unit.floorPlan,
			})),
			totalCount: page.totalCount,
			skip: page.skip,
			take: page.take,
		};
	}

	const cancelMutation = createMutation(() => ({
		mutationFn: () => leaseManagements.cancelPlannedRelationship(selected!.leaseManagementId, {
			unitId: dashboard.unit.id,
			cancellationReasonCode: cancellationReasonCode.trim(),
			draftCancellationReason: draftCancellationReason.trim(),
			cancellationNote: cancellationNote.trim() || null,
			accesses: (cancelContextQuery.data?.activeTenantUserAccesses ?? []).map((access) => ({
				tenantUserAccessId: access.tenantUserAccessId,
				disposition: accessDispositions[access.tenantUserAccessId],
			})),
		}, crypto.randomUUID()),
		onSuccess: async () => { cancelOpen = false; await refresh(); showSuccess('Planned relationship canceled.'); },
		onError: (error) => showError(apiErrorMessage(error, 'Could not cancel the planned relationship.')),
	}));

	const transferMutation = createMutation(() => ({
		mutationFn: () => leaseManagements.transferToUnit(selected!.leaseManagementId, {
			sourceUnitId: dashboard.unit.id,
			destinationUnitId: Number(destinationUnitValue),
			effectiveOn,
			plannedDestinationPossessionAtUtc: plannedDestinationPossessionAtUtc ? new Date(plannedDestinationPossessionAtUtc).toISOString() : null,
			giveDestinationPossessionNow,
			possessionAgreementExceptionReason: giveDestinationPossessionNow ? possessionAgreementExceptionReason.trim() : null,
			destinationDocumentTemplateId: destinationTemplateId,
			carryTenantBalance,
			carrySecurityDeposit,
			transferReason: transferReason.trim(),
		}, crypto.randomUUID()),
		onSuccess: async () => { transferOpen = false; await refresh(); showSuccess('Relationship transferred.'); },
		onError: (error) => showError(apiErrorMessage(error, 'Could not transfer the relationship.')),
	}));

	const closeMutation = createMutation(() => ({
		mutationFn: () => leaseManagements.closeAccount(selected!.leaseManagementId, {
			tenantAccountId: selected!.tenantAccountId!,
			closeReasonCode: closeReasonCode.trim(),
			closeNote: closeNote.trim() || null,
		}, crypto.randomUUID()),
		onSuccess: async () => { closeOpen = false; await refresh(); showSuccess('Tenant account closed.'); },
		onError: (error) => showError(apiErrorMessage(error, 'The account cannot close until possession, money, drafts, signatures, payments, and autopay are resolved.')),
	}));

	function choose(relationship: LeaseManagementSummary, action: 'cancel' | 'transfer' | 'close') {
		selected = relationship;
		if (action === 'cancel') cancelOpen = true;
		if (action === 'transfer') transferOpen = true;
		if (action === 'close') closeOpen = true;
	}
</script>

<div class="space-y-4" data-testid="unit-lease-tab">
	<div class="flex flex-wrap items-center justify-between gap-3">
		<div><h2 class="text-lg font-semibold">Tenant & lease</h2><p class="text-sm text-muted-foreground">See who lives here, review the lease, and manage move-in or move-out.</p></div>
		<div class="flex gap-2"><Button variant="outline" class="gap-2" onclick={onScan}><ScanLine class="h-4 w-4" /> Import agreement</Button><Button href="/applications" class="gap-2"><Users class="h-4 w-4" /> Prepare move-in</Button></div>
	</div>

	{#if relationshipsQuery.isLoading}
		<LoadingState label="Loading tenant relationships" testid="unit-tenant-relationships-loading" />
	{:else if relationshipsQuery.isError}
		<div class="rounded-xl border border-destructive/40 bg-destructive/5 p-4" role="alert" data-testid="unit-tenant-relationships-error">
			<p class="text-sm font-medium text-destructive">Tenant relationships could not be loaded.</p>
			<p class="mt-1 text-sm text-muted-foreground">The service may be temporarily unavailable. Try again.</p>
			<Button class="mt-3" variant="outline" size="sm" onclick={() => relationshipsQuery.refetch()}>Try again</Button>
		</div>
	{:else if (relationshipsQuery.data?.items.length ?? 0) === 0}
		<DetailCard title="No tenant relationship yet" icon={FileText}><p class="text-sm text-muted-foreground">Prepare a move-in from an approved application, or scan an existing signed agreement.</p></DetailCard>
	{:else}
		<div class="divide-y overflow-hidden rounded-2xl bg-card">
			{#each relationshipsQuery.data?.items ?? [] as relationship}
				<div class="p-4">
					<a href={`/units/${dashboard.unit.id}?tab=tenant-lease&view=agreements&leaseManagement=${relationship.leaseManagementId}`} class="flex items-center justify-between gap-4 transition-colors hover:bg-muted/40">
						<div class="min-w-0"><div class="flex flex-wrap items-center gap-2"><p class="font-medium">{relationship.primaryTenantName ?? 'No primary tenant'}</p><StatusBadge status={relationship.lifecycle} /></div><p class="mt-1 text-sm text-muted-foreground">{relationship.agreementNumber ?? 'No governing agreement'}{relationship.agreementStatus ? ` · ${relationship.agreementStatus}` : ''}{relationship.termEndOn ? ` · ends ${relationship.termEndOn}` : ''}</p></div><ArrowRight class="h-4 w-4 shrink-0 text-muted-foreground" />
					</a>
					{#if canManageLifecycle}
						<div class="mt-3 flex flex-wrap gap-2" data-testid="unit-lifecycle-actions-{relationship.leaseManagementId}">
							{#if relationship.lifecycle === 'Planned' && !relationship.possessionGivenAtUtc}
								<Button size="sm" variant="destructive" onclick={() => choose(relationship, 'cancel')}>Cancel planned move-in</Button>
							{/if}
							{#if relationship.possessionGivenAtUtc && !relationship.possessionReturnedAtUtc}
								<Button size="sm" variant="outline" onclick={() => choose(relationship, 'transfer')}>Move to another rental</Button>
							{/if}
							{#if relationship.tenantAccountId && !relationship.accountClosedAtUtc}
								<Button size="sm" variant="outline" onclick={() => choose(relationship, 'close')}>Close account</Button>
							{/if}
						</div>
					{/if}
				</div>
			{/each}
		</div>
	{/if}
</div>

<Dialog.Root open={cancelOpen} onOpenChange={(open) => (cancelOpen = open)}>
	<Dialog.Content class="max-h-[90vh] max-w-xl overflow-y-auto" data-testid="cancel-planned-dialog">
		<Dialog.Header><Dialog.Title>Cancel planned move-in</Dialog.Title><Dialog.Description>Explain why the move-in was canceled and choose whether each tenant can still sign in to view old records.</Dialog.Description></Dialog.Header>
		<label class="block space-y-1 text-sm"><span>Reason category</span><input bind:value={cancellationReasonCode} class="m3-field-surface h-10 w-full px-3" placeholder="For example: applicant withdrew" /></label>
		<label class="block space-y-1 text-sm"><span>What happened?</span><textarea bind:value={draftCancellationReason} class="m3-field-surface min-h-20 w-full p-3"></textarea></label>
		<label class="block space-y-1 text-sm"><span>Optional note</span><textarea bind:value={cancellationNote} class="min-h-20 w-full rounded-md border p-3"></textarea></label>
		{#if cancelContextQuery.isLoading}
			<LoadingState label="Loading tenant access" variant="spinner" testid="cancel-relationship-context-loading" />
		{:else if cancelContextQuery.isError}
			<div class="rounded-xl border border-destructive/40 bg-destructive/5 p-4" role="alert">
				<p class="text-sm font-medium text-destructive">Tenant access could not be loaded.</p>
				<Button class="mt-3" variant="outline" size="sm" onclick={() => cancelContextQuery.refetch()}>Try again</Button>
			</div>
		{:else}
			{#each cancelContextQuery.data?.activeTenantUserAccesses ?? [] as access}
				<label class="block space-y-1 text-sm">
					<span>{access.tenantName} · {access.userEmail}</span>
					<Select.Root
						type="single"
						value={accessDispositions[access.tenantUserAccessId]}
						onValueChange={(value) => {
							if (!value) return;
							accessDispositions = {
								...accessDispositions,
								[access.tenantUserAccessId]: value as CancelPlannedAccessDisposition
							};
						}}
					>
						<Select.Trigger class="w-full">
							{accessDispositions[access.tenantUserAccessId] === 'Retain'
								? 'Keep access to past records'
								: 'Remove access now'}
						</Select.Trigger>
						<Select.Content>
							<Select.Item value="RevokeNow" label="Remove access now">Remove access now</Select.Item>
							<Select.Item value="Retain" label="Keep access to past records">Keep access to past records</Select.Item>
						</Select.Content>
					</Select.Root>
				</label>
			{/each}
		{/if}
		<Dialog.Footer><Button variant="outline" onclick={() => (cancelOpen = false)}>Keep relationship</Button><Button variant="destructive" disabled={!cancellationReasonCode.trim() || !draftCancellationReason.trim() || cancelContextQuery.isLoading || cancelContextQuery.isError || cancelMutation.isPending} onclick={() => cancelMutation.mutate()}>Cancel relationship</Button></Dialog.Footer>
	</Dialog.Content>
</Dialog.Root>

<Dialog.Root open={transferOpen} onOpenChange={(open) => (transferOpen = open)}>
	<Dialog.Content class="max-h-[90vh] max-w-xl overflow-y-auto" data-testid="transfer-unit-dialog">
		<Dialog.Header><Dialog.Title>Move tenants to another rental</Dialog.Title><Dialog.Description>Choose the new rental and effective date. Rental Command will close this occupancy and create the new one together.</Dialog.Description></Dialog.Header>
		<RemoteRecordSelect
			queryKey={['units', 'lease-transfer', dashboard.unit.id]}
			label="New rental"
			bind:value={destinationUnitValue}
			placeholder="Choose a rental"
			searchPlaceholder="Search available rentals…"
			emptyLabel="No available rentals"
			loadPage={loadDestinationUnits}
			disabled={transferMutation.isPending}
			required
			testid="transfer-destination-unit"
		/>
		{#if destinationTemplatesQuery.isLoading}
			<LoadingState label="Loading active lease templates" variant="spinner" testid="transfer-options-loading" />
		{:else if destinationTemplatesQuery.isError}
			<div class="rounded-xl border border-destructive/40 bg-destructive/5 p-4" role="alert">
				<p class="text-sm font-medium text-destructive">Active lease templates could not be loaded.</p>
				<Button class="mt-3" variant="outline" size="sm" onclick={() => destinationTemplatesQuery.refetch()}>Try again</Button>
			</div>
		{:else}
			<label class="block space-y-1 text-sm">
				<span>Lease template</span>
				<Select.Root
					type="single"
					value={String(destinationTemplateId)}
					onValueChange={(value) => (destinationTemplateId = Number(value ?? 0))}
				>
					<Select.Trigger class="w-full">
						{destinationTemplatesQuery.data?.items.find((template) => template.id === destinationTemplateId)?.name ?? 'Choose a template'}
					</Select.Trigger>
					<Select.Content>
						{#each destinationTemplatesQuery.data?.items ?? [] as template}
							<Select.Item value={String(template.id)} label={template.name}>{template.name}</Select.Item>
						{/each}
					</Select.Content>
				</Select.Root>
			</label>
		{/if}
		<label class="block space-y-1 text-sm"><span>Move effective date</span><DatePicker bind:value={effectiveOn} /></label>
		<label class="block space-y-1 text-sm"><span>Planned key handoff</span><DateTimePicker bind:value={plannedDestinationPossessionAtUtc} /></label>
		<label class="flex items-center gap-2 text-sm"><input type="checkbox" bind:checked={giveDestinationPossessionNow} /> Give access to the new rental now</label>
		{#if giveDestinationPossessionNow}<label class="block space-y-1 text-sm"><span>Why is access being given before the new lease is ready?</span><textarea bind:value={possessionAgreementExceptionReason} class="m3-field-surface min-h-20 w-full p-3"></textarea></label>{/if}
		<label class="flex items-center gap-2 text-sm"><input type="checkbox" bind:checked={carryTenantBalance} /> Move the tenant’s balance to the new rental</label>
		<label class="flex items-center gap-2 text-sm"><input type="checkbox" bind:checked={carrySecurityDeposit} /> Move the security deposit to the new rental</label>
		<label class="block space-y-1 text-sm"><span>Transfer reason</span><textarea bind:value={transferReason} class="min-h-20 w-full rounded-md border p-3"></textarea></label>
		<Dialog.Footer><Button variant="outline" onclick={() => (transferOpen = false)}>Cancel</Button><Button disabled={Number(destinationUnitValue) <= 0 || destinationTemplateId <= 0 || !effectiveOn || !transferReason.trim() || (giveDestinationPossessionNow && !possessionAgreementExceptionReason.trim()) || destinationTemplatesQuery.isLoading || destinationTemplatesQuery.isError || transferMutation.isPending} onclick={() => transferMutation.mutate()}>Transfer relationship</Button></Dialog.Footer>
	</Dialog.Content>
</Dialog.Root>

<Dialog.Root open={closeOpen} onOpenChange={(open) => (closeOpen = open)}>
	<Dialog.Content class="max-w-lg" data-testid="close-account-dialog">
		<Dialog.Header><Dialog.Title>Close tenant account</Dialog.Title><Dialog.Description>Before closing, confirm the keys were returned, nothing is owed, the deposit was settled, and no payments or signatures are still pending.</Dialog.Description></Dialog.Header>
		<label class="block space-y-1 text-sm"><span>Reason for closing</span><input bind:value={closeReasonCode} class="m3-field-surface h-10 w-full px-3" /></label>
		<label class="block space-y-1 text-sm"><span>Optional note</span><textarea bind:value={closeNote} class="min-h-20 w-full rounded-md border p-3"></textarea></label>
		<Dialog.Footer><Button variant="outline" onclick={() => (closeOpen = false)}>Cancel</Button><Button disabled={!closeReasonCode.trim() || closeMutation.isPending} onclick={() => closeMutation.mutate()}>Close account</Button></Dialog.Footer>
	</Dialog.Content>
</Dialog.Root>
