export function tabForLeaseEdit(_currentTab: string | undefined | null): string {
	return 'overview';
}

export function scannedLeaseDocumentLinkLabel(scanIsImage: boolean | null | undefined): string {
	return scanIsImage ? 'Open full size' : 'View scanned document';
}
