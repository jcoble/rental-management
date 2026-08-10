<script lang="ts">
	let { leaseManagementId, source, changeType }: {
		leaseManagementId: number;
		source: { leaseAgreementId: number; agreementNumber: string };
		changeType: string;
	} = $props();
</script>

<div data-testid="stub-agreement-successor-dialog">
	<span data-testid="stub-successor-lease-management-id">{leaseManagementId}</span>
	<span data-testid="stub-successor-source-agreement-id">{source.leaseAgreementId}</span>
	<span data-testid="stub-successor-source-agreement-number">{source.agreementNumber}</span>
	<span data-testid="stub-successor-change-type">{changeType}</span>
</div>
