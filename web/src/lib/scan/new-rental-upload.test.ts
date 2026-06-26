import assert from 'node:assert/strict';
import { describe, it } from 'node:test';

import { prepareNewRentalPhotoUpload } from './new-rental-upload.ts';

describe('prepareNewRentalPhotoUpload', () => {
	it('keeps a single lease photo as its original image file', async () => {
		const photo = new File(['lease photo bytes'], 'lease-photo.jpg', { type: 'image/jpeg' });
		let stitched = false;

		const upload = await prepareNewRentalPhotoUpload([photo], async () => {
			stitched = true;
			return new File(['pdf bytes'], 'lease-scan.pdf', { type: 'application/pdf' });
		});

		assert.equal(upload, photo);
		assert.equal(upload.type, 'image/jpeg');
		assert.equal(stitched, false);
	});

	it('stitches multiple lease photos into one upload file', async () => {
		const first = new File(['first'], 'first.jpg', { type: 'image/jpeg' });
		const second = new File(['second'], 'second.jpg', { type: 'image/jpeg' });
		const pdf = new File(['pdf bytes'], 'lease-scan.pdf', { type: 'application/pdf' });

		const upload = await prepareNewRentalPhotoUpload([first, second], async (files, outputName) => {
			assert.deepEqual(files, [first, second]);
			assert.equal(outputName, 'lease-scan.pdf');
			return new File([await pdf.arrayBuffer()], outputName, { type: pdf.type });
		});

		assert.equal(upload.name, 'lease-scan.pdf');
		assert.equal(upload.type, 'application/pdf');
	});
});
