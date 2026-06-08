<script lang="ts">
	import { onMount } from 'svelte';

	let {
		selector = '[data-m3-tooltip]',
		'data-testid': dataTestId = 'm3-hover-tooltip'
	}: {
		selector?: string;
		'data-testid'?: string;
	} = $props();

	let activeTooltip: HTMLElement | null = null;
	let activeTarget: HTMLElement | null = null;
	let showTimer: ReturnType<typeof setTimeout> | null = null;

	function clearTimer() {
		if (!showTimer) return;
		clearTimeout(showTimer);
		showTimer = null;
	}

	function hideTooltip() {
		clearTimer();
		activeTooltip?.remove();
		activeTooltip = null;
		activeTarget = null;
	}

	function positionTooltip(target: HTMLElement, tooltip: HTMLElement) {
		const rect = target.getBoundingClientRect();
		const margin = 12;
		tooltip.style.left = `${rect.left + rect.width / 2}px`;
		tooltip.style.top = `${rect.bottom + 8}px`;
		tooltip.style.transform = 'translateX(-50%)';

		const tipRect = tooltip.getBoundingClientRect();
		if (tipRect.right > window.innerWidth - margin) {
			tooltip.style.left = `${window.innerWidth - margin - tipRect.width / 2}px`;
		}
		if (tipRect.left < margin) {
			tooltip.style.left = `${margin + tipRect.width / 2}px`;
		}
		if (tipRect.bottom > window.innerHeight - margin) {
			tooltip.style.top = `${rect.top - tipRect.height - 8}px`;
		}
	}

	function showTooltip(target: HTMLElement) {
		const text = target.dataset.m3Tooltip;
		if (!text) return;
		hideTooltip();

		const tooltip = document.createElement('div');
		tooltip.className = 'm3-tooltip-content';
		tooltip.setAttribute('role', 'tooltip');
		tooltip.setAttribute('data-testid', dataTestId);
		tooltip.textContent = text;
		tooltip.style.position = 'fixed';
		tooltip.style.pointerEvents = 'none';
		document.body.appendChild(tooltip);
		positionTooltip(target, tooltip);
		activeTooltip = tooltip;
		activeTarget = target;
	}

	function getTarget(event: Event): HTMLElement | null {
		if (!(event.target instanceof Element)) return null;
		return event.target.closest<HTMLElement>(selector);
	}

	function scheduleShow(event: Event) {
		const target = getTarget(event);
		if (!target || target === activeTarget) return;
		clearTimer();
		showTimer = setTimeout(() => showTooltip(target), 300);
	}

	function handleOut(event: MouseEvent | FocusEvent) {
		const target = getTarget(event);
		if (!target) return;
		const relatedTarget = event instanceof MouseEvent ? event.relatedTarget : null;
		if (relatedTarget instanceof Node && target.contains(relatedTarget)) return;
		hideTooltip();
	}

	onMount(() => {
		document.addEventListener('mouseover', scheduleShow);
		document.addEventListener('focusin', scheduleShow);
		document.addEventListener('mouseout', handleOut);
		document.addEventListener('focusout', handleOut);
		window.addEventListener('scroll', hideTooltip, true);
		window.addEventListener('resize', hideTooltip);

		return () => {
			document.removeEventListener('mouseover', scheduleShow);
			document.removeEventListener('focusin', scheduleShow);
			document.removeEventListener('mouseout', handleOut);
			document.removeEventListener('focusout', handleOut);
			window.removeEventListener('scroll', hideTooltip, true);
			window.removeEventListener('resize', hideTooltip);
			hideTooltip();
		};
	});
</script>
