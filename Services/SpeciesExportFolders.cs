using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace BirdPhotoSelector;

/// <summary>Snapshots effective species labels into safe, globally unambiguous directory components.</summary>
internal static class SpeciesExportFolders
{
    internal const string Unclassified = "未分类";

    internal static Dictionary<string, string[]> Create(IEnumerable<PhotoItem> photos)
    {
        var entries = photos.ToArray();
        var identities = entries.SelectMany(photo => photo.Birds).Select(bird => bird.EffectiveSpecies)
            .Where(identity => identity is { IsValid: true }).Select(identity => identity!)
            .GroupBy(identity => identity.Key, StringComparer.OrdinalIgnoreCase)
            // A duplicate bird must not arbitrarily rename the first Chinese label.
            .Select(group => group.FirstOrDefault(identity => !string.IsNullOrWhiteSpace(identity.ChineseName)) ?? group.First())
            .OrderBy(identity => identity.Key, StringComparer.Ordinal).ToArray();
        var baseNames = identities.ToDictionary(identity => identity.Key,
            identity => Clean(identity.DisplayName, 100), StringComparer.OrdinalIgnoreCase);
        var counts = baseNames.Values.GroupBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Unclassified };
        var folders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var identity in identities)
        {
            string name = baseNames[identity.Key];
            if (counts[name] > 1 || used.Contains(name))
                name += "（" + Clean(identity.ScientificName.Trim(), 80) + "）";
            string candidate = name;
            string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity.Key))).ToLowerInvariant();
            int suffix = 0;
            while (!used.Add(candidate)) candidate = name + "-" + hash[..12] + (suffix++ == 0 ? "" : "-" + suffix);
            folders[identity.Key] = candidate;
        }
        return entries.ToDictionary(photo => photo.Id, photo =>
        {
            var keys = photo.Birds.Select(bird => bird.EffectiveSpecies).Where(identity => identity is { IsValid: true })
                .Select(identity => identity!.Key).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            return keys.Length == 0 ? new[] { Unclassified } : keys.Select(key => folders[key]).ToArray();
        }, StringComparer.Ordinal);
    }

    private static string Clean(string value, int maximum)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var chars = value.Trim().Select(character => invalid.Contains(character) || char.IsControl(character) ? '_' : character).ToArray();
        string name = new string(chars).TrimEnd(' ', '.');
        if (name.Length > maximum) name = name[..maximum].TrimEnd(' ', '.');
        if (name.Length == 0 || name is "." or "..") name = "鸟种";
        if (Regex.IsMatch(name, @"^(CON|PRN|AUX|NUL|COM[1-9¹²³]|LPT[1-9¹²³])(?:\.|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            name = "_" + name;
        return name;
    }

    internal static string EnsureDestination(string root, string folder)
    {
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException("所选导出目录已不存在。");
        if (string.IsNullOrWhiteSpace(folder) || folder is "." or ".." || Path.IsPathRooted(folder) ||
            folder != Path.GetFileName(folder) || folder.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new IOException("鸟种分类目录名称无效。");
        string parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        string destination = Path.GetFullPath(Path.Combine(parent, folder));
        if (!string.Equals(Path.GetDirectoryName(destination), parent, StringComparison.OrdinalIgnoreCase))
            throw new IOException("鸟种分类目录超出所选目标目录。");
        Directory.CreateDirectory(destination);
        // Do not follow a pre-existing junction/symlink out of the selected destination.
        if ((File.GetAttributes(destination) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("鸟种分类目录是链接，已跳过以保留原件。");
        return destination;
    }
}
