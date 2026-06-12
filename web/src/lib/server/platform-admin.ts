/**
 * Platform super-admin gate (IA Wave 1, F6 / TSK-212).
 *
 * Engine Health and other platform-operator surfaces must NOT be visible to ordinary
 * landlord Admins. Rental Command has no dedicated super-admin *role* (introducing one
 * means migrations + seeding + assignment UI — out of scope for an IA regroup), so we
 * gate platform pages by a config-listed email allowlist instead.
 *
 * Set PLATFORM_ADMIN_EMAILS to a comma-separated list of emails (case-insensitive).
 * When unset, no one is a platform admin — the super-admin shell is simply unreachable.
 *
 * Read via $env/dynamic/private so the list never leaks into the client bundle; the
 * client only ever receives the resolved boolean for the current user (root layout).
 */

import { env } from '$env/dynamic/private';
import type { User } from '$lib/types/user';

function platformAdminEmails(): string[] {
	return (env.PLATFORM_ADMIN_EMAILS ?? '')
		.split(',')
		.map((e) => e.trim().toLowerCase())
		.filter(Boolean);
}

export function isPlatformAdmin(user: User | null): boolean {
	if (!user?.email) return false;
	const allow = platformAdminEmails();
	if (allow.length === 0) return false;
	return allow.includes(user.email.toLowerCase());
}
