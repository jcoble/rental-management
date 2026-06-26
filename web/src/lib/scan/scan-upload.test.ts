import assert from 'node:assert/strict';
import { describe, it } from 'node:test';

import { prepareScanDocumentUpload } from './scan-upload.ts';

describe('prepareScanDocumentUpload', () => {
	it('keeps a single uploaded document unchanged', async () => {
		const pdf = new File(['lease pdf bytes'], 'lease.pdf', { type: 'application/pdf' });
		let stitched = false;

		const upload = await prepareScanDocumentUpload([pdf], {
			targetEntityType: 'Lease',
			stitcher: async () => {
				stitched = true;
				return new File(['stitched'], 'lease-scan.pdf', { type: 'application/pdf' });
			}
		});

		assert.equal(upload, pdf);
		assert.equal(stitched, false);
	});

	it('stitches multiple application photos into a named PDF upload', async () => {
		const first = new File(['first'], 'application-page-1.jpg', { type: 'image/jpeg' });
		const second = new File(['second'], 'application-page-2.jpg', { type: 'image/jpeg' });

		const upload = await prepareScanDocumentUpload([first, second], {
			targetEntityType: 'Application',
			stitcher: async (files, outputName) => {
				assert.deepEqual(files, [first, second]);
				assert.equal(outputName, 'application-scan.pdf');
				return new File(['stitched'], outputName, { type: 'application/pdf' });
			}
		});

		assert.equal(upload.name, 'application-scan.pdf');
		assert.equal(upload.type, 'application/pdf');
	});

	it('rejects combining PDFs with photos into one scan draft', async () => {
		const pdf = new File(['pdf'], 'lease.pdf', { type: 'application/pdf' });
		const photo = new File(['photo'], 'lease-page-2.jpg', { type: 'image/jpeg' });

		await assert.rejects(
			() => prepareScanDocumentUpload([pdf, photo], { targetEntityType: 'Lease' }),
			/Upload one PDF or multiple photos/
		);
	});
});
