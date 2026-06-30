import assert from 'node:assert/strict';
import { describe, it } from 'node:test';

import { getLeaseDeleteState } from './lease-delete-state.ts';

describe('lease delete state', () => {
	it('explains active lease removal before the destructive action', () => {
		assert.deepEqual(
			getLeaseDeleteState({
				leaseNumber: 'RC-2A-2026',
				status: 'Active',
				propertyName: 'Riverside Courtyard',
				unitNumber: '2A',
			}),
			{
				title: 'Remove active lease',
				message:
					'RC-2A-2026 is active for Riverside Courtyard - Unit 2A. Removing it will terminate this lease record, release the unit from active occupancy, and hide it from active lease workflows. Use Create / Send notice for a normal move-out; remove only duplicate or mistaken leases.',
				confirmLabel: 'Remove active lease',
			}
		);
	});

	it('uses discard copy for pending signature leases', () => {
		assert.deepEqual(getLeaseDeleteState({ leaseNumber: 'DRAFT-7', status: 'PendingSignature' }), {
			title: 'Delete pending lease',
			message:
				'DRAFT-7 is pending signature. Deleting it will remove this lease from signing and rent workflows. Continue only if the pending agreement should be discarded.',
			confirmLabel: 'Delete pending lease',
		});
	});

	it('keeps simple destructive copy for draft leases', () => {
		assert.deepEqual(getLeaseDeleteState({ leaseNumber: 'DRAFT-1', status: 'Draft' }), {
			title: 'Delete draft lease',
			message: 'Delete draft lease DRAFT-1? This cannot be undone.',
			confirmLabel: 'Delete draft',
		});
	});
});
