using Xunit;

// Coordinator tests share process-wide priority, GPU and vault state.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
