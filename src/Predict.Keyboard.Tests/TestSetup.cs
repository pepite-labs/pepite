using System.Runtime.CompilerServices;

namespace Predict.Keyboard.Tests;

/// <summary>Runs once when the test assembly loads, before any test.</summary>
internal static class TestSetup
{
    /// <summary>The stores' <c>Flush()</c> waits 3 s in the app (the exit must not hang on a stuck disk). On a busy 2-core CI
    /// runner a queued background write can take longer, and the tests that reopen a file right after <c>Flush()</c> then
    /// read the old content: here <c>Flush()</c> waits until the writes are done (60 s bound).</summary>
#pragma warning disable CA2255 // a module initializer in a test assembly, on purpose
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void Init() => JsonFileStore.DefaultFlushTimeoutMs = 60000;
}
