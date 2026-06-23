import { stitchImagesToPdf } from './stitch-pdf.ts';

type Stitcher = (files: File[]) => Promise<File>;

export async function prepareNewRentalPhotoUpload(
	files: File[],
	stitcher: Stitcher = stitchImagesToPdf
): Promise<File> {
	if (files.length === 0) throw new Error('No photos to upload');
	if (files.length === 1) return files[0];
	return stitcher(files);
}
