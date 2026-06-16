<!--
  TenantFields — the single source-of-truth field group for a Tenant create/edit form. Consumed by
  BOTH the manual New-Tenant modal (tenants/+page.svelte) AND the guided scan flow's Step 3. Owns ONLY
  the field rows + inline errors + the optional "from your lease" badge (AC-3) — not the dialog chrome
  or submit button (each caller wraps it).
-->
<script lang="ts">
	import { Input } from '$lib/components/ui/input';
	import AutoFilledBadge from './AutoFilledBadge.svelte';

	let {
		form = $bindable(),
		errors = {},
		autoFilled,
		testidPrefix = 'tenant'
	}: {
		form: { firstName: string; lastName: string; email: string; phone: string; emergencyContact: string };
		errors?: Record<string, string>;
		autoFilled?: Set<string>;
		testidPrefix?: string;
	} = $props();
	const filled = (key: string) => !!autoFilled?.has(key);
</script>

<div class="grid gap-3 md:grid-cols-2" data-testid={`${testidPrefix}-fields`}>
	<div>
		<div class="mb-1 flex items-center gap-2">
			<span class="text-xs font-medium text-muted-foreground">First name</span>
			<AutoFilledBadge show={filled('firstName')} />
		</div>
		<Input data-testid={`${testidPrefix}-first-name-input`} bind:value={form.firstName} placeholder="First name" />
		{#if errors.firstName}<p class="mt-1 text-xs text-destructive" data-testid={`${testidPrefix}-first-name-error`}>{errors.firstName}</p>{/if}
	</div>
	<div>
		<div class="mb-1 flex items-center gap-2">
			<span class="text-xs font-medium text-muted-foreground">Last name</span>
			<AutoFilledBadge show={filled('lastName')} />
		</div>
		<Input data-testid={`${testidPrefix}-last-name-input`} bind:value={form.lastName} placeholder="Last name" />
		{#if errors.lastName}<p class="mt-1 text-xs text-destructive" data-testid={`${testidPrefix}-last-name-error`}>{errors.lastName}</p>{/if}
	</div>
	<div>
		<span class="mb-1 block text-xs font-medium text-muted-foreground">Email</span>
		<Input data-testid={`${testidPrefix}-email-input`} bind:value={form.email} placeholder="Email" />
		{#if errors.email}<p class="mt-1 text-xs text-destructive" data-testid={`${testidPrefix}-email-error`}>{errors.email}</p>{/if}
	</div>
	<div>
		<span class="mb-1 block text-xs font-medium text-muted-foreground">Phone</span>
		<Input data-testid={`${testidPrefix}-phone-input`} bind:value={form.phone} placeholder="Phone" />
	</div>
	<div class="md:col-span-2">
		<span class="mb-1 block text-xs font-medium text-muted-foreground">Emergency contact</span>
		<Input data-testid={`${testidPrefix}-emergency-input`} bind:value={form.emergencyContact} placeholder="Emergency contact" />
	</div>
</div>
