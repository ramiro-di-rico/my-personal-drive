using Xunit;

// Test classes run sequentially.
//
// AppDataCollection already serialized the classes that swap XDG_CONFIG_HOME against each other,
// but two pieces of state are process-global in a way a collection cannot contain: that variable,
// and Localizer.Instance. A collection only serializes the classes *inside* it — different
// collections still run in parallel — so a test that switches the interface language runs
// alongside every test that asserts on a localized sentence, and one of them loses.
//
// That produced exactly the failure AppDataCollection's own comment warns about: green in
// isolation, red once in a full run, never the same test twice. The suite is ~10 s; buying
// determinism with a few seconds of wall time is the right trade, and the alternative — injecting
// a Localizer into every view model and into ByteSize — is a design change made for the test
// harness rather than for the app.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
