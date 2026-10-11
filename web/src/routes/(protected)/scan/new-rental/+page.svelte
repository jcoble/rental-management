<script lang="ts">
	import { goto } from '$app/navigation';
	import { page } from '$app/state';
	import { recordHref } from '$lib/navigation/record-href';
	import { getCurrentPortfolioId } from '$lib/stores/portfolio.svelte';
	import PageBreadcrumb from '$lib/components/shared/PageBreadcrumb.svelte';
	import LeaseFirstImport from '$lib/components/scan/LeaseFirstImport.svelte';
	import { hasAllPropertiesRentalsManageAuthority } from '$lib/auth/property-authority';
	import { parseNewRentalDraftId } from '$lib/scan/new-rental-state';
	import { getAuthState } from '$lib/stores/auth.svelte';

	const portfolioId = getCurrentPortfolioId();
	const initialDraftId = parseNewRentalDraftId(page.url.searchParams);
	const authState = getAuthState();
	const currentAccess = $derived(page.data.access ?? authState.accessEnvelope ?? null);
	const canCreateProperty = $derived(hasAllPropertiesRentalsManageAuthority(currentAccess));

	function onComplete(result: { leaseManagementId?: number | null; unitId?: number | null }) {
		if (result.leaseManagementId) {
			// Existing unit chosen → unit Command Center Lease tab; a freshly created unit
			// has no id in scope, so recordHref falls back to /leases/{id}.
			goto(recordHref('leaseManagement', { id: result.leaseManagementId, unitId: result.unitId }));
		} else {
			goto('/leases');
		}
	}
</script>

<svelte:head>
	<title>New Rental from a Lease - Rental Command</title>
</svelte:head>

<div class="mx-auto max-w-2xl space-y-4 p-4">
	<PageBreadcrumb crumbs={[{ label: 'Scan', href: '/scan' }, { label: 'New rental from your lease' }]} />
	<LeaseFirstImport {portfolioId} {initialDraftId} {canCreateProperty} syncDraftToUrl oncomplete={onComplete} />
</div>
