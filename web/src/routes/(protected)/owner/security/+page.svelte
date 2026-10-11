<script lang="ts">
	import type { ActionData } from './$types';
	import AccountSecurityPage from '$lib/components/account/AccountSecurityPage.svelte';

	let { form }: { form: ActionData } = $props();
</script>

<AccountSecurityPage {form} backHref="/owner" backLabel="Back to owner overview" />
