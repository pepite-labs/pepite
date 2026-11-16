using System.Collections.Generic;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Predict.Keyboard;

/// <summary>Reads what <see cref="MachineLayer.Untrusted"/> needs from the Windows ACL API (moved out of
/// <c>SettingsService</c>, which the unit tests compile without this API).</summary>
internal static class MachineSecurity
{
    /// <summary>Owner, reparse-point flag and explicit + inherited ACEs of a file or folder (machine layer trust rule).</summary>
    public static FileSecurityInfo? Security(string path, bool isDir)
    {
        FileSystemInfo fi = isDir ? new DirectoryInfo(path) : new FileInfo(path);
        if (!fi.Exists) return null;
        FileSystemSecurity sec = isDir ? ((DirectoryInfo)fi).GetAccessControl() : ((FileInfo)fi).GetAccessControl();
        var aces = new List<AceInfo>();
        foreach (FileSystemAccessRule r in sec.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            if ((r.PropagationFlags & PropagationFlags.InheritOnly) == 0)   // inherit-only entries do not apply to this object
                aces.Add(new AceInfo(r.IdentityReference.Value, (int)r.FileSystemRights, r.AccessControlType == AccessControlType.Allow));
        return new FileSecurityInfo(sec.GetOwner(typeof(SecurityIdentifier))?.Value,
                                    (fi.Attributes & FileAttributes.ReparsePoint) != 0, aces);
    }
}
