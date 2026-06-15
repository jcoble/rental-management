/**
 * The Settings deep-link contract, in one dependency-free place so both the Settings page and a guard
 * test can share it (no Svelte/icon imports here).
 *
 * Settings (`/settings`) is tabbed: the active tab is driven by the URL hash. A hash opens a tab when
 * it is either a TAB KEY directly, or a known legacy section-anchor ID mapped to its owning tab. The
 * getting-started checklist + onboarding wizard now deep-link with tab-key hashes, but the wizard's
 * "back to settings" return and old bookmarks still use these in-page anchor IDs — so Settings maps
 * them. This module is the single source of truth for that mapping.
 */

/**
 * Legacy in-page section-anchor IDs → the tab key that owns them. Keep in lock-step with the anchor
 * `id=` attributes in settings/+page.svelte.
 */
export const LEGACY_ANCHOR_TAB: Record<string, string> = {
	'settings-portfolio-basics': 'portfolio',
	'settings-notification-email': 'notifications',
	'settings-notification-delivery': 'automations',
};

/**
 * Resolve a URL hash to the Settings tab it should open, or `null` when it isn't a known tab/anchor.
 * @param hash the hash WITHOUT a leading '#'
 * @param tabKeys the valid tab keys (SETTINGS_SECTIONS.map(s => s.key))
 */
export function resolveSettingsTab(hash: string, tabKeys: readonly string[]): string | null {
	if (!hash) return null;
	if (tabKeys.includes(hash)) return hash;
	return LEGACY_ANCHOR_TAB[hash] ?? null;
}

/** Whether a hash is a legacy section-anchor (vs. a tab key) — those also need a scroll-into-view. */
export function isLegacySettingsAnchor(hash: string): boolean {
	return hash in LEGACY_ANCHOR_TAB;
}
