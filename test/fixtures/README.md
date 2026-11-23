# Shared test fixtures

- `legacy-updaters/`: the bodies of the old `Update-Construct.ps1` of updater classes E and F
  (docs/plans/construct-platform-design.md 13.2) and the lifecycle lock the class F body loads
  from the old install. `test/agent-bootstrap.test.ps1` and `test/source-release.test.ps1` run
  them against this tree.
- `vault-patterns.json`: the key vault's search patterns, read by the host service's
  `VaultPatternFixtureTests`.
