/**
 * The text the landlord pastes into Zillow (or anywhere else): headline, description,
 * rent, and deposit as plain lines. Anything blank or not a number is left out.
 */
export type ListingCopyValues = {
	headline?: string | null;
	description?: string | null;
	rent?: string | number | null;
	securityDeposit?: string | number | null;
};

function money(value: string | number | null | undefined): string | null {
	if (value == null || String(value).trim() === '') return null;
	const amount = Number(value);
	if (!Number.isFinite(amount)) return null;
	return amount.toLocaleString('en-US', {
		style: 'currency',
		currency: 'USD',
		minimumFractionDigits: Number.isInteger(amount) ? 0 : 2,
		maximumFractionDigits: 2,
	});
}

export function listingClipboardText(listing: ListingCopyValues): string {
	const blocks: string[] = [];
	const headline = listing.headline?.trim();
	const description = listing.description?.trim();
	if (headline) blocks.push(headline);
	if (description) blocks.push(description);

	const amounts: string[] = [];
	const rent = money(listing.rent);
	const deposit = money(listing.securityDeposit);
	if (rent) amounts.push(`Rent: ${rent} per month`);
	if (deposit) amounts.push(`Deposit: ${deposit}`);
	if (amounts.length) blocks.push(amounts.join('\n'));

	return blocks.join('\n\n');
}
