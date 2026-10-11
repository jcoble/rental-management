<script lang="ts">
	let { leaseManagementId, leaseAgreementId, agreementNumber }: {
		leaseManagementId: number;
		leaseAgreementId: number;
		agreementNumber: string;
	} = $props();
</script>

<div data-testid="stub-agreement-signature-progress">
	<span data-testid="stub-signature-lease-management-id">{leaseManagementId}</span>
	<span data-testid="stub-signature-agreement-id">{leaseAgreementId}</span>
	<span data-testid="stub-signature-agreement-number">{agreementNumber}</span>
</div>
