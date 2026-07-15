import assert from "node:assert/strict";
import { describe, it } from "node:test";
import { readFileSync } from "node:fs";

const choiceSource = readFileSync(
  new URL(
    "../components/scan/LeaseScanSignatureChoice.svelte",
    import.meta.url
  ),
  "utf8"
);
const generalReviewSource = readFileSync(
  new URL(
    "../../routes/(protected)/scan/[draftId]/+page.svelte",
    import.meta.url
  ),
  "utf8"
);
const leaseFirstImportSource = readFileSync(
  new URL("../components/scan/LeaseFirstImport.svelte", import.meta.url),
  "utf8"
);

describe("lease scan signature review contract", () => {
  it("explains the legal difference between importing a signed agreement and preparing a draft", () => {
    assert.match(choiceSource, /Has everyone already signed this lease\?/);
    assert.match(choiceSource, /Keep this exact PDF as the executed agreement/);
    assert.match(
      choiceSource,
      /It will not govern until the required people sign it/
    );
  });

  it("loads a filtered, server-paged active lease-template list", () => {
    assert.match(choiceSource, /documentTemplates\.listPage\(\{/);
    assert.match(choiceSource, /kind: 'Lease'/);
    assert.match(choiceSource, /status: 'Active'/);
    assert.match(choiceSource, /propertyId: propertyScope/);
    assert.match(choiceSource, /skip: templateSkip/);
    assert.match(choiceSource, /take: PAGE_SIZE/);
  });

  it("defaults NeedsSignatures to the supplied renderer and keeps custom templates optional", () => {
    assert.match(choiceSource, /Rental Command supplied lease/);
    assert.match(
      choiceSource,
      /templateSource = \$state<'BuiltInRenderer' \| 'CustomTemplate'>\('BuiltInRenderer'\)/
    );
    assert.match(
      choiceSource,
      /enabled: needsSignatures && templateSource === 'CustomTemplate'/
    );
    assert.match(
      leaseFirstImportSource,
      /reviewDisposition === 'NeedsSignatures' && documentTemplateId/
    );
    assert.match(
      leaseFirstImportSource,
      /signatureChoiceInvalid = \$derived\(!reviewDisposition\)/
    );
  });

  it("requires the disposition and conditionally includes the template in both scan review entry points", () => {
    for (const source of [generalReviewSource, leaseFirstImportSource]) {
      assert.match(source, /LeaseScanSignatureChoice/);
      assert.match(source, /reviewDisposition/);
      assert.match(source, /documentTemplateId/);
    }
    assert.match(
      generalReviewSource,
      /leaseReviewDisposition === 'NeedsSignatures'/
    );
    assert.match(
      leaseFirstImportSource,
      /reviewDisposition === 'NeedsSignatures'/
    );
    assert.match(generalReviewSource, /leaseSignatureChoiceInvalid/);
    assert.match(leaseFirstImportSource, /signatureChoiceInvalid/);
  });
});
