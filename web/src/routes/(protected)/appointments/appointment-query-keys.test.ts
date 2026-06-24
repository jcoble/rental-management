import assert from 'node:assert/strict';
import { describe, it } from 'node:test';

import { invalidateAppointmentQueries } from './appointment-query-keys.ts';

function createQueryClientSpy() {
	const invalidated: unknown[][] = [];
	return {
		client: {
			invalidateQueries: ({ queryKey }: { queryKey: unknown[] }) => invalidated.push(queryKey),
		},
		invalidated,
	};
}

describe('invalidateAppointmentQueries', () => {
	it('refreshes appointment lists and the app-shell upcoming badge', () => {
		const { client, invalidated } = createQueryClientSpy();

		invalidateAppointmentQueries(client as never, 4);

		assert.deepEqual(invalidated, [
			['appointments', 4],
			['header-upcoming-appointments', 4],
		]);
	});

	it('also refreshes the active detail query when an appointment id is provided', () => {
		const { client, invalidated } = createQueryClientSpy();

		invalidateAppointmentQueries(client as never, 4, 12);

		assert.deepEqual(invalidated, [
			['appointments', 4],
			['header-upcoming-appointments', 4],
			['appointment', 12],
		]);
	});
});
