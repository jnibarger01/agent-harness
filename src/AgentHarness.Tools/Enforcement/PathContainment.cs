namespace AgentHarness.Tools.Enforcement;

/// <summary>
/// Write-scope containment.
///
/// String prefix comparison is the classic hole: "/srv/app" prefixes "/srv/appdata", and
/// "../" walks straight out. Resolve to a full path, then compare on a separator boundary.
/// </summary>
public static class PathContainment
{
    public static bool IsContained(string allowedRoot, string candidate)
    {
        if (string.IsNullOrWhiteSpace(allowedRoot) || string.IsNullOrWhiteSpace(candidate))
            return false;

        string root, target;
        try
        {
            root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(allowedRoot));
            target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
        }
        catch (Exception)
        {
            // Unresolvable path is not a permitted path.
            return false;
        }

        if (string.Equals(root, target, StringComparison.Ordinal))
            return true;

        // The separator is what stops /srv/app from swallowing /srv/appdata.
        return target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }
}
