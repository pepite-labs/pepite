using System;
using System.IO;
using System.Threading;

namespace Predict.Keyboard.Tests;

/// <summary>Test teardown only: delete a temp folder, never failing the test. The file stores write on background tasks and a
/// scanner (Defender, the indexer) may hold a just-written file for a moment, so a delete is retried briefly; a folder
/// still busy after that is left in %TEMP% (unique per test) rather than turning a passed test red.</summary>
internal static class TestDirs
{
    public static void Delete(string dir)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(50 * (attempt + 1));
            }
        }
    }
}
