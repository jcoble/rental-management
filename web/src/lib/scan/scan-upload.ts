import { stitchImagesToPdf } from './stitch-pdf.ts';

type Stitcher = (files: File[], outputName: string) => Promise<File>;

const TARGET_FILE_PREFIX: Record<string, string> = {
	Expense: 'expense',
	Payment: 'payment',
	WorkOrder: 'work-order',
	Lease: 'lease',
	Application: 'application'
};

function isPdf(file: File): boolean {
	const type = file.type.trim().toLowerCase();
	return type === 'application/pdf' || file.name.trim().toLowerCase().endsWith('.pdf');
}

export function scanUploadFileName(targetEntityType: string): string {
	return `${TARGET_FILE_PREFIX[targetEntityType] ?? 'document'}-scan.pdf`;
}

export async function prepareScanDocumentUpload(
	files: File[],
	{
		targetEntityType,
		stitcher = stitchImagesToPdf
	}: {
		targetEntityType: string;
		stitcher?: Stitcher;
	}
): Promise<File> {
	if (files.length === 0) throw new Error('No files to upload');
	if (files.length === 1) return files[0];
	if (files.some(isPdf)) {
		throw new Error('Upload one PDF or multiple photos for a single scan.');
	}
	return stitcher(files, scanUploadFileName(targetEntityType));
}
