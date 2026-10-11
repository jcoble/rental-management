<script lang="ts">
	let { leaseManagementId, leaseAgreementId, source }: {
		leaseManagementId: number;
		leaseAgreementId: number;
		source: { leaseAgreementId: number; agreementNumber: string } | null;
	} = $props();
</script>

<div data-testid="stub-agreement-draft-dialog">
	<span data-testid="stub-draft-lease-management-id">{leaseManagementId}</span>
	<span data-testid="stub-draft-agreement-id">{leaseAgreementId}</span>
	<span data-testid="stub-draft-source-id">{source?.leaseAgreementId ?? ''}</span>
	<span data-testid="stub-draft-source-number">{source?.agreementNumber ?? ''}</span>
</div>
