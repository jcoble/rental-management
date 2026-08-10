<script lang="ts">
	let { leaseManagementId, propertyId, baseAgreement }: {
		leaseManagementId: number;
		propertyId: number;
		baseAgreement: { leaseAgreementId: number; agreementNumber: string };
	} = $props();
</script>

<div data-testid="stub-addendum-create-dialog">
	<span data-testid="stub-addendum-lease-management-id">{leaseManagementId}</span>
	<span data-testid="stub-addendum-property-id">{propertyId}</span>
	<span data-testid="stub-addendum-base-agreement-id">{baseAgreement.leaseAgreementId}</span>
	<span data-testid="stub-addendum-base-agreement-number">{baseAgreement.agreementNumber}</span>
</div>
