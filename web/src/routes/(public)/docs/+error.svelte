<script lang="ts">
	import { page } from '$app/stores';
	import { FileQuestion, ArrowLeft } from '@lucide/svelte';
	import { Button } from '$lib/components/ui/button';
</script>

<div class="mx-auto flex max-w-6xl flex-col items-center px-5 py-24 text-center sm:px-8">
	<div class="flex h-14 w-14 items-center justify-center rounded-2xl bg-secondary ring-1 ring-inset ring-border">
		<FileQuestion class="h-7 w-7 text-muted-foreground" />
	</div>
	<h1 class="mt-6 text-2xl font-semibold tracking-tight">
		{$page.status === 404 ? 'Article not found' : 'Something went wrong'}
	</h1>
	<p class="mt-2 max-w-md text-sm text-muted-foreground">
		{$page.error?.message ?? 'We could not load that page. Try heading back to the docs home.'}
	</p>
	<Button href="/docs" class="mt-6">
		<ArrowLeft class="h-4 w-4" />
		Back to docs
	</Button>
</div>
