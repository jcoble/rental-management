<script lang="ts">
	import { createMutation, createQuery, useQueryClient } from '@tanstack/svelte-query';
	import {
		activateAiCredential,
		getAiIntegrationStatus,
		removeAiCredential,
		rotateAiCredential,
		testAiCredential,
		type AiProvider
	} from '$lib/api/endpoints/ai-integrations';
	import { Button } from '$lib/components/ui/button';
	import * as Card from '$lib/components/ui/card';
	import { Input } from '$lib/components/ui/input';
	import LoadingState from '$lib/components/shared/LoadingState.svelte';
	import { apiErrorMessage, showError, showSuccess } from '$lib/utils/toast';

	const queryClient = useQueryClient();
	const statusQuery = createQuery(() => ({
		queryKey: ['ai-integration-status'],
		queryFn: getAiIntegrationStatus
	}));

	let provider = $state<AiProvider>('openai');
	let modelId = $state('gpt-4o');
	let apiKey = $state('');
	let testedSignature = $state<string | null>(null);
	const signature = $derived(`${provider}:${modelId}:${apiKey}`);
	const canSave = $derived(apiKey.trim().length > 0 && modelId.trim().length > 0 && testedSignature === signature);

	$effect(() => {
		const status = statusQuery.data;
		if (!status?.configured || !status.provider || !status.modelId) return;
		provider = status.provider;
		modelId = status.modelId;
	});

	function changedProvider(next: AiProvider) {
		provider = next;
		modelId = next === 'openai' ? 'gpt-4o' : 'claude-3-5-sonnet-latest';
		apiKey = '';
		testedSignature = null;
	}

	const testMutation = createMutation(() => ({
		mutationFn: () => testAiCredential({ provider, modelId: modelId.trim(), apiKey: apiKey.trim() }),
		onSuccess: () => {
			testedSignature = signature;
			showSuccess('Credential verified. You can now save it.');
		},
		onError: (error) => {
			testedSignature = null;
			showError(apiErrorMessage(error));
		}
	}));

	const saveMutation = createMutation(() => ({
		mutationFn: () => {
			const input = { provider, modelId: modelId.trim(), apiKey: apiKey.trim() };
			return statusQuery.data?.configured ? rotateAiCredential(input) : activateAiCredential(input);
		},
		onSuccess: (status) => {
			queryClient.setQueryData(['ai-integration-status'], status);
			apiKey = '';
			testedSignature = null;
			showSuccess('Workspace AI provider saved.');
		},
		onError: (error) => showError(apiErrorMessage(error))
	}));

	const removeMutation = createMutation(() => ({
		mutationFn: removeAiCredential,
		onSuccess: () => {
			queryClient.setQueryData(['ai-integration-status'], {
				configured: false,
				provider: null,
				modelId: null,
				lastTestedAtUtc: null,
				updatedAtUtc: null
			});
			apiKey = '';
			testedSignature = null;
			showSuccess('Workspace AI credential removed.');
		},
		onError: (error) => showError(apiErrorMessage(error))
	}));
</script>

<svelte:head><title>AI provider · Rental Command</title></svelte:head>

<div class="mx-auto max-w-3xl space-y-6" data-testid="ai-provider-settings">
	<div>
		<h1 class="text-2xl font-semibold tracking-tight">AI provider</h1>
		<p class="mt-1 text-sm text-muted-foreground">
			Use your workspace's OpenAI or Anthropic account for Scan / Add. Rental Command never shows the key after you save it.
		</p>
	</div>

	<Card.Root>
		<Card.Content class="space-y-5 p-6">
			{#if statusQuery.isLoading}
				<LoadingState label="Loading AI provider status" variant="spinner" testid="ai-provider-loading" />
			{:else if statusQuery.isError}
				<div class="space-y-3">
					<p class="text-sm text-destructive">We couldn't load the AI provider status.</p>
					<Button variant="outline" onclick={() => statusQuery.refetch()}>Retry</Button>
				</div>
			{:else}
				<div class="rounded-lg border bg-muted/30 p-4" aria-live="polite">
					<p class="font-medium">{statusQuery.data?.configured ? 'Configured' : 'Not configured'}</p>
					{#if statusQuery.data?.configured}
						<p class="mt-1 text-sm text-muted-foreground">
							{statusQuery.data.provider === 'openai' ? 'OpenAI' : 'Anthropic'} · {statusQuery.data.modelId}
						</p>
					{:else}
						<p class="mt-1 text-sm text-muted-foreground">
							Scan / Add will stop with a clear missing-key message; it will not use a shared Rental Command key.
						</p>
					{/if}
				</div>

				<label class="grid gap-2 text-sm font-medium">
					Provider
					<select
						class="h-11 rounded-md border border-input bg-background px-3"
						value={provider}
						onchange={(event) => changedProvider(event.currentTarget.value as AiProvider)}
					>
						<option value="openai">OpenAI</option>
						<option value="anthropic">Anthropic</option>
					</select>
				</label>

				<label class="grid gap-2 text-sm font-medium">
					Model
					<Input bind:value={modelId} autocomplete="off" oninput={() => (testedSignature = null)} />
				</label>

				<label class="grid gap-2 text-sm font-medium">
					API key
					<Input
						type="password"
						bind:value={apiKey}
						autocomplete="new-password"
						placeholder={statusQuery.data?.configured ? 'Enter a replacement key' : 'Paste a key'}
						oninput={() => (testedSignature = null)}
					/>
					<span class="text-xs font-normal text-muted-foreground">Encrypted at rest and write-only after save.</span>
				</label>

				<div class="flex flex-wrap gap-3">
					<Button
						variant="outline"
						disabled={!apiKey.trim() || !modelId.trim() || testMutation.isPending}
						onclick={() => testMutation.mutate()}
					>
						{testMutation.isPending ? 'Testing…' : 'Test credential'}
					</Button>
					<Button disabled={!canSave || saveMutation.isPending} onclick={() => saveMutation.mutate()}>
						{saveMutation.isPending ? 'Saving…' : statusQuery.data?.configured ? 'Rotate credential' : 'Activate provider'}
					</Button>
					{#if statusQuery.data?.configured}
						<Button
							variant="destructive"
							disabled={removeMutation.isPending}
							onclick={() => removeMutation.mutate()}
						>
							{removeMutation.isPending ? 'Removing…' : 'Remove provider'}
						</Button>
					{/if}
				</div>
			{/if}
		</Card.Content>
	</Card.Root>
</div>
