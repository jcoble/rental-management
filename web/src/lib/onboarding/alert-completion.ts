/**
 * The durable personal-alert completion rule shared by the checklist and wizard.
 *
 * The server projection is intentionally represented as a boolean in both surfaces. The
 * preference-object forms are accepted for callers that already hold a saved response;
 * all forms use the same persisted signal: email alerts are enabled for the signed-in user.
 */
export function isPersonalAlertSetupComplete(
	value:
		| boolean
		| { enableEmail?: boolean | null }
		| { hasNotificationEmail?: boolean | null }
		| null
		| undefined
): boolean {
	if (typeof value === 'boolean') return value;
	if (!value) return false;
	if ('enableEmail' in value) return value.enableEmail === true;
	return 'hasNotificationEmail' in value && value.hasNotificationEmail === true;
}
