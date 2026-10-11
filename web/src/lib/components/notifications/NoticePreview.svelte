<script lang="ts">
	import { createMutation } from '@tanstack/svelte-query';
	import {
		notifications,
		type NoticePreviewResponse,
		type NoticeTestSendResponse
	} from '$lib/api/endpoints/notifications';
	import type { NoticeMergeFieldHelpResponse } from '$lib/api/types/notification';
	import { Button } from '$lib/components/ui/button';
	import { Input } from '$lib/components/ui/input';
	import { apiErrorMessage } from '$lib/utils/toast';

	let {
		systemKey,
		subject,
		body,
		mergeFields
	}: {
		systemKey: string;
		subject: string;
		body: string;
		mergeFields: NoticeMergeFieldHelpResponse[];
	} = $props();

	let preview = $state<NoticePreviewResponse | null>(null);
	let testDestination = $state('');
	let testResult = $state<NoticeTestSendResponse | null>(null);
	let error = $state('');

	const previewMutation = createMutation(() => ({
		mutationFn: () => notifications.tenantNotices.previewNotice({ systemKey, subject, body }),
		onSuccess: (result) => {
			preview = result;
			error = '';
		},
		onError: (failure) => (error = apiErrorMessage(failure))
	}));

	const testMutation = createMutation(() => ({
		mutationFn: () => notifications.tenantNotices.sendTest({
			systemKey,
			subject,
			body,
			destination: testDestination.trim()
		}),
		onSuccess: (result) => {
			testResult = result;
			error = '';
		},
		onError: (failure) => {
			testResult = null;
			error = apiErrorMessage(failure);
		}
	}));
</script>

<section class="space-y-4 rounded-lg border border-border bg-muted/20 p-4" data-testid="notice-preview">
	<div>
		<h4 class="font-medium">Preview unsaved message</h4>
		<p class="mt-1 text-xs text-muted-foreground">Example values render only this preview and test. Nothing is saved, reviewed, or sent to a tenant.</p>
	</div>
	<div class="flex flex-wrap gap-2">
		{#each mergeFields as field (field.key)}
			<span class="rounded-full bg-background px-2 py-1 text-xs">{field.label}: {field.example}</span>
		{/each}
	</div>
	<Button type="button" variant="outline" onclick={() => previewMutation.mutate()} disabled={previewMutation.isPending || !subject.trim() || !body.trim()}>
		{previewMutation.isPending ? 'Rendering…' : 'Render current edits'}
	</Button>
	{#if preview}
		<div class="space-y-3 rounded-md border border-border bg-background p-4" aria-live="polite">
			<p class="font-medium">{preview.subject}</p>
			<p class="whitespace-pre-wrap text-sm leading-6">{preview.body}</p>
		</div>
	{/if}
	<div class="grid gap-3 border-t border-border pt-4 sm:grid-cols-[minmax(0,1fr)_auto] sm:items-end">
		<label>
			<span class="mb-1 block text-sm font-medium">Controlled non-tenant test email</span>
			<Input type="email" bind:value={testDestination} placeholder="you+notice-test@example.com" autocomplete="off" />
		</label>
		<Button type="button" onclick={() => testMutation.mutate()} disabled={testMutation.isPending || !testDestination.trim() || !subject.trim() || !body.trim()}>
			{testMutation.isPending ? 'Sending test…' : 'Send isolated test'}
		</Button>
	</div>
	{#if testResult}
		<p
			class:test-success={testResult.state === 'Accepted'}
			class:test-warning={testResult.state === 'Suppressed'}
			class:test-error={testResult.state === 'ProviderError'}
			class="rounded-md border border-border bg-background p-3 text-sm"
			aria-live="polite"
		>
			<strong>{testResult.state === 'ProviderError' ? 'Provider error' : testResult.state}:</strong>
			{testResult.message}
		</p>
	{/if}
	{#if error}<p class="text-sm text-destructive" aria-live="assertive">{error}</p>{/if}
</section>
