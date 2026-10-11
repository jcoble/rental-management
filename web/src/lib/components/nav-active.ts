/**
 * Which sidebar link is highlighted for the address the user is on.
 *
 * The rule is deliberately one rule: a link matches its own address and anything
 * underneath it, and when two links both match (say "Overview" at /accounting and
 * "Who's behind" at /accounting/past-due) only the more specific one lights up.
 * A few addresses need a hand-written match because their sub-pages belong to a
 * different link, or because the link covers a second address entirely.
 */
function matchesHref(currentPath: string, href: string): boolean {
	if (href === '/') return currentPath === '/';
	// The owner and leasing home pages each have their own sub-page links.
	if (href === '/owner' || href === '/leasing') return currentPath === href;
	if (href === '/units') return currentPath === '/units' || currentPath.startsWith('/units/');
	// Reports also covers the owner statement pages, which sit at their own address.
	if (href === '/reports') {
		return currentPath === '/reports'
			|| currentPath.startsWith('/reports/')
			|| currentPath.startsWith('/owners-report');
	}
	return currentPath === href || currentPath.startsWith(href + '/');
}

/**
 * True when this link is the one to highlight: it matches the current address and
 * no longer link in the sidebar matches it too.
 */
export function isNavItemActive(currentPath: string, href: string, allHrefs: string[]): boolean {
	if (!matchesHref(currentPath, href)) return false;
	return !allHrefs.some(
		(other) => other.length > href.length && matchesHref(currentPath, other)
	);
}
