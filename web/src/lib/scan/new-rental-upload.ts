import { prepareScanDocumentUpload } from './scan-upload.ts';

type Stitcher = (files: File[], outputName: string) => Promise<File>;

export async function prepareNewRentalPhotoUpload(
	files: File[],
	stitcher?: Stitcher
): Promise<File> {
	return prepareScanDocumentUpload(files, { targetEntityType: 'LeaseAgreement', stitcher });
}
