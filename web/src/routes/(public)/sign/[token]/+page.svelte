<script lang="ts">
	import {
		submitSignature,
		declineSignature,
		documentUrlFor,
		SignApiError,
		type SignActionResponse,
		type SignPackageResponse
	} from '$lib/api/endpoints/sign';
	import * as Dialog from '$lib/components/ui/dialog';
	import {
		FileText,
		Download,
		CheckCircle2,
		Loader2,
		AlertCircle,
		LinkIcon,
		PenLine,
		Type,
		Eraser,
		ShieldCheck,
		XCircle
	} from '@lucide/svelte';
	import type { PageData } from './$types';
	import BrandMark from '$lib/components/BrandMark.svelte';

	let { data }: { data: PageData } = $props();

	const token = $derived(data.token);

	// ── Terminal/landing-state flags from the load result ───────────────────────
	const isInvalid = $derived(data.state === 'invalid');
	const isExpired = $derived(data.state === 'expired');
	const pkg = $derived<SignPackageResponse | null>(data.state === 'ok' ? data.pkg : null);

	// The browser loads the PDF same-origin (proxy) so no auth header is needed.
	const documentUrl = $derived(token ? documentUrlFor(token) : '');

	// ── Whether the loaded package is already in a terminal/read-only state ──────
	// If the signer already signed/declined, or the whole request is finished,
	// we show a read-only confirmation instead of the signing form.
	function isTerminalStatus(p: SignPackageResponse | null): boolean {
		if (!p) return false;
		if (p.alreadySigned) return true;
		const signer = (p.signerStatus ?? '').toLowerCase();
		if (signer === 'signed' || signer === 'declined') return true;
		const req = (p.requestStatus ?? '').toLowerCase();
		return req === 'completed' || req === 'declined' || req === 'voided';
	}

	// ── Live action result (after the signer signs / declines on this page) ─────
	let actionResult = $state<SignActionResponse | null>(null);

	// Effective status: the action result wins once we have one; otherwise the
	// loaded package. This drives whether the form or a terminal screen shows.
	const effectiveSignerStatus = $derived(
		(actionResult?.signerStatus ?? pkg?.signerStatus ?? '').toLowerCase()
	);
	const effectiveRequestStatus = $derived(
		(actionResult?.requestStatus ?? pkg?.requestStatus ?? '').toLowerCase()
	);

	const justSigned = $derived(effectiveSignerStatus === 'signed');
	const justDeclined = $derived(effectiveSignerStatus === 'declined');
	const requestCompleted = $derived(
		actionResult?.requestCompleted === true || effectiveRequestStatus === 'completed'
	);

	// Show the read-only terminal screen when the package loaded terminal, OR once
	// the signer has signed/declined here.
	const showTerminal = $derived(
		actionResult != null || isTerminalStatus(pkg)
	);

	// ── Consent ─────────────────────────────────────────────────────────────────
	let consent = $state(false);

	// ── Signature capture: Typed vs Drawn ───────────────────────────────────────
	let signatureType = $state<'Typed' | 'Drawn'>('Typed');
	let typedName = $state('');

	// Pre-fill the typed name from the signer's known name once the package loads.
	$effect(() => {
		if (pkg?.signerName && typedName === '') {
			typedName = pkg.signerName;
		}
	});

	// Canvas drawing state.
	let canvasEl = $state<HTMLCanvasElement | null>(null);
	let hasDrawing = $state(false);
	let drawing = false;
	let lastX = 0;
	let lastY = 0;

	function setupCanvas(node: HTMLCanvasElement) {
		canvasEl = node;
		// Size the backing store to the displayed size * DPR for crisp lines.
		const resize = () => {
			const ratio = Math.max(window.devicePixelRatio || 1, 1);
			const rect = node.getBoundingClientRect();
			// Preserve any existing drawing across a resize.
			const prev = hasDrawing ? node.toDataURL('image/png') : null;
			node.width = Math.max(1, Math.round(rect.width * ratio));
			node.height = Math.max(1, Math.round(rect.height * ratio));
			const ctx = node.getContext('2d');
			if (ctx) {
				ctx.scale(ratio, ratio);
				ctx.lineWidth = 2.5;
				ctx.lineCap = 'round';
				ctx.lineJoin = 'round';
				ctx.strokeStyle = '#0f172a';
			}
			if (prev && ctx) {
				const img = new Image();
				img.onload = () => ctx.drawImage(img, 0, 0, rect.width, rect.height);
				img.src = prev;
			}
		};
		resize();
		window.addEventListener('resize', resize);
		return {
			destroy() {
				window.removeEventListener('resize', resize);
			}
		};
	}

	function pointerPos(e: PointerEvent): { x: number; y: number } {
		const rect = canvasEl!.getBoundingClientRect();
		return { x: e.clientX - rect.left, y: e.clientY - rect.top };
	}

	function startDraw(e: PointerEvent) {
		if (!canvasEl) return;
		e.preventDefault();
		canvasEl.setPointerCapture?.(e.pointerId);
		drawing = true;
		const { x, y } = pointerPos(e);
		lastX = x;
		lastY = y;
		// A single tap should leave a visible dot.
		const ctx = canvasEl.getContext('2d');
		if (ctx) {
			ctx.beginPath();
			ctx.arc(x, y, 1.25, 0, Math.PI * 2);
			ctx.fillStyle = '#0f172a';
			ctx.fill();
		}
		hasDrawing = true;
	}

	function moveDraw(e: PointerEvent) {
		if (!drawing || !canvasEl) return;
		e.preventDefault();
		const { x, y } = pointerPos(e);
		const ctx = canvasEl.getContext('2d');
		if (ctx) {
			ctx.beginPath();
			ctx.moveTo(lastX, lastY);
			ctx.lineTo(x, y);
			ctx.stroke();
		}
		lastX = x;
		lastY = y;
		hasDrawing = true;
	}

	function endDraw(e: PointerEvent) {
		if (!drawing) return;
		e.preventDefault();
		drawing = false;
		canvasEl?.releasePointerCapture?.(e.pointerId);
	}

	function clearCanvas() {
		if (!canvasEl) return;
		const ctx = canvasEl.getContext('2d');
		if (ctx) ctx.clearRect(0, 0, canvasEl.width, canvasEl.height);
		hasDrawing = false;
	}

	// ── Validity: can the user sign? ────────────────────────────────────────────
	const hasSignature = $derived(
		signatureType === 'Typed' ? typedName.trim().length > 0 : hasDrawing
	);
	const canSign = $derived(consent && hasSignature);

	// ── Submit ──────────────────────────────────────────────────────────────────
	let submitting = $state(false);
	let submitError = $state<string | null>(null);

	async function handleSign() {
		if (!canSign || submitting) return;
		submitError = null;
		submitting = true;
		try {
			const body =
				signatureType === 'Typed'
					? { consent, signatureType: 'Typed' as const, typedName: typedName.trim() }
					: {
							consent,
							signatureType: 'Drawn' as const,
							drawnImage: canvasEl?.toDataURL('image/png') ?? ''
						};
			actionResult = await submitSignature(token, body);
			if (typeof window !== 'undefined') window.scrollTo({ top: 0, behavior: 'smooth' });
		} catch (err) {
			submitError =
				err instanceof SignApiError || err instanceof Error
					? err.message
					: 'Something went wrong. Please try again.';
		} finally {
			submitting = false;
		}
	}

	// ── Decline ─────────────────────────────────────────────────────────────────
	let declineOpen = $state(false);
	let declineReason = $state('');
	let declining = $state(false);
	let declineError = $state<string | null>(null);

	async function handleDecline() {
		if (declining) return;
		declineError = null;
		declining = true;
		try {
			actionResult = await declineSignature(token, {
				reason: declineReason.trim() || undefined
			});
			declineOpen = false;
			if (typeof window !== 'undefined') window.scrollTo({ top: 0, behavior: 'smooth' });
		} catch (err) {
			declineError =
				err instanceof SignApiError || err instanceof Error
					? err.message
					: 'Something went wrong. Please try again.';
		} finally {
			declining = false;
		}
	}

	// Friendly label for a pre-existing terminal status (when loaded terminal).
	const terminalLoadedLabel = $derived.by(() => {
		if (!pkg) return '';
		const signer = (pkg.signerStatus ?? '').toLowerCase();
		if (signer === 'declined') return 'declined';
		const req = (pkg.requestStatus ?? '').toLowerCase();
		if (req === 'voided') return 'voided';
		return 'signed';
	});
