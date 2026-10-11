<script lang="ts">
	import { goto } from '$app/navigation';
	import { page } from '$app/state';
	import type { ApplicationResponse, ApplicationStatus } from '$lib/api/endpoints/applications';
	import type { PrepareMoveInResponse } from '$lib/api/endpoints/lease-managements';
	import { leasingWorkspace, type LeasingApplicationDetail } from '$lib/api/endpoints/leasing-workspace';
	import PrepareMoveInDialog from '$lib/components/applications/PrepareMoveInDialog.svelte';
	import ApplicationDetail from '$lib/components/records/ApplicationDetail.svelte';
	import { readPrepareMoveInPrefill } from '$lib/leases/prepare-move-in-prefill';

	const applicationId = $derived(Number(page.params.id));
	const prepareMoveInBasePath = $derived(`/leasing/applications/${page.params.id}`);
	const prepareMoveInPrefill = $derived(readPrepareMoveInPrefill(page.url.searchParams));

	function closePrepareMoveIn() {
		const url = new URL(page.url);
		url.searchParams.delete('prepareMoveIn');
		url.searchParams.delete('applicationId');
		url.searchParams.delete('unitId');
		url.searchParams.delete('tenantId');
		void goto(`${url.pathname}${url.search}`, {
			replaceState: true,
			keepFocus: true,
			noScroll: true
		});
	}

	function finishPrepareMoveIn(result: PrepareMoveInResponse) {
		void goto(`/leasing/move-ins/${result.leaseManagementId}`);
	}

	function mapLeasingApplication(application: LeasingApplicationDetail): ApplicationResponse {
		const [firstName = application.applicantName, ...lastNameParts] = application.applicantName.split(' ');
		return {
			id: application.id,
			propertyId: application.propertyId ?? null,
			unitId: application.unitId ?? null,
			propertyName: application.propertyName ?? null,
			unitNumber: application.unitNumber ?? null,
			firstName,
			lastName: lastNameParts.join(' '),
			email: application.email ?? '',
			phone: application.phone ?? '',
			dateOfBirth: null,
			currentAddressLine1: null,
			currentAddressLine2: null,
			currentCity: null,
			currentState: null,
			currentPostalCode: null,
			currentAddress: null,
			employer: null,
			monthlyIncome: application.monthlyIncome ?? null,
			desiredMoveInDate: application.desiredMoveInDate ?? null,
			notes: application.notes ?? null,
			consentGiven: application.consentGiven,
			consentAtUtc: null,
			status: application.status as ApplicationStatus,
			decisionReason: null,
			submittedAtUtc: application.submittedAtUtc,
			reviewedAtUtc: null,
			approvedTenantId: application.approvedTenantId ?? null,
			hasScan: false,
			scanIsImage: false,
			testId: `application-${application.id}`
		};
	}

	async function loadLeasingApplication(id: number): Promise<ApplicationResponse> {
		return mapLeasingApplication(await leasingWorkspace.application(id));
	}
</script>

<ApplicationDetail
	{applicationId}
	onDeleted={() => goto('/leasing/pipeline')}
	{prepareMoveInBasePath}
	loadApplication={loadLeasingApplication}
	showApplicationActions={false}
	showScreening={false}
	showTenantLink={false}
	applicationQueryScope="leasing"
/>

{#if prepareMoveInPrefill}
	<PrepareMoveInDialog
		prefill={prepareMoveInPrefill}
		onclose={closePrepareMoveIn}
		onprepared={finishPrepareMoveIn}
	/>
{/if}
