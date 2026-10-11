export async function resolve(specifier, context, nextResolve) {
	if (specifier === '$lib/api/server-fetch') {
		return {
			url: new URL('../../lib/api/server-fetch.ts', import.meta.url).href,
			shortCircuit: true
		};
	}

	if (specifier === '$lib/server/config') {
		return {
			url: 'data:text/javascript,export%20const%20SERVER_API_BASE_URL%20%3D%20%22https%3A%2F%2Fapi.test%2Fapi%2Fv1%22%3B',
			shortCircuit: true
		};
	}

	return nextResolve(specifier, context);
}
