using System;
using System.IO;

namespace Predict.Keyboard;

/// <summary>
/// Where the app keeps its files (D29, owner 2026-11-14: the unique prefix « Pepite-Prediction », other software is
/// named Pépite). Per Windows account: <c>%APPDATA%\Pepite-Prediction</c> (settings.json = app state, <c>profiles\</c>)
/// and <c>%LOCALAPPDATA%\Pepite-Prediction\sauvegardes</c> (zip backups of the profiles, not roamed). Up to 0.6.1:
/// <c>%APPDATA%\PredictKeyboard</c>, read once by <see cref="ProfileMigration"/>.
/// </summary>
internal static class AppPaths
{
    public const string FolderName = "Pepite-Prediction";
    public const string LegacyFolderName = "PredictKeyboard";

    public static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), FolderName);
    public static string LegacyRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), LegacyFolderName);
    public static string Backups => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), FolderName, "sauvegardes");
}
