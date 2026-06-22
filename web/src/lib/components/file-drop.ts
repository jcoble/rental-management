export type FileDropCandidate = {
	name: string;
	type?: string | null;
};

export const ALLOWED_MIME_TYPES = [
	'application/pdf',
	'image/jpeg',
	'image/jpg',
	'image/png',
	'image/gif',
	'image/webp',
	'image/heic',
	'image/heif',
	'image/bmp',
	'image/tiff'
] as const;

const ALLOWED_EXTENSIONS = new Set([
	'.pdf',
	'.jpg',
	'.jpeg',
	'.png',
	'.gif',
	'.webp',
	'.heic',
	'.heif',
	'.bmp',
	'.tif',
	'.tiff'
]);

export function isSupportedFile(file: FileDropCandidate): boolean {
	const mime = file.type?.trim().toLowerCase();
	if (mime && (ALLOWED_MIME_TYPES as readonly string[]).includes(mime)) {
		return true;
	}

	const normalizedName = file.name.trim().toLowerCase();
	const dotIndex = normalizedName.lastIndexOf('.');
	if (dotIndex < 0) {
		return false;
	}

	return ALLOWED_EXTENSIONS.has(normalizedName.slice(dotIndex));
}

export function partitionSupportedFiles<TFile extends FileDropCandidate>(files: readonly TFile[]) {
	const accepted: TFile[] = [];
	const rejected: TFile[] = [];

	for (const file of files) {
		if (isSupportedFile(file)) {
			accepted.push(file);
		} else {
			rejected.push(file);
		}
	}

	return { accepted, rejected };
}

export function unsupportedFileMessage(file: FileDropCandidate): string {
	const suppliedType = file.type?.trim();
	const typeLabel = suppliedType || file.name || 'unknown';
	return `File type "${typeLabel}" is not supported. Upload a PDF or image.`;
}
