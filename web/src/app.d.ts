// See https://svelte.dev/docs/kit/types#app.d.ts
// for information about these interfaces

import type { AccessEnvelope, User } from '$lib/types/user';

declare global {
	namespace App {
		// interface Error {}
		interface Locals {
			user: User | null;
			accessToken: string | null;
			accessTokenExpiration?: string;
			access: AccessEnvelope | null;
		}
		// interface PageData {}
		interface PageState {
			unitTab?: string;
			unitView?: string | null;
			unitPaymentId?: number | null;
			unitExpenseId?: number | null;
		}
		// interface Platform {}
	}
}

export {};
