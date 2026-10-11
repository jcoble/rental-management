<script lang="ts">
	import { goto } from '$app/navigation';
	import { onMount } from 'svelte';
	import { banking } from '$lib/api/endpoints/banking';
	import { apiErrorMessage, showError, showSuccess } from '$lib/utils/toast';

	type PlaidWindow = Window &
		typeof globalThis & {
			Plaid?: {
				create: (config: Record<string, unknown>) => { open: () => void; destroy?: () => void };
			};
		};

	let status = $state('Finishing bank connection...');

	async function loadPlaidScript() {
		const plaidWindow = window as PlaidWindow;
		if (plaidWindow.Plaid) return;
		await new Promise<void>((resolve, reject) => {
			const existing = document.querySelector<HTMLScriptElement>('script[src="https://cdn.plaid.com/link/v2/stable/link-initialize.js"]');
			if (existing) {
				existing.addEventListener('load', () => resolve(), { once: true });
				existing.addEventListener('error', () => reject(new Error('Plaid Link script failed to load.')), { once: true });
				return;
			}
			const script = document.createElement('script');
			script.src = 'https://cdn.plaid.com/link/v2/stable/link-initialize.js';
			script.async = true;
			script.onload = () => resolve();
			script.onerror = () => reject(new Error('Plaid Link script failed to load.'));
			document.head.appendChild(script);
		});
	}

	onMount(async () => {
		try {
			const linkToken = sessionStorage.getItem('plaid:linkToken');
			if (!linkToken) {
				status = 'Plaid session expired. Start bank connection again.';
				return;
			}

			await loadPlaidScript();
			const handler = (window as PlaidWindow).Plaid?.create({
				token: linkToken,
				receivedRedirectUri: window.location.href,
				onSuccess: async (public_token: string, metadata: Record<string, unknown>) => {
					const accounts = (metadata.accounts as Array<Record<string, string | undefined>> | undefined) ?? [];
					const account = accounts[0] ?? {};
					const institution = metadata.institution as Record<string, string | undefined> | undefined;
					const operationId = sessionStorage.getItem('plaid:exchangeOperationId') ?? crypto.randomUUID();
					sessionStorage.setItem('plaid:exchangeOperationId', operationId);
					await banking.exchangePlaidPublicToken({
						clientOperationId: operationId,
						publicToken: public_token,
						institutionName: institution?.name ?? 'Plaid bank',
						accountId: account.id ?? '',
						accountName: account.name ?? account.subtype ?? 'Linked account',
						accountMask: account.mask,
						accountType: account.type,
						accountSubtype: account.subtype
					});
					sessionStorage.removeItem('plaid:linkToken');
					sessionStorage.removeItem('plaid:exchangeOperationId');
					showSuccess('Bank account connected.');
					await goto('/banking');
				},
				onExit: async () => {
					await goto('/banking');
				}
			});
			handler?.open();
		} catch (err) {
			status = apiErrorMessage(err);
			showError(status);
		}
	});
</script>

<svelte:head>
	<title>Plaid Authorization - Rental Command</title>
</svelte:head>

<div class="flex h-full items-center justify-center p-6">
	<div class="rounded-md border border-border bg-card p-6 text-center">
		<p class="font-medium">{status}</p>
		<p class="mt-2 text-sm text-muted-foreground">You can return to Banking if this does not continue automatically.</p>
	</div>
</div>
