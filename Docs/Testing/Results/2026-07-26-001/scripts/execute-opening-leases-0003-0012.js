async (page) => {
	const leases = [
		{ scan: 'SCN-0003', file: '/Users/blackcolours/dev/work/rental-management/output/pdf/tsk-749-year-simulation-scan-corpus/documents/2027-01-02/scn-0003-opening-lease-l003.pdf', property: 'Cedar Bend', address: '151 Cedar Street', zip: '43203', unit: 'Main', beds: '2', baths: '1.0', rent: '1725', tenant: 'Jordan Reed', email: 'tenant.003@example.local', start: '04/01/2026', end: '10/31/2027', deposit: '1725' },
		{ scan: 'SCN-0004', file: '/Users/blackcolours/dev/work/rental-management/output/pdf/tsk-749-year-simulation-scan-corpus/documents/2027-01-02/scn-0004-opening-lease-l004.jpg', property: 'Dover House', address: '168 Dover Street', zip: '43204', unit: 'Main', beds: '3', baths: '2.0', rent: '1350', tenant: 'Morgan Adams', email: 'tenant.004@example.local', start: '05/01/2026', end: '12/31/2027', deposit: '1350' },
		{ scan: 'SCN-0005', file: '/Users/blackcolours/dev/work/rental-management/output/pdf/tsk-749-year-simulation-scan-corpus/documents/2027-01-02/scn-0005-opening-lease-l005.pdf', property: 'Elm Haven', address: '185 Elm Street', zip: '43205', unit: 'Main', beds: '4', baths: '1.0', rent: '1875', tenant: 'Parker Flores', email: 'tenant.005@example.local', start: '06/01/2026', end: '02/28/2027', deposit: '1875' },
		{ scan: 'SCN-0006', file: '/Users/blackcolours/dev/work/rental-management/output/pdf/tsk-749-year-simulation-scan-corpus/documents/2027-01-02/scn-0006-opening-lease-l006.jpg', property: 'Franklin Place', address: '202 Franklin Street', zip: '43206', unit: 'Main', beds: '2', baths: '1.0', rent: '1500', tenant: 'Sage King', email: 'tenant.006@example.local', start: '01/01/2026', end: '12/31/2027', deposit: '1500' },
		{ scan: 'SCN-0007', file: '/Users/blackcolours/dev/work/rental-management/output/pdf/tsk-749-year-simulation-scan-corpus/documents/2027-01-02/scn-0007-opening-lease-l007.pdf', property: 'Grove House', address: '219 Grove Street', zip: '43207', unit: 'Main', beds: '3', baths: '1.0', rent: '1125', tenant: 'Val Price', email: 'tenant.007@example.local', start: '02/01/2026', end: '12/31/2027', deposit: '1125' },
		{ scan: 'SCN-0008', file: '/Users/blackcolours/dev/work/rental-management/output/pdf/tsk-749-year-simulation-scan-corpus/documents/2027-01-02/scn-0008-opening-lease-l008.jpg', property: 'Hawthorne Home', address: '236 Hawthorne Street', zip: '43208', unit: 'Main', beds: '4', baths: '2.0', rent: '1650', tenant: 'Yara Brooks', email: 'tenant.008@example.local', start: '03/01/2026', end: '08/31/2027', deposit: '1650' },
		{ scan: 'SCN-0009', file: '/Users/blackcolours/dev/work/rental-management/output/pdf/tsk-749-year-simulation-scan-corpus/documents/2027-01-02/scn-0009-opening-lease-l009.pdf', property: 'Ivy House', address: '253 Ivy Street', zip: '43209', unit: 'Main', beds: '2', baths: '1.0', rent: '1275', tenant: 'Bennett Garcia', email: 'tenant.009@example.local', start: '04/01/2026', end: '12/31/2027', deposit: '1275' },
		{ scan: 'SCN-0010', file: '/Users/blackcolours/dev/work/rental-management/output/pdf/tsk-749-year-simulation-scan-corpus/documents/2027-01-02/scn-0010-opening-lease-l010.jpg', property: 'Juniper Place', address: '270 Juniper Street', zip: '43210', unit: 'Main', beds: '3', baths: '1.0', rent: '1800', tenant: 'Elena Lewis', email: 'tenant.010@example.local', start: '05/01/2026', end: '12/31/2027', deposit: '1800' },
		{ scan: 'SCN-0011', file: '/Users/blackcolours/dev/work/rental-management/output/pdf/tsk-749-year-simulation-scan-corpus/documents/2027-01-02/scn-0011-opening-lease-l011.pdf', property: 'Kingston House', address: '287 Kingston Street', zip: '43211', unit: 'Main', beds: '4', baths: '1.0', rent: '1425', tenant: 'Hector Reed', email: 'tenant.011@example.local', start: '06/01/2026', end: '12/31/2027', deposit: '1425' },
		{ scan: 'SCN-0012', file: '/Users/blackcolours/dev/work/rental-management/output/pdf/tsk-749-year-simulation-scan-corpus/documents/2027-01-02/scn-0012-opening-lease-l012.jpg', property: 'Lakeview Home', address: '304 Lakeview Street', zip: '43212', unit: 'Main', beds: '2', baths: '2.0', rent: '1050', tenant: 'Keira Adams', email: 'tenant.012@example.local', start: '01/01/2026', end: '12/31/2027', deposit: '1050' }
	];
	const completed = [];

	for (const [index, lease] of leases.entries()) {
		const propertyReview = page.getByText('Step 1 of 5 · Property');
		if (!(await propertyReview.isVisible())) {
			if (await page.getByRole('heading', { name: "You're all set!" }).isVisible()) {
				await page.getByRole('button', { name: 'Import more records' }).click();
			}
			await page.getByTestId('onboarding-step-tab-import').click();
			await page.getByRole('heading', { name: 'New rental from your lease' }).waitFor();

			const inputs = page.locator('input[type=file]');
			await inputs.nth(lease.file.endsWith('.pdf') ? 1 : 0).setInputFiles(lease.file);
			await propertyReview.waitFor({ timeout: 120000 });
		}

		const extractedProperty = await page.getByRole('textbox', { name: 'Property name' }).inputValue();
		const extractedAddress = await page.getByRole('combobox', { name: 'Street address' }).inputValue();
		if ((extractedProperty && extractedProperty !== lease.property) || (extractedAddress && extractedAddress !== lease.address)) {
			throw new Error(`${lease.scan} extraction mismatch: ${extractedProperty} / ${extractedAddress}`);
		}

		await page.getByText('One rental', { exact: true }).click();
		await page.getByRole('textbox', { name: 'Property name' }).fill(lease.property);
		await page.getByRole('combobox', { name: 'Street address' }).fill(lease.address);
		await page.getByRole('textbox', { name: 'City', exact: true }).fill('Columbus');
		await page.getByRole('textbox', { name: 'ZIP' }).fill(lease.zip);
		await page.getByRole('button', { name: 'Next', exact: true }).click();

		await page.getByRole('textbox', { name: 'Unit number' }).fill(lease.unit);
		await page.getByRole('textbox', { name: 'Beds' }).fill(lease.beds);
		await page.getByRole('textbox', { name: 'Baths' }).fill(lease.baths);
		await page.getByRole('textbox', { name: 'Rent' }).fill(lease.rent);
		await page.getByRole('button', { name: 'Next', exact: true }).click();

		const [firstName, ...lastName] = lease.tenant.split(' ');
		await page.getByRole('textbox', { name: 'First name' }).fill(firstName);
		await page.getByRole('textbox', { name: 'Last name' }).fill(lastName.join(' '));
		await page.getByRole('textbox', { name: 'Email' }).fill(lease.email);
		await page.getByRole('textbox', { name: 'Phone' }).fill(`+1 614 555 ${String(index + 103).padStart(4, '0')}`);
		await page.getByRole('textbox', { name: 'Emergency contact' }).fill(`Simulation contact, +1 614 555 ${String(index + 193).padStart(4, '0')}`);
		await page.getByRole('button', { name: 'Next', exact: true }).click();

		await page.getByRole('textbox', { name: 'Lease number' }).fill(lease.scan);
		await page.getByRole('textbox', { name: 'Start date' }).fill(lease.start);
		await page.getByRole('textbox', { name: 'End date' }).fill(lease.end);
		await page.getByRole('textbox', { name: 'Monthly rent' }).fill(lease.rent);
		await page.getByRole('textbox', { name: 'Security deposit' }).fill(lease.deposit);
		await page.getByRole('textbox', { name: 'Late fee' }).fill('75');
		await page.getByRole('textbox', { name: 'Due day' }).fill('1');
		await page.getByRole('textbox', { name: 'Optional lease notes' }).fill(`Opening lease imported from ${lease.scan} for the 2027 simulation.`);
		await page.getByRole('button', { name: 'Next', exact: true }).click();

		await page.getByText('Yes, everyone has signed', { exact: true }).click();
		await page.getByRole('button', { name: 'Confirm & create' }).click();
		await page.getByRole('heading', { name: "You're all set!" }).waitFor({ timeout: 30000 });
		completed.push(lease.scan);
	}

	return completed;
}
