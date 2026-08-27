import { describe, it, expect } from 'vitest';
import { ownerSchema } from '$lib/schemas';

describe('ownerSchema', () => {
	it('rejects a phone longer than the server limit', () => {
		const result = ownerSchema.safeParse({
			name: 'Oak Street Holdings',
			ownerEntityType: 'LLC',
			phone: '1'.repeat(51)
		});

		expect(result.success).toBe(false);
	});

	it('accepts an empty optional phone', () => {
		const result = ownerSchema.safeParse({
			name: 'Oak Street Holdings',
			ownerEntityType: 'LLC',
			phone: ''
		});

		expect(result.success).toBe(true);
	});
});
