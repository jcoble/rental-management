<script lang="ts">
	import { createMutation, createQuery, useQueryClient } from '@tanstack/svelte-query';
	import {
		activateAiDecisionCredential,
		getAiDecisionStatus,
		removeAiDecisionCredential,
		rotateAiDecisionCredential,
		testAiDecisionCredential,
		type AiDecisionProvider
	} from '$lib/api/endpoints/ai-integrations';
	import { Button } from '$lib/components/ui/button';
	import * as Card from '$lib/components/ui/card';
	import { Input } from '$lib/components/ui/input';
	import * as Select from '$lib/components/ui/select';
	import LoadingState from '$lib/components/shared/LoadingState.svelte';
	import { apiErrorMessage, showError, showSuccess } from '$lib/utils/toast';

	const queryClient = useQueryClient();
	const statusQuery = createQuery(() => ({
		queryKey: ['ai-decision-status'],
		queryFn: getAiDecisionStatus
	}));

	let provider = $state<AiDecisionProvider>('typesafe');
	let modelId = $state('jev-1.13.0');
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

	function providerName(value: AiDecisionProvider | null) {
		return value === 'openrouter' ? 'OpenRouter' : 'TypeSafe';
	}

	function changedProvider(next: AiDecisionProvider) {
		provider = next;
		modelId = next === 'typesafe' ? 'jev-1.13.0' : 'jev-1.13';
		apiKey = '';
		testedSignature = null;
	}

	const testMutation = createMutation(() => ({
		mutationFn: () => testAiDecisionCredential({ provider, modelId: modelId.trim(), apiKey: apiKey.trim() }),
		onSuccess: () => {
			testedSignature = signature;
			showSuccess('Jev key verified. You can now save it.');
		},
		onError: (error) => {
			testedSignature = null;
			showError(apiErrorMessage(error));
		}
	}));

	const saveMutation = createMutation(() => ({
		mutationFn: () => {
			const input = { provider, modelId: modelId.trim(), apiKey: apiKey.trim() };
			return statusQuery.data?.configured ? rotateAiDecisionCredential(input) : activateAiDecisionCredential(input);
		},
		onSuccess: (status) => {
			queryClient.setQueryData(['ai-decision-status'], status);
			apiKey = '';
			testedSignature = null;
			showSuccess('Faster document sorting saved.');
		},
		onError: (error) => showError(apiErrorMessage(error))
	}));

	const removeMutation = createMutation(() => ({
		mutationFn: removeAiDecisionCredential,
		onSuccess: () => {
			queryClient.setQueryData(['ai-decision-status'], {
				configured: false,
				provider: null,
				modelId: null,
				lastTestedAtUtc: null,
				updatedAtUtc: null
			});
			apiKey = '';
			testedSignature = null;
			showSuccess('Jev key removed.');
		},
		onError: (error) => showError(apiErrorMessage(error))
	}));
</script>

<Card.Root data-testid="ai-decision-settings">
	<Card.Content class="space-y-5 p-6">
		<div>
			<h2 class="text-lg font-semibold">Faster document sorting (optional)</h2>
			<p class="mt-1 text-sm text-muted-foreground">
				Add a Jev key and Scan / Add sorts each document (receipt, rent check, lease…) in under a second.
				Without it, Scan / Add still works using the AI connection above.
			</p>
		</div>

		{#if statusQuery.isLoading}
			<LoadingState label="Loading document sorting status" variant="spinner" testid="ai-decision-loading" />
		{:else if statusQuery.isError}
			<div class="space-y-3">
				<p class="text-sm text-destructive">We couldn't load the document sorting status.</p>
				<Button variant="outline" onclick={() => statusQuery.refetch()}>Retry</Button>
			</div>
		{:else}
			<div class="rounded-lg border bg-muted/30 p-4" aria-live="polite">
				<p class="font-medium">{statusQuery.data?.configured ? 'Connected' : 'Not connected'}</p>
				{#if statusQuery.data?.configured}
					<p class="mt-1 text-sm text-muted-foreground">
						{providerName(statusQuery.data.provider)} · {statusQuery.data.modelId}
					</p>
				{:else}
					<p class="mt-1 text-sm text-muted-foreground">
						Scan / Add sorts documents using the AI connection above.
					</p>
				{/if}
			</div>

			<div class="grid gap-2 text-sm font-medium">
				<span>Sorting service</span>
				<Select.Root
					type="single"
					value={provider}
					onValueChange={(value) => changedProvider(value as AiDecisionProvider)}
				>
					<Select.Trigger class="h-11 w-full" data-testid="ai-decision-select">
						{providerName(provider)}
					</Select.Trigger>
					<Select.Content>
						<Select.Item value="typesafe" label="TypeSafe">TypeSafe</Select.Item>
						<Select.Item value="openrouter" label="OpenRouter">OpenRouter</Select.Item>
					</Select.Content>
				</Select.Root>
			</div>

			<label class="grid gap-2 text-sm font-medium">
				Jev model
				<Input bind:value={modelId} autocomplete="off" oninput={() => (testedSignature = null)} />
				<span class="text-xs font-normal text-muted-foreground">Keep the suggested model unless your service requires another one.</span>
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
				<span class="text-xs font-normal text-muted-foreground">Rental Command encrypts this key and will not show it again after you save.</span>
			</label>

			<div class="flex flex-wrap gap-3">
				<Button
					variant="outline"
					data-testid="ai-decision-check"
					disabled={!apiKey.trim() || !modelId.trim() || testMutation.isPending}
					onclick={() => testMutation.mutate()}
				>
					{testMutation.isPending ? 'Checking…' : 'Check key'}
				</Button>
				<Button
					data-testid="ai-decision-save"
					disabled={!canSave || saveMutation.isPending}
					onclick={() => saveMutation.mutate()}
				>
					{saveMutation.isPending ? 'Saving…' : statusQuery.data?.configured ? 'Replace connection' : 'Save connection'}
				</Button>
				{#if statusQuery.data?.configured}
					<Button
						variant="destructive"
						data-testid="ai-decision-remove"
						disabled={removeMutation.isPending}
						onclick={() => removeMutation.mutate()}
					>
						{removeMutation.isPending ? 'Removing…' : 'Remove connection'}
					</Button>
				{/if}
			</div>
		{/if}
	</Card.Content>
</Card.Root>
