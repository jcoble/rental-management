<script lang="ts">
	import { goto } from '$app/navigation';
	import { createMutation } from '@tanstack/svelte-query';
	import { auth } from '$lib/api/endpoints/auth';
	import { portfolios } from '$lib/api/endpoints/portfolios';
	import { setCurrentUser } from '$lib/stores/auth.svelte';
	import { getCurrentPortfolioId, setCurrentPortfolioId } from '$lib/stores/portfolio.svelte';

	let portfolioId = $state(getCurrentPortfolioId());
	let email = $state('');
	let password = $state('');
	let error = $state('');

	let portfolioOptions = $state<{ id: number; name: string }[]>([]);

	async function loadPortfolios() {
		try {
			portfolioOptions = (await portfolios.list()).map((p) => ({ id: p.id, name: p.name }));
			if (!portfolioOptions.find((p) => p.id === portfolioId) && portfolioOptions.length > 0) {
				portfolioId = portfolioOptions[0].id;
			}
		} catch {
			portfolioOptions = [];
		}
	}

	$effect(() => {
		loadPortfolios();
	});

	const loginMutation = createMutation(() => ({
		mutationFn: () => auth.login({ portfolioId, email, password }),
		onSuccess: (result) => {
			setCurrentPortfolioId(result.user.portfolioId);
			setCurrentUser(result.user);
			goto('/portal');
		},
		onError: () => {
			error = 'Invalid credentials. Check email/password and portfolio.';
		},
	}));

	function submit() {
		error = '';
		if (!email.trim() || !password.trim()) {
			error = 'Email and password are required.';
			return;
		}
		loginMutation.mutate();
	}
</script>

<svelte:head>
	<title>Login - Rental Command</title>
</svelte:head>

<div class="flex h-full items-center justify-center bg-bg p-6">
	<div class="w-full max-w-md rounded-xl border border-border bg-surface p-6 shadow-xl">
		<h1 class="mb-1 text-xl font-bold">Portal Login</h1>
		<p class="mb-4 text-sm text-text-secondary">Sign in as admin, manager, agent, owner, or tenant.</p>

		<div class="space-y-3">
			<div>
				<label for="login-portfolio" class="mb-1 block text-xs text-text-tertiary">Portfolio</label>
				<select id="login-portfolio" bind:value={portfolioId} class="w-full rounded border border-border bg-bg px-3 py-2 text-sm">
					{#each portfolioOptions as p}
						<option value={p.id}>{p.name}</option>
					{/each}
				</select>
			</div>
			<div>
				<label for="login-email" class="mb-1 block text-xs text-text-tertiary">Email</label>
				<input id="login-email" type="email" bind:value={email} class="w-full rounded border border-border bg-bg px-3 py-2 text-sm" placeholder="user@example.com" />
			</div>
			<div>
				<label for="login-password" class="mb-1 block text-xs text-text-tertiary">Password</label>
				<input id="login-password" type="password" bind:value={password} class="w-full rounded border border-border bg-bg px-3 py-2 text-sm" placeholder="••••••••" />
			</div>
			<button onclick={submit} class="w-full rounded bg-accent px-3 py-2 text-sm text-white" disabled={loginMutation.isPending}>Sign In</button>
			{#if error}
				<p class="text-sm text-danger">{error}</p>
			{/if}
		</div>
	</div>
</div>
