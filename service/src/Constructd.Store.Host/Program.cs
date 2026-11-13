using Constructd.Store.Http;

// The record store on its own (docs/plans/construct-platform-design.md 5.4). Everything lives in
// StandaloneStore so tests start the same host in process.
await StandaloneStore.Build(args).RunAsync();
