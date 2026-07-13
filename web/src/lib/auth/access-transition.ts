import type { QueryClient } from '@tanstack/svelte-query';
import type { AccessEnvelope } from '$lib/types/user';
import { assistantChat } from '$lib/stores/assistantChat.svelte';
import { notificationStore } from '$lib/stores/notifications.svelte';
import { initPortfolio } from '$lib/stores/portfolio.svelte';

/** Remove every client-owned projection that may have been authorized by the previous access view. */
export function resetAccessDependentClientState(
	queryClient: QueryClient,
	access: AccessEnvelope
): void {
	queryClient.clear();
	assistantChat.resetForAccessChange();
	notificationStore.resetForAccessChange();
	initPortfolio(access.selectedContext.portfolioId);
}
