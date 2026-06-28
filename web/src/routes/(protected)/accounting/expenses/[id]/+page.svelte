<script lang="ts">
	import { page } from '$app/state';
	import { goto } from '$app/navigation';
	import ExpenseDetail from '$lib/components/records/ExpenseDetail.svelte';
</script>

<ExpenseDetail expenseId={Number(page.params.id)} onDeleted={() => goto('/accounting')} />
