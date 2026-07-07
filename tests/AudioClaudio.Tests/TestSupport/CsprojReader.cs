using System.Xml.Linq;

namespace AudioClaudio.Tests.TestSupport;

/// <summary>
/// Reads the bare project names (no extension) declared as &lt;ProjectReference&gt;
/// in an SDK-style .csproj. SDK-style project files carry no default XML
/// namespace, so element names match without qualification.
/// </summary>
public static class CsprojReader
{
    public static ISet<string> ReferencedProjectNames(string csprojPath)
    {
        var doc = XDocument.Load(csprojPath);

        return doc.Descendants("ProjectReference")
            .Select(e => (string?)e.Attribute("Include"))
            .Where(include => !string.IsNullOrWhiteSpace(include))
            .Select(include => Path.GetFileNameWithoutExtension(include!.Replace('\\', '/')))
            .ToHashSet(StringComparer.Ordinal);
    }
}
