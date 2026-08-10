<script lang="ts">
	import { ArrowLeft, Building, FileText } from '@lucide/svelte';
</script>

<svelte:head>
	<title>Terms of Service - Rental Command</title>
	<meta
		name="description"
		content="Rental Command Terms of Service for landlords and property managers."
	/>
</svelte:head>

<div class="min-h-dvh overflow-y-auto bg-background text-foreground">
	<header class="border-b border-border/60 bg-background/90 backdrop-blur">
		<div class="mx-auto flex max-w-4xl items-center justify-between px-5 py-4 sm:px-8">
			<a href="/welcome" class="flex items-center gap-2 text-sm font-semibold tracking-tight">
				<span class="flex h-8 w-8 items-center justify-center rounded-lg bg-primary/10 text-primary ring-1 ring-inset ring-primary/20">
					<Building class="h-4 w-4" />
				</span>
				Rental Command
			</a>
			<a href="/welcome" class="inline-flex items-center gap-1.5 text-sm font-medium text-muted-foreground transition-colors hover:text-foreground">
				<ArrowLeft class="h-4 w-4" />
				Home
			</a>
		</div>
	</header>

	<main class="mx-auto max-w-4xl px-5 py-10 sm:px-8 sm:py-14">
		<div class="mb-8">
			<span class="inline-flex items-center gap-2 rounded-full border border-primary/20 bg-primary/10 px-3 py-1 text-xs font-medium text-primary">
				<FileText class="h-3.5 w-3.5" />
				Terms
			</span>
			<h1 class="mt-4 text-3xl font-semibold tracking-tight sm:text-4xl">Rental Command Terms of Service</h1>
			<p class="mt-2 text-sm text-muted-foreground">Last updated: 10 August 2026</p>
		</div>

		<article class="prose prose-sm max-w-none dark:prose-invert">
			<p>These terms explain the basic rules for using Rental Command. By creating an account, you agree to use the service lawfully and to keep your sign-in information secure.</p>
			<h2>Use of the service</h2>
			<p>Rental Command helps landlords and property managers organize rental records, accounting, maintenance, documents, and communications. You are responsible for the information you enter and for having the right to use it.</p>
			<h2>Your account</h2>
			<p>Keep your account details accurate, protect your password, and tell us promptly if you believe someone else accessed your account.</p>
			<h2>Questions</h2>
			<p>For questions about these terms, contact <strong>jcoble@rentalcommand.net</strong>. Our <a href="/privacy">Privacy Policy</a> explains how we handle personal information.</p>
		</article>
	</main>
</div>
