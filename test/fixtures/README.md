# Shared test fixtures

Files both the PowerShell and the node test suites read, so the two engines are
compared against ONE recorded artifact rather than against each other's current
behaviour.

- `publish-manifest.input.json` / `publish-manifest.expected.json` —
  the provenance manifest entry a **publish** writes (plan 4.13 / B15). The
  expected file holds the exact bytes; `test/config-sync.test.ps1`
  (`Publish-ConstructConfigProfiles` / `ConvertTo-ConstructJsonValue`) and
  `extension/test/configsync.test.js` (`publishManifestEntry`) must both
  reproduce them byte for byte, because Publish and Import write the same
  manifest and either engine may have written the file the other one reads.