</script>

<svelte:head>
	<title>{pkg ? `Sign ${pkg.subject ?? pkg.documentName}` : 'Sign document'} · Rental Command</title>
	<meta name="robots" content="noindex" />
	<meta name="viewport" content="width=device-width, initial-scale=1" />
</svelte:head>

<!-- Own scroll container: html/body are overflow:hidden globally, so this standalone public
     page must scroll itself. -->
<div class="flex h-dvh flex-col overflow-y-auto bg-muted/30 text-foreground" data-testid="sign-page">
	<!-- Branded header -->
	<header class="border-b border-border/60 bg-background/90 backdrop-blur">
		<div class="mx-auto flex h-16 max-w-3xl items-center gap-2 px-4 sm:px-6">
			<BrandMark class="h-8 w-8 shadow-sm" />
			<span class="text-base font-semibold tracking-tight">Rental Command</span>
			<span class="ml-auto hidden items-center gap-1.5 text-xs font-medium text-muted-foreground sm:inline-flex">
				<ShieldCheck class="h-3.5 w-3.5 text-success" />
				Secure e-signature
			</span>
		</div>
	</header>

	<main class="mx-auto w-full max-w-3xl flex-1 px-4 py-6 sm:px-6 sm:py-10">
		{#if isInvalid}
			<!-- 404 — unknown / garbled link -->
			<div
				class="rounded-2xl border border-border bg-card p-8 text-center shadow-sm"
				data-testid="sign-invalid"
			>
				<div class="mx-auto mb-4 flex h-14 w-14 items-center justify-center rounded-full bg-muted">
					<LinkIcon class="h-7 w-7 text-muted-foreground" />
				</div>
				<h1 class="text-xl font-bold">This signing link isn't valid</h1>
				<p class="mx-auto mt-2 max-w-md text-sm text-muted-foreground">
					The link may be incomplete or mistyped. Please open the signing link directly from the
					email or message you received, or contact the sender for a new one.
				</p>
			</div>
		{:else if isExpired}
			<!-- 410 — expired / already used / finalized -->
			<div
				class="rounded-2xl border border-border bg-card p-8 text-center shadow-sm"
				data-testid="sign-expired"
			>
				<div class="mx-auto mb-4 flex h-14 w-14 items-center justify-center rounded-full bg-muted">
					<AlertCircle class="h-7 w-7 text-muted-foreground" />
				</div>
				<h1 class="text-xl font-bold">This signing link is no longer active</h1>
				<p class="mx-auto mt-2 max-w-md text-sm text-muted-foreground">
					This link has expired or has already been used. If you still need to sign, please ask the
					sender to send you a new link.
				</p>
			</div>
		{:else if pkg}
			{#if showTerminal}
				<!-- Terminal / read-only states (signed here, declined here, or loaded terminal) -->
				{#if justDeclined || (terminalLoadedLabel === 'declined' && !justSigned)}
					<!-- Declined -->
					<div
						class="rounded-2xl border border-border bg-card p-8 text-center shadow-sm"
						data-testid="sign-success"
					>
						<div class="mx-auto mb-4 flex h-16 w-16 items-center justify-center rounded-full bg-muted">
							<XCircle class="h-9 w-9 text-muted-foreground" />
						</div>
						<h1 class="text-2xl font-bold">You declined to sign</h1>
						<p class="mx-auto mt-3 max-w-md text-base text-muted-foreground">
							We've let {pkg.senderName || 'the sender'} know that you declined to sign
							{pkg.subject ? `“${pkg.subject}”` : 'this document'}. You can close this page now.
						</p>
					</div>
				{:else}
					<!-- Signed (fully completed or partially — your part is done) -->
					<div
						class="rounded-2xl border border-border bg-card p-8 text-center shadow-sm"
						data-testid="sign-success"
					>
						<div
							class="mx-auto mb-4 flex h-16 w-16 items-center justify-center rounded-full bg-success/15"
						>
							<CheckCircle2 class="h-9 w-9 text-success" />
						</div>
						{#if requestCompleted}
							<h1 class="text-2xl font-bold">Signed — all done</h1>
							<p class="mx-auto mt-3 max-w-md text-base text-muted-foreground">
								Thank you, {pkg.signerName || 'there'}. {pkg.subject ?? pkg.documentName} is fully signed.
								A completed copy will be sent to <span class="font-medium text-foreground">{pkg.signerEmail}</span>.
							</p>
						{:else}
							<h1 class="text-2xl font-bold">Thanks — your signature is recorded</h1>
							<p class="mx-auto mt-3 max-w-md text-base text-muted-foreground">
								Thank you, {pkg.signerName || 'there'}. We've recorded your signature on
								{pkg.subject ? `“${pkg.subject}”` : 'this document'}. We're now waiting on the other
								signer(s); you'll receive a completed copy at
								<span class="font-medium text-foreground">{pkg.signerEmail}</span> once everyone has signed.
							</p>
						{/if}
						<a
							href={documentUrl}
							target="_blank"
							rel="noopener"
							class="mt-6 inline-flex h-11 items-center justify-center gap-2 rounded-lg border border-input bg-background px-5 text-sm font-medium transition-colors hover:bg-accent"
						>
							<Download class="h-4 w-4" />
							Open the document
						</a>
					</div>
				{/if}
			{:else}
				<!-- ── Active signing experience ───────────────────────────────────── -->
				<header class="mb-6 text-center">
					<h1 class="text-2xl font-bold sm:text-3xl">
						{pkg.senderName || 'Someone'} sent you
						{pkg.subject ? `“${pkg.subject}”` : pkg.documentName} to sign
					</h1>
					<p class="mt-2 text-sm text-muted-foreground">
						Signing as <span class="font-medium text-foreground">{pkg.signerName}</span>
						{#if pkg.signerEmail}
							· {pkg.signerEmail}
						{/if}
					</p>
				</header>

				<!-- Document preview -->
				<section class="mb-6 overflow-hidden rounded-2xl border border-border bg-card shadow-sm">
					<div class="flex items-center justify-between gap-2 border-b border-border px-4 py-3">
						<div class="flex min-w-0 items-center gap-2">
							<FileText class="h-4 w-4 shrink-0 text-muted-foreground" />
							<span class="truncate text-sm font-medium">{pkg.documentName}</span>
						</div>
						<a
							href={documentUrl}
							target="_blank"
							rel="noopener"
							class="inline-flex shrink-0 items-center gap-1.5 text-xs font-medium text-primary hover:underline"
							data-testid="sign-document-open"
						>
							<Download class="h-3.5 w-3.5" />
							Open / download PDF
						</a>
					</div>
					<object
						data={documentUrl}
						type="application/pdf"
						title={pkg.documentName}
						class="h-[55vh] w-full bg-muted/30 sm:h-[70vh]"
					>
						<!-- Fallback when inline PDF rendering isn't supported (some mobile browsers). -->
						<div class="flex flex-col items-center gap-3 px-6 py-12 text-center">
							<FileText class="h-8 w-8 text-muted-foreground" />
							<p class="text-sm text-muted-foreground">
								Your browser can't show the document inline.
							</p>
							<a
								href={documentUrl}
								target="_blank"
								rel="noopener"
								class="inline-flex h-11 items-center justify-center gap-2 rounded-lg bg-primary px-5 text-sm font-semibold text-primary-foreground hover:opacity-90"
							>
								<Download class="h-4 w-4" />
								Open the document
							</a>
						</div>
					</object>
				</section>

				<!-- ESIGN / UETA consent -->
				<section
					class="mb-6 rounded-2xl border border-border bg-card p-5 shadow-sm"
					data-testid="sign-consent-section"
				>
					<h2 class="text-base font-semibold">Consent to sign electronically</h2>
					<div
						class="mt-3 max-h-44 overflow-y-auto rounded-xl border border-border/70 bg-muted/30 p-4 text-sm leading-relaxed text-muted-foreground"
					>
						<p class="whitespace-pre-line">{pkg.consentDisclosure}</p>
					</div>
					<label class="mt-4 flex cursor-pointer items-start gap-3">
						<input
							type="checkbox"
							bind:checked={consent}
							class="mt-0.5 h-5 w-5 shrink-0 rounded border-input text-primary focus:ring-2 focus:ring-ring/40"
							data-testid="sign-consent"
						/>
						<span class="text-sm font-medium leading-relaxed text-foreground">
							I have read the disclosure above and I agree to sign electronically and to use
							electronic records for this transaction.
						</span>
					</label>
				</section>

				<!-- Signature capture -->
				<section class="mb-6 rounded-2xl border border-border bg-card p-5 shadow-sm">
					<h2 class="text-base font-semibold">Your signature</h2>

					<!-- Typed / Drawn toggle -->
					<div class="mt-3 inline-flex rounded-xl border border-border bg-muted/40 p-1">
						<button
							type="button"
							class="inline-flex items-center gap-1.5 rounded-lg px-4 py-2 text-sm font-medium transition-colors {signatureType ===
							'Typed'
								? 'bg-background text-foreground shadow-sm'
								: 'text-muted-foreground hover:text-foreground'}"
							onclick={() => (signatureType = 'Typed')}
							data-testid="sign-type-typed"
						>
							<Type class="h-4 w-4" />
							Type it
						</button>
						<button
							type="button"
							class="inline-flex items-center gap-1.5 rounded-lg px-4 py-2 text-sm font-medium transition-colors {signatureType ===
							'Drawn'
								? 'bg-background text-foreground shadow-sm'
								: 'text-muted-foreground hover:text-foreground'}"
							onclick={() => (signatureType = 'Drawn')}
							data-testid="sign-type-drawn"
						>
							<PenLine class="h-4 w-4" />
							Draw it
						</button>
					</div>

					{#if signatureType === 'Typed'}
						<div class="mt-4">
							<label class="block">
								<span class="mb-1.5 block text-sm font-medium text-foreground">Full legal name</span>
								<input
									type="text"
									bind:value={typedName}
									placeholder="Type your full name"
									autocomplete="name"
									class="h-12 w-full rounded-xl border border-input bg-background px-3 text-base text-foreground placeholder:text-muted-foreground focus:border-ring focus:outline-none focus:ring-2 focus:ring-ring/40"
									data-testid="sign-typed-input"
								/>
							</label>
							<!-- Signature-style preview -->
							<div
								class="mt-3 flex min-h-[5rem] items-center rounded-xl border border-dashed border-border bg-muted/20 px-4"
							>
								<span
									class="signature-script text-3xl text-foreground sm:text-4xl"
									data-testid="sign-typed-preview"
								>
									{typedName || 'Your signature'}
								</span>
							</div>
						</div>
					{:else}
						<div class="mt-4">
							<div
								class="relative overflow-hidden rounded-xl border border-dashed border-border bg-background"
							>
								<canvas
									use:setupCanvas
									class="block h-44 w-full touch-none"
									onpointerdown={startDraw}
									onpointermove={moveDraw}
									onpointerup={endDraw}
									onpointerleave={endDraw}
									onpointercancel={endDraw}
									data-testid="sign-canvas"
								></canvas>
								{#if !hasDrawing}
									<span
										class="pointer-events-none absolute inset-0 flex items-center justify-center text-sm text-muted-foreground"
									>
										Sign here with your finger or mouse
									</span>
								{/if}
							</div>
							<button
								type="button"
								onclick={clearCanvas}
								class="mt-2 inline-flex items-center gap-1.5 text-sm font-medium text-muted-foreground hover:text-foreground"
								data-testid="sign-clear"
							>
								<Eraser class="h-4 w-4" />
								Clear
							</button>
						</div>
					{/if}
				</section>

				{#if submitError}
					<div
						class="mb-4 rounded-xl border border-destructive/30 bg-destructive/10 p-4 text-sm text-destructive"
						data-testid="sign-submit-error"
					>
						{submitError}
					</div>
				{/if}

				<!-- Actions -->
				<div class="flex flex-col gap-3 sm:flex-row-reverse">
					<button
						type="button"
						onclick={handleSign}
						disabled={!canSign || submitting}
						class="inline-flex h-14 flex-1 items-center justify-center gap-2 rounded-2xl bg-primary text-base font-semibold text-primary-foreground transition-opacity hover:opacity-90 disabled:cursor-not-allowed disabled:opacity-50"
						data-testid="sign-submit"
					>
						{#if submitting}
							<Loader2 class="h-5 w-5 animate-spin" />
							Signing…
						{:else}
							<PenLine class="h-5 w-5" />
							Sign {pkg.subject ? `“${pkg.subject}”` : 'document'}
						{/if}
					</button>
					<button
						type="button"
						onclick={() => (declineOpen = true)}
						disabled={submitting}
						class="inline-flex h-14 items-center justify-center rounded-2xl border border-input bg-background px-6 text-base font-medium text-muted-foreground transition-colors hover:bg-accent disabled:opacity-50 sm:flex-none"
						data-testid="sign-decline"
					>
						Decline to sign
					</button>
				</div>
				{#if !canSign}
					<p class="mt-3 text-center text-xs text-muted-foreground">
						{#if !consent}
							Check the box above to agree to sign electronically.
						{:else}
							Add your signature to continue.
						{/if}
					</p>
				{/if}
			{/if}
		{/if}
	</main>

	<footer class="border-t border-border/60 py-6 text-center text-xs text-muted-foreground">
		Secured by <span class="font-medium text-foreground">Rental Command</span> · Your signature is
		legally binding under the ESIGN Act.
	</footer>
</div>

<!-- Decline dialog -->
<Dialog.Root bind:open={declineOpen}>
	<Dialog.Content class="sm:max-w-md" data-testid="sign-decline-dialog">
		<Dialog.Header>
			<Dialog.Title>Decline to sign?</Dialog.Title>
			<Dialog.Description>
				You won't sign this document. You can optionally tell {pkg?.senderName || 'the sender'} why.
			</Dialog.Description>
		</Dialog.Header>
		<div class="py-2">
			<label class="block">
				<span class="mb-1.5 block text-sm font-medium text-foreground">Reason (optional)</span>
				<textarea
					bind:value={declineReason}
					rows="3"
					placeholder="Add a short note…"
					class="w-full rounded-xl border border-input bg-background px-3 py-2.5 text-base text-foreground placeholder:text-muted-foreground focus:border-ring focus:outline-none focus:ring-2 focus:ring-ring/40"
					data-testid="sign-decline-reason"
				></textarea>
			</label>
			{#if declineError}
				<p class="mt-2 text-sm text-destructive" data-testid="sign-decline-error">{declineError}</p>
			{/if}
		</div>
		<Dialog.Footer class="gap-2">
			<button
				type="button"
				onclick={() => (declineOpen = false)}
				disabled={declining}
				class="inline-flex h-11 items-center justify-center rounded-lg border border-input bg-background px-5 text-sm font-medium transition-colors hover:bg-accent disabled:opacity-50"
			>
				Go back
			</button>
			<button
				type="button"
				onclick={handleDecline}
				disabled={declining}
				class="inline-flex h-11 items-center justify-center gap-2 rounded-lg bg-destructive px-5 text-sm font-semibold text-destructive-foreground transition-opacity hover:opacity-90 disabled:opacity-50"
				data-testid="sign-decline-confirm"
			>
				{#if declining}
					<Loader2 class="h-4 w-4 animate-spin" />
					Declining…
				{:else}
					Decline to sign
				{/if}
			</button>
		</Dialog.Footer>
	</Dialog.Content>
</Dialog.Root>

<style>
	/* Cursive signature preview for the typed-name option. The web-safe cursive
	   stack keeps it readable on every device without bundling a font. */
	.signature-script {
		font-family: 'Brush Script MT', 'Segoe Script', 'Snell Roundhand', cursive;
		font-style: italic;
	}
</style>
