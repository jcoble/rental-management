<script lang="ts">
	import { FileText, WalletCards, ChevronRight } from '@lucide/svelte';
</script>

<svelte:head><title>Account &amp; lease - Rental Command</title></svelte:head>

<div class="h-full overflow-y-auto bg-background" data-testid="tenant-account-lease-hub">
	<div class="mx-auto max-w-4xl space-y-6 px-5 py-6 md:px-8">
		<header>
			<p class="text-sm text-muted-foreground">Your rental</p>
			<h1 class="text-2xl font-semibold tracking-normal">Account &amp; lease</h1>
			<p class="mt-2 max-w-2xl text-sm text-muted-foreground">
				Review what you owe, payment history, and the agreement for your home.
			</p>
		</header>

		<div class="grid gap-4 md:grid-cols-2">
			<a
				href="/portal/payments"
				class="group rounded-xl border border-border bg-card p-5 transition-colors hover:border-primary/50 hover:bg-muted/20"
				data-testid="tenant-account-link"
			>
				<div class="flex items-start justify-between gap-4">
					<div class="flex gap-3">
						<div class="rounded-lg bg-primary/10 p-2 text-primary"><WalletCards class="size-5" /></div>
						<div>
							<h2 class="font-semibold">Charges &amp; payments</h2>
							<p class="mt-1 text-sm text-muted-foreground">See your current balance, charges, credits, receipts, and payment options.</p>
						</div>
					</div>
					<ChevronRight class="mt-1 size-4 shrink-0 text-muted-foreground transition-transform group-hover:translate-x-0.5" />
				</div>
			</a>

			<a
				href="/portal/lease"
				class="group rounded-xl border border-border bg-card p-5 transition-colors hover:border-primary/50 hover:bg-muted/20"
				data-testid="tenant-lease-link"
			>
				<div class="flex items-start justify-between gap-4">
					<div class="flex gap-3">
						<div class="rounded-lg bg-primary/10 p-2 text-primary"><FileText class="size-5" /></div>
						<div>
							<h2 class="font-semibold">Lease agreement</h2>
							<p class="mt-1 text-sm text-muted-foreground">Review your agreement, rent terms, important dates, and stored lease details.</p>
						</div>
					</div>
					<ChevronRight class="mt-1 size-4 shrink-0 text-muted-foreground transition-transform group-hover:translate-x-0.5" />
				</div>
			</a>
		</div>
	</div>
</div>
