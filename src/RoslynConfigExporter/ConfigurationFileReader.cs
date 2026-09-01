using System.Text.Json;

namespace RoslynConfigExporter;

internal static class ConfigurationFileReader
{
    public static IReadOnlyList<ConfigFileEntry> Read(IEnumerable<string> inputs)
    {
        var files = inputs.SelectMany(ExpandInput).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var entries = new List<ConfigFileEntry>();
        foreach (var file in files)
        {
            using var stream = File.OpenRead(file);
            using var document = JsonDocument.Parse(stream, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            });
            Flatten(document.RootElement, "", Path.GetFullPath(file), entries);
        }

        return entries
            .DistinctBy(e => (e.Key.ToUpperInvariant(), e.File.ToUpperInvariant()))
            .OrderBy(e => e.Key, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.File, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IEnumerable<string> ExpandInput(string input)
    {
        if (File.Exists(input))
        {
            yield return input;
            yield break;
        }

        if (Directory.Exists(input))
        {
            foreach (var file in Directory.EnumerateFiles(input, "appsettings*.json", SearchOption.AllDirectories))
            {
                yield return file;
            }

            yield break;
        }

        throw new FileNotFoundException("Configuration file or directory was not found.", input);
    }

    private static void Flatten(JsonElement element, string prefix, string file, List<ConfigFileEntry> entries)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    Flatten(property.Value, Join(prefix, property.Name), file, entries);
                }

                break;

            case JsonValueKind.Array:
                var index = 0;
                foreach (var item in element.EnumerateArray())
                {
                    Flatten(item, Join(prefix, index++.ToString(System.Globalization.CultureInfo.InvariantCulture)), file, entries);
                }

                if (index == 0 && !string.IsNullOrEmpty(prefix))
                {
                    entries.Add(new(prefix, file));
                }

                break;

            default:
                if (!string.IsNullOrEmpty(prefix))
                {
                    entries.Add(new(prefix, file));
                }

                break;
        }
    }

    private static string Join(string prefix, string name) => string.IsNullOrEmpty(prefix) ? name : prefix + ":" + name;
}
