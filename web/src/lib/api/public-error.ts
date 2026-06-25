export async function readPublicError(response: Response): Promise<string> {
	try {
		const body = await response.json();
		if (body && typeof body === 'object') {
			const b = body as Record<string, unknown>;
			if (typeof b.error === 'string') return b.error;
			if (
				b.error &&
				typeof b.error === 'object' &&
				typeof (b.error as Record<string, unknown>).message === 'string'
			) {
				return (b.error as Record<string, string>).message;
			}
			if (typeof b.detail === 'string') return b.detail;
			if (typeof b.message === 'string') return b.message;
			if (typeof b.title === 'string') return b.title;
		}
	} catch {
		// non-JSON body
	}
	if (response.status === 404) return 'This application link is invalid or has expired.';
	return `Request failed (${response.status}).`;
}
