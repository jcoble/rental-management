<script lang="ts">
	import { createMutation, createQuery, useQueryClient } from '@tanstack/svelte-query';
	import { ai, type QaTurn, type BriefingBullet, type DocCitation } from '$lib/api/endpoints/ai';
	import { getCurrentPortfolioId } from '$lib/stores/portfolio.svelte';
	import { showError, apiErrorMessage } from '$lib/utils/toast';
	import * as Card from '$lib/components/ui/card';
	import { Button } from '$lib/components/ui/button';
	import { Input } from '$lib/components/ui/input';
	import { Badge } from '$lib/components/ui/badge';
	import { RefreshCw, Sparkles, Send, BookOpen, ArrowUpRight } from '@lucide/svelte';

	const queryClient = useQueryClient();
	const portfolioId = $derived(getCurrentPortfolioId());

	// ── Briefing ──────────────────────────────────────────────────────────────

	const briefingQuery = createQuery(() => ({
		queryKey: ['ai-briefing', portfolioId],
		queryFn: () => ai.briefing(),
	}));

	function refreshBriefing() {
		queryClient.invalidateQueries({ queryKey: ['ai-briefing', portfolioId] });
	}

	function severityBorderClass(severity: BriefingBullet['severity']): string {
		switch (severity) {
			case 'critical': return 'border-l-red-500';
			case 'warning':  return 'border-l-amber-500';
			default:         return 'border-l-blue-400';
		}
	}

	function severityBadgeVariant(_severity: BriefingBullet['severity']): 'default' | 'secondary' | 'destructive' | 'outline' {
		return 'outline';
	}

	function severityBadgeClass(severity: BriefingBullet['severity']): string {
		switch (severity) {
			case 'critical':
				return 'border-red-300 bg-red-50 text-red-700 dark:border-red-700 dark:bg-red-900/20 dark:text-red-300';
			case 'warning':
				return 'border-amber-300 bg-amber-50 text-amber-700 dark:border-amber-700 dark:bg-amber-900/20 dark:text-amber-300';
			default:
				return 'border-blue-300 bg-blue-50 text-blue-700 dark:border-blue-700 dark:bg-blue-900/20 dark:text-blue-300';
		}
	}

	function formatBriefingDate(dateStr: string): string {
		try {
			return new Date(dateStr).toLocaleDateString('en-US', { weekday: 'long', month: 'long', day: 'numeric' });
		} catch {
			return dateStr;
		}
	}

	// ── Ask / Chat ────────────────────────────────────────────────────────────

	let history = $state<QaTurn[]>([]);
	let question = $state('');
	let llmUnavailable = $state(false);

	// Docs citations to show under an assistant turn, keyed by that turn's index in
	// `history`. Kept separate so we don't pollute the QaTurn payload sent to the API.
	let citationsByTurn = $state<Record<number, DocCitation[]>>({});

	const EXAMPLE_PROMPTS = [
		"Who's late on rent?",
		"How much did I collect?",
		"Any leases expiring soon?",
		"What needs maintenance?",
	];

	const askMutation = createMutation(() => ({
		mutationFn: ({ q, hist }: { q: string; hist: QaTurn[] }) => ai.ask(q, hist),
		onSuccess: (data, vars) => {
			const assistantIndex = history.length;
			history = [...history, { role: 'assistant', content: data.answer }];
			if (data.source === 'Docs' && data.citations && data.citations.length > 0) {
				citationsByTurn = { ...citationsByTurn, [assistantIndex]: data.citations };
			}
			if (!data.llmAvailable) {
				llmUnavailable = true;
			}
		},
		onError: (err) => {
			// Remove the optimistic user turn we pushed before calling
			history = history.slice(0, -1);
			showError(apiErrorMessage(err));
		},
	}));

	function submitQuestion() {
		const q = question.trim();
		if (!q || askMutation.isPending) return;
		history = [...history, { role: 'user', content: q }];
		const historyBeforeThisQuestion = history.slice(0, -1);
		question = '';
		askMutation.mutate({ q, hist: historyBeforeThisQuestion });
	}

	function handleKeydown(e: KeyboardEvent) {
		if (e.key === 'Enter' && !e.shiftKey) {
			e.preventDefault();
			submitQuestion();
		}
	}

	function useExamplePrompt(prompt: string) {
		question = prompt;
	}

	/** Turn a raw tool name like `get_financial_summary` into "financial summary". */
	function friendlyToolName(name: string): string {
		return name
			.replace(/^(get_|list_|fetch_)/, '')
			.replace(/_/g, ' ');
	}
</script>

<svelte:head>
	<title>AI Assistant - Rental Command</title>
</svelte:head>

