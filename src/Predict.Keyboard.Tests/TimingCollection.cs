using Xunit;

namespace Predict.Keyboard.Tests;

/// <summary>Tests that assert a wall-clock budget from a plan (« ≤ 2 s ») run in this collection: xunit runs it after the
/// parallel collections, alone, so the time measured is the product's and not the CPU / disk contention of ~1 300 other
/// tests (the 40-profile import went over 2 s under that load in some runs; 0.2–0.6 s alone).</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class TimingCollection
{
    public const string Name = "Timing (runs alone)";
}
