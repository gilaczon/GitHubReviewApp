namespace GitHubReviewApp.Tests.Infrastructure;

/// <summary>Walks a JSON schema and yields every object schema in it, however deeply nested, so a rule can be checked on all of them rather than on the two a test happens to name.</summary>
internal static class JsonSchemaWalker
{
    public static IReadOnlyList<(string Path, JsonElement Schema)> ObjectSchemas(JsonElement root)
    {
        var found = new List<(string, JsonElement)>();
        Visit(root, "$", found);

        return found;
    }

    /// <summary>Every property name used anywhere in the schema, including the names of keywords.</summary>
    public static IReadOnlySet<string> AllKeywords(JsonElement root)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        CollectKeywords(root, names);

        return names;
    }

    private static void Visit(JsonElement element, string path, List<(string, JsonElement)> found)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String && type.GetString() == "object")
            {
                found.Add((path, element));
            }

            foreach (var property in element.EnumerateObject())
            {
                Visit(property.Value, $"{path}.{property.Name}", found);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            var index = 0;

            foreach (var item in element.EnumerateArray())
            {
                Visit(item, $"{path}[{index++}]", found);
            }
        }
    }

    private static void CollectKeywords(JsonElement element, HashSet<string> names)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                names.Add(property.Name);
                CollectKeywords(property.Value, names);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                CollectKeywords(item, names);
            }
        }
    }
}
