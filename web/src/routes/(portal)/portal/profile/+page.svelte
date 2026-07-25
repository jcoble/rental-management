<script lang="ts">
	import { BellRing, CalendarClock, ChevronRight, ShieldCheck } from '@lucide/svelte';
</script>

<svelte:head><title>Profile - Rental Command</title></svelte:head>

<div class="h-full overflow-y-auto bg-background" data-testid="tenant-profile-hub">
	<div class="mx-auto max-w-4xl space-y-6 px-5 py-6 md:px-8">
		<header>
			<p class="text-sm text-muted-foreground">Your account</p>
			<h1 class="text-2xl font-semibold tracking-normal">Profile</h1>
			<p class="mt-2 max-w-2xl text-sm text-muted-foreground">Manage your sign-in, personal alerts, notifications, and scheduled visits.</p>
		</header>

		<div class="divide-y divide-border overflow-hidden rounded-xl border border-border bg-card">
			{#each [
				{ href: '/portal/security', label: 'Sign-in & security', description: 'Change your password and review account security.', icon: ShieldCheck },
				{ href: '/settings/notifications/my-alerts', label: 'My alerts', description: 'Choose how Rental Command sends alerts to you.', icon: BellRing },
				{ href: '/portal/notifications', label: 'Notifications', description: 'Review recent updates about your rental.', icon: BellRing },
				{ href: '/portal/appointments', label: 'Appointments', description: 'See upcoming maintenance visits and other scheduled events.', icon: CalendarClock }
			] as item}
				<a href={item.href} class="group flex items-center gap-3 p-4 transition-colors hover:bg-muted/30">
					<div class="rounded-lg bg-primary/10 p-2 text-primary"><item.icon class="size-5" /></div>
					<div class="min-w-0 flex-1">
						<h2 class="font-medium">{item.label}</h2>
						<p class="text-sm text-muted-foreground">{item.description}</p>
					</div>
					<ChevronRight class="size-4 shrink-0 text-muted-foreground transition-transform group-hover:translate-x-0.5" />
				</a>
			{/each}
		</div>
	</div>
</div>
