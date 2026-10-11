import assert from 'node:assert/strict';
import { describe, it } from 'node:test';
import { auditChangeLabel, auditEntryTitle } from './audit-change-label.ts';

describe('audit change labels', () => {
	it('hides timestamps the landlord never asked about', () => {
		assert.equal(auditChangeLabel('Updated at utc'), null);
		assert.equal(auditChangeLabel('UpdatedAtUtc'), null);
		assert.equal(auditChangeLabel('CreatedAt'), null);
		assert.equal(auditChangeLabel('Created at'), null);
		assert.equal(auditChangeLabel('Signed at utc'), null);
	});

	it('hides internal record numbers', () => {
		assert.equal(auditChangeLabel('Issued artifact id'), null);
		assert.equal(auditChangeLabel('ExecutedArtifactId'), null);
		assert.equal(auditChangeLabel('Possession agreement exception authorized by user id'), null);
		assert.equal(auditChangeLabel('PortfolioId'), null);
		assert.equal(auditChangeLabel('Id'), null);
	});

	it('hides row bookkeeping columns', () => {
		assert.equal(auditChangeLabel('Row version'), null);
		assert.equal(auditChangeLabel('RowVersion'), null);
		assert.equal(auditChangeLabel('ConcurrencyStamp'), null);
	});

	it('turns the remaining field names into landlord words', () => {
		assert.equal(auditChangeLabel('Base rent amount'), 'Base rent amount');
		assert.equal(auditChangeLabel('BaseRentAmount'), 'Base rent amount');
		assert.equal(auditChangeLabel('Possession agreement exception reason'), 'Lease override reason');
		assert.equal(auditChangeLabel('Lease agreement number'), 'Lease number');
		assert.equal(auditChangeLabel('Ending disposition'), 'How the lease ended');
		assert.equal(auditChangeLabel('Lifecycle'), 'Status');
		assert.equal(auditChangeLabel('LeaseLifecycle'), 'Lease status');
	});

	it('ignores blank field names', () => {
		assert.equal(auditChangeLabel(''), null);
		assert.equal(auditChangeLabel('   '), null);
	});
});

describe('audit entry titles', () => {
	it('replaces internal record names with landlord words', () => {
		assert.equal(
			auditEntryTitle('Updated tenant and lease relationship'),
			'Updated lease participant'
		);
		assert.equal(auditEntryTitle('Created lease agreement'), 'Created lease');
	});

	it('leaves plain titles alone', () => {
		assert.equal(auditEntryTitle('Created payment'), 'Created payment');
	});
});
