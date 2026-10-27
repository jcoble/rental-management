<script lang="ts">
	import { QueryClient, QueryClientProvider } from '@tanstack/svelte-query';
	import AiSettingsPage from '../src/routes/(protected)/settings/integrations/ai/+page.svelte';

	const queryClient = new QueryClient({
		defaultOptions: { queries: { retry: false } }
	});
</script>

<QueryClientProvider client={queryClient}>
	<AiSettingsPage />
</QueryClientProvider>