<div class="box-border h-full overflow-y-auto p-6 pb-20" data-testid="ai-page">
	<div class="mb-4 flex items-center gap-2">
		<div class="flex h-8 w-8 items-center justify-center rounded-full bg-primary/10 text-primary">
			<Sparkles class="h-4 w-4" />
		</div>
		<div>
			<h1 class="text-2xl font-bold">AI Assistant</h1>
			<p class="text-sm text-muted-foreground">Your property portfolio, summarised and ready to answer questions.</p>
		</div>
	</div>

	{#if llmUnavailable}
		<div class="mb-4 rounded-lg border border-amber-300 bg-amber-50 px-4 py-3 text-sm text-amber-900 dark:border-amber-700 dark:bg-amber-900/20 dark:text-amber-200" data-testid="ai-unavailable-banner">
			<strong>AI is off</strong> — no API key is configured. Set
			<code class="rounded bg-amber-100 px-1 dark:bg-amber-900/40">Assistant:ApiKey</code>
			to enable the assistant. The daily briefing bullets below still work without it.
		</div>
	{/if}

	<div class="grid gap-6 lg:grid-cols-2">
		<!-- ── Left / Top: Daily Briefing ──────────────────────────────────── -->
		<Card.Root class="flex flex-col gap-0 py-0" data-testid="briefing-card">
			<Card.Header class="flex-row items-center justify-between border-b border-border px-4 py-3 space-y-0">
				<div>
					<Card.Title class="text-base font-semibold">Today's Briefing</Card.Title>
					{#if briefingQuery.data?.date}
						<p class="text-xs text-muted-foreground mt-0.5">{formatBriefingDate(briefingQuery.data.date)}</p>
					{/if}
				</div>
				<Card.Action>
					<Button
						variant="ghost"
						size="icon"
						aria-label="Refresh briefing"
						onclick={refreshBriefing}
						disabled={briefingQuery.isFetching}
						data-testid="briefing-refresh"
					>
						<RefreshCw class="h-4 w-4 {briefingQuery.isFetching ? 'animate-spin' : ''}" />
					</Button>
				</Card.Action>
			</Card.Header>

			<Card.Content class="flex-1 overflow-y-auto p-4">
				{#if briefingQuery.isLoading}
					<div class="flex h-32 items-center justify-center" data-testid="briefing-loading">
						<div class="h-6 w-6 animate-spin rounded-full border-2 border-primary border-t-transparent"></div>
					</div>
				{:else if briefingQuery.isError}
					<p class="text-sm text-destructive" data-testid="briefing-error">
						Could not load briefing. {briefingQuery.error instanceof Error ? briefingQuery.error.message : 'Try refreshing.'}
					</p>
				{:else if briefingQuery.data}
					{@const briefing = briefingQuery.data}

					{#if briefing.summary}
						<div class="mb-4 rounded-md bg-muted/40 px-3 py-2.5" data-testid="briefing-summary">
							<p class="text-sm">{briefing.summary}</p>
							{#if briefing.llmEnhanced}
								<p class="mt-1 text-xs text-muted-foreground">AI summary</p>
							{/if}
						</div>
					{/if}

					{#if briefing.bullets.length === 0}
						<div class="flex flex-col items-center justify-center py-10 text-center" data-testid="briefing-empty">
							<p class="text-sm font-medium text-green-600 dark:text-green-400">All clear — nothing needs attention today.</p>
							<p class="mt-1 text-xs text-muted-foreground">Check back tomorrow for your next briefing.</p>
						</div>
					{:else}
						<ul class="space-y-2" data-testid="briefing-bullets">
							{#each briefing.bullets as bullet, i (i)}
								<li
									class="rounded-md border border-border border-l-4 {severityBorderClass(bullet.severity)} bg-background px-3 py-2.5"
									data-testid="briefing-bullet"
								>
									<div class="flex items-start justify-between gap-2">
										<p class="text-sm font-medium leading-snug">{bullet.title}</p>
										<Badge
											variant={severityBadgeVariant(bullet.severity)}
											class="shrink-0 text-xs capitalize {severityBadgeClass(bullet.severity)}"
										>
											{bullet.severity}
										</Badge>
									</div>
									{#if bullet.detail}
										<p class="mt-0.5 text-xs text-muted-foreground">{bullet.detail}</p>
									{/if}
								</li>
							{/each}
						</ul>
					{/if}
				{/if}
			</Card.Content>
		</Card.Root>

		<!-- ── Right / Bottom: Ask box ─────────────────────────────────────── -->
		<Card.Root class="flex flex-col gap-0 py-0" data-testid="ask-card">
			<Card.Header class="border-b border-border px-4 py-3 space-y-0">
				<Card.Title class="text-base font-semibold">Ask a Question</Card.Title>
				<p class="text-xs text-muted-foreground mt-0.5">Ask anything about your properties, tenants, or finances.</p>
			</Card.Header>

			<!-- Chat history -->
			<Card.Content class="min-h-[200px] flex-1 overflow-y-auto p-4" data-testid="chat-history">
				{#if history.length === 0}
					<div class="flex h-full flex-col items-center justify-center py-6 text-center" data-testid="chat-empty">
						<p class="text-sm text-muted-foreground">Try one of the questions below, or type your own.</p>
						<div class="mt-4 flex flex-wrap justify-center gap-2">
							{#each EXAMPLE_PROMPTS as prompt}
								<button
									type="button"
									class="rounded-full border border-border bg-muted/40 px-3 py-1.5 text-xs text-foreground transition-colors hover:bg-muted focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
									onclick={() => useExamplePrompt(prompt)}
									data-testid="example-prompt"
								>
									{prompt}
								</button>
							{/each}
						</div>
					</div>
				{:else}
					<div class="space-y-3">
						{#each history as turn, i (i)}
							{#if turn.role === 'user'}
								<div class="flex justify-end" data-testid="chat-user-turn">
									<div class="max-w-[85%] rounded-2xl rounded-br-sm bg-primary px-3.5 py-2 text-sm text-primary-foreground">
										{turn.content}
									</div>
								</div>
							{:else}
								<div class="flex justify-start" data-testid="chat-assistant-turn">
									<div class="max-w-[85%] space-y-1">
										<div class="rounded-2xl rounded-bl-sm border border-border bg-muted/40 px-3.5 py-2 text-sm">
											{turn.content}
										</div>
										{#if citationsByTurn[i]?.length}
											<div
												class="rounded-lg border border-border bg-background px-3 py-2"
												data-testid="ai-answer-citations"
											>
												<p class="flex items-center gap-1.5 text-xs font-medium text-muted-foreground">
													<BookOpen class="h-3.5 w-3.5 text-primary" />
													From the docs
												</p>
												<ul class="mt-1.5 space-y-1">
													{#each citationsByTurn[i] as citation (citation.slug)}
														<li>
															<a
																href="/docs/{citation.slug}"
																class="group inline-flex items-center gap-1 text-xs font-medium text-primary hover:underline"
																data-testid="ai-citation-{citation.slug}"
															>
																{citation.title}
																<ArrowUpRight class="h-3 w-3 opacity-70 transition-opacity group-hover:opacity-100" />
															</a>
															<span class="ml-1 text-[11px] text-muted-foreground">· {citation.category}</span>
														</li>
													{/each}
												</ul>
											</div>
										{/if}
										{#if i === history.length - 1 && askMutation.data && (askMutation.data.toolsUsed?.length ?? 0) > 0}
											<p class="px-1 text-xs text-muted-foreground" data-testid="tools-used">
												Looked at: {askMutation.data.toolsUsed.map(friendlyToolName).join(', ')}
												{#if (askMutation.data.tokensUsed ?? 0) > 0}
													· <span class="font-mono tabular-nums">{askMutation.data.tokensUsed.toLocaleString()} tokens</span>
												{/if}
											</p>
										{/if}
									</div>
								</div>
							{/if}
						{/each}

						{#if askMutation.isPending}
							<div class="flex justify-start" data-testid="chat-typing">
								<div class="rounded-2xl rounded-bl-sm border border-border bg-muted/40 px-4 py-3">
									<span class="flex gap-1">
										<span class="h-1.5 w-1.5 animate-bounce rounded-full bg-muted-foreground [animation-delay:0ms]"></span>
										<span class="h-1.5 w-1.5 animate-bounce rounded-full bg-muted-foreground [animation-delay:150ms]"></span>
										<span class="h-1.5 w-1.5 animate-bounce rounded-full bg-muted-foreground [animation-delay:300ms]"></span>
									</span>
								</div>
							</div>
						{/if}
					</div>
				{/if}
			</Card.Content>

			<!-- Input row -->
			<div class="border-t border-border px-4 py-3">
				{#if history.length > 0 && !askMutation.isPending}
					<div class="mb-2 flex flex-wrap gap-1.5">
						{#each EXAMPLE_PROMPTS as prompt}
							<button
								type="button"
								class="rounded-full border border-border bg-muted/40 px-2.5 py-1 text-xs text-foreground transition-colors hover:bg-muted focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring"
								onclick={() => useExamplePrompt(prompt)}
							>
								{prompt}
							</button>
						{/each}
					</div>
				{/if}
				<div class="flex gap-2">
					<Input
						bind:value={question}
						placeholder="Ask anything about your properties…"
						onkeydown={handleKeydown}
						disabled={askMutation.isPending}
						aria-label="Question input"
						data-testid="question-input"
						class="flex-1"
					/>
					<Button
						onclick={submitQuestion}
						disabled={askMutation.isPending || !question.trim()}
						aria-label="Send question"
						data-testid="ask-button"
						size="icon"
					>
						<Send class="h-4 w-4" />
					</Button>
				</div>
			</div>
		</Card.Root>
	</div>
</div>
