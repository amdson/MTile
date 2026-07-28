using Xunit;

// Sim tests mutate process-wide statics (MovementConfig.Current — including the
// CorrectorScenario harness knob — and hot-reload paths), so concurrent test
// CLASSES race through them and produce order-dependent flakes. Determinism is
// the project's core invariant; the suite runs sequentially on purpose.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
