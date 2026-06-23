import assert from 'node:assert/strict';
import { describe, it } from 'node:test';

import { scanProcessingCopy, scanUploadCopy } from './scan-copy.ts';

describe('scan document-type copy', () => {
	it('uses application-specific upload and processing copy', () => {
		assert.deepEqual(scanUploadCopy('Application'), {
			title: 'Drop a rental application here',
			helperText: 'or click to browse — PDF, JPG, PNG accepted'
		});

		assert.equal(
			scanProcessingCopy('Application').body,
			'The computer is pulling out applicant, income, and requested-home details for you. This usually takes just a few seconds.'
		);
	});

	it('keeps payment and maintenance copy out of the receipt default', () => {
		assert.equal(scanUploadCopy('Payment').title, 'Drop a rent check or payment receipt here');
		assert.equal(scanUploadCopy('WorkOrder').title, 'Drop a maintenance request, estimate, or repair photo here');
		assert.equal(scanUploadCopy('Expense').title, 'Drop a receipt or invoice here');
	});
});
