/**
 * Client-side stitch of selected lease photos into ONE PDF, then uploaded via the existing
 * single-file /scans path (the server already handles PDF). One image per page, fit-to-page,
 * orientation chosen per image. jspdf is the chosen lib: tiny, no worker, pure-client, already
 * image-oriented (we only need raster pages, not text). Photos of ONE document → one draft.
 */
import { jsPDF } from 'jspdf';

async function readAsDataUrl(file: File): Promise<string> {
	return new Promise((resolve, reject) => {
		const r = new FileReader();
		r.onload = () => resolve(r.result as string);
		r.onerror = () => reject(r.error);
		r.readAsDataURL(file);
	});
}

function loadImage(dataUrl: string): Promise<HTMLImageElement> {
	return new Promise((resolve, reject) => {
		const img = new Image();
		img.onload = () => resolve(img);
		img.onerror = () => reject(new Error('Could not decode image'));
		img.src = dataUrl;
	});
}

/** Stitch image files (jpeg/png/webp) into a single A4 PDF, one image per page. */
export async function stitchImagesToPdf(files: File[]): Promise<File> {
	if (files.length === 0) throw new Error('No photos to combine');
	let doc: jsPDF | null = null;
	for (const file of files) {
		const dataUrl = await readAsDataUrl(file);
		const img = await loadImage(dataUrl);
		const landscape = img.width > img.height;
		const orientation = landscape ? 'landscape' : 'portrait';
		if (doc === null) {
			doc = new jsPDF({ orientation, unit: 'pt', format: 'a4' });
		} else {
			doc.addPage('a4', orientation);
		}
		const pageW = doc.internal.pageSize.getWidth();
		const pageH = doc.internal.pageSize.getHeight();
		// Fit the image inside the page preserving aspect ratio, with a small margin.
		const margin = 18;
		const maxW = pageW - margin * 2;
		const maxH = pageH - margin * 2;
		const scale = Math.min(maxW / img.width, maxH / img.height);
		const w = img.width * scale;
		const h = img.height * scale;
		const x = (pageW - w) / 2;
		const y = (pageH - h) / 2;
		const fmt = file.type === 'image/png' ? 'PNG' : 'JPEG';
		doc.addImage(dataUrl, fmt, x, y, w, h);
	}
	const blob = doc!.output('blob');
	return new File([blob], 'lease-scan.pdf', { type: 'application/pdf' });
}
