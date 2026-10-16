using Xunit;

// Sim tests mutate process-wide statics (MovementConfig.Current — including the
// CorrectorScenario harness knob — and hot-reload paths), so concurrent test
// CLASSES race through them and produce order-dependent flakes. Determinism is
// the project's core invariant; the suite runs sequentially on purpose.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

// The [move]/[action] FSM trace (SimTrace) is a live-play dev tool; in the test host
// it only floods xUnit's captured output (MTile.Bench turns it off the same way).
internal static class TestHostSetup
{
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void DisableSimTrace() => MTile.SimTrace.Enabled = false;
}
