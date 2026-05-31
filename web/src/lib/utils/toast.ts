/**
 * Toast wrapper enforcing consistent duration defaults (mirrors EdiPlatform).
 * Error/warning toasts persist until the user dismisses them; success/info
 * auto-dismiss after 4s. Also exposes {@link apiErrorMessage} to turn an
 * {@link ApiError} (or anything thrown by the fetch client) into a single
 * human-readable string suitable for {@link showError}.
 */

import { toast, type ExternalToast } from 'svelte-sonner';
import { ApiError } from '$lib/api/client';

/** Error toast — persists until the user clicks X. */
export function showError(message: string, options?: ExternalToast) {
	return toast.error(message, { duration: Infinity, ...options });
}

/** Warning toast — persists until the user clicks X. */
export function showWarning(message: string, options?: ExternalToast) {
	return toast.warning(message, { duration: Infinity, ...options });
}

/** Success toast — auto-dismisses after 4s. */
export function showSuccess(message: string, options?: ExternalToast) {
	return toast.success(message, { duration: 4000, ...options });
}

/** Info toast — auto-dismisses after 4s. */
export function showInfo(message: string, options?: ExternalToast) {
	return toast.info(message, { duration: 4000, ...options });
}

/**
 * Reduce any thrown error into a user-facing message. Prefers ASP.NET
 * ValidationProblemDetails field messages, then the structured error message,
 * then a generic fallback.
 */
export function apiErrorMessage(error: unknown, fallback = 'Something went wrong.'): string {
	if (error instanceof ApiError) {
		if (error.validationErrors) {
			const first = Object.values(error.validationErrors).flat().find(Boolean);
			if (first) return first;
		}
		return error.message || fallback;
	}
	if (error instanceof Error && error.message) return error.message;
	return fallback;
}
