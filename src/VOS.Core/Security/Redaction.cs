using System.Text.Json;
using System.Text.Json.Nodes;

namespace VOS.Core.Security;

/// <summary>Removes secrets from data before it leaves the PC (diagnostics exports).</summary>
public static class Redaction
{
    /// <summary>Replaces every string or number whose property name suggests a secret (…key, token, secret,
    /// password) with "(removed)", at any depth. Unreadable JSON is not passed through.</summary>
    public static string RedactJson(string json)
    {
        try
        {
            var node = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            Walk(node);
            return node?.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) ?? "";
        }
        catch (JsonException) { return "(settings could not be read)"; }
    }

    private static void Walk(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var (name, value) in o.ToList())
                {
                    if (value is JsonValue && IsSecretName(name)) o[name] = "(removed)";
                    else Walk(value);
                }
                break;
            case JsonArray a:
                foreach (var item in a) Walk(item);
                break;
        }
    }

    private static bool IsSecretName(string name)
    {
        var n = name.ToLowerInvariant();
        return n.EndsWith("key", StringComparison.Ordinal) || n.Contains("token", StringComparison.Ordinal) || n.Contains("secret", StringComparison.Ordinal) || n.Contains("password", StringComparison.Ordinal);
    }
}
