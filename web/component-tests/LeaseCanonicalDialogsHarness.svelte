<script lang="ts">
	import { QueryClient, QueryClientProvider } from '@tanstack/svelte-query';
	import AddendumCreateDialog from '$lib/components/leases/AddendumCreateDialog.svelte';
	import AgreementDraftDialog from '$lib/components/leases/AgreementDraftDialog.svelte';
	import AgreementSignatureProgress from '$lib/components/leases/AgreementSignatureProgress.svelte';
	import AgreementSuccessorDialog from '$lib/components/leases/AgreementSuccessorDialog.svelte';

	type Mode = 'addendum' | 'successor' | 'draft' | 'signature';

	let {
		mode,
		leaseManagementId,
		propertyId = 7,
		source,
		changeType = 'Correction',
		businessDate = '2027-01-01',
		leaseAgreementId = 101,
		agreementNumber = 'LEASE-101'
	}: {
		mode: Mode;
		leaseManagementId: number;
		propertyId?: number;
		source?: Record<string, unknown>;
		changeType?: 'Correction' | 'Restatement' | 'Renewal' | 'MonthToMonth';
		businessDate?: string;
		leaseAgreementId?: number;
		agreementNumber?: string;
	} = $props();

	const queryClient = new QueryClient({
		defaultOptions: {
			queries: { retry: false },
			mutations: { retry: false }
		}
	});

	const noop = () => undefined;
</script>

<QueryClientProvider client={queryClient}>
	{#if mode === 'addendum'}
		<AddendumCreateDialog
			{leaseManagementId}
			{propertyId}
			baseAgreement={source as { leaseAgreementId: number; agreementNumber: string }}
			onclose={noop}
			oncreated={noop}
		/>
	{:else if mode === 'successor'}
		<AgreementSuccessorDialog
			{leaseManagementId}
			source={source as never}
			{changeType}
			{businessDate}
			onclose={noop}
			oncreated={noop}
		/>
	{:else if mode === 'draft'}
		<AgreementDraftDialog
			{leaseManagementId}
			{leaseAgreementId}
			source={source as never}
			canCancel={false}
			onclose={noop}
			onissued={noop}
			oncanceled={noop}
		/>
	{:else}
		<AgreementSignatureProgress
			{leaseManagementId}
			{leaseAgreementId}
			{agreementNumber}
			onclose={noop}
		/>
	{/if}
</QueryClientProvider>
