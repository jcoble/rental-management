import assert from 'node:assert/strict';
import { describe, it } from 'node:test';
import { partitionSupportedFiles } from './file-drop.ts';

describe('partitionSupportedFiles', () => {
	it('accepts supported images and PDFs by MIME type', () => {
		const result = partitionSupportedFiles([
			{ name: 'lease.pdf', type: 'application/pdf' },
			{ name: 'receipt.jpg', type: 'image/jpeg' }
		]);

		assert.deepEqual(result.accepted.map((file) => file.name), ['lease.pdf', 'receipt.jpg']);
		assert.deepEqual(result.rejected, []);
	});

	it('accepts common supported extensions when the browser leaves MIME type blank', () => {
		const result = partitionSupportedFiles([
			{ name: 'camera-upload.HEIC', type: '' },
			{ name: 'statement.PDF', type: '' }
		]);

		assert.deepEqual(result.accepted.map((file) => file.name), ['camera-upload.HEIC', 'statement.PDF']);
		assert.deepEqual(result.rejected, []);
	});

	it('rejects unsupported files instead of forwarding them to upload handlers', () => {
		const result = partitionSupportedFiles([
			{ name: 'lease.pdf', type: 'application/pdf' },
			{ name: 'contacts.csv', type: 'text/csv' },
			{ name: 'script.sh', type: '' }
		]);

		assert.deepEqual(result.accepted.map((file) => file.name), ['lease.pdf']);
		assert.deepEqual(result.rejected.map((file) => file.name), ['contacts.csv', 'script.sh']);
	});
});
