using System.Text.Json.Nodes;

namespace Sentinel.Core.Ocsf;

/// <summary>
/// Validates the structural requirements declared by the pinned OCSF 1.9.0
/// schema files. It is intentionally limited to the classes and objects
/// vendored under <c>schemas/ocsf-1.9.0</c>; it is not a general OCSF compiler.
/// </summary>
public sealed class OcsfSchemaValidator
{
    private static readonly Dictionary<string, string> AttributeObjects = new(StringComparer.Ordinal)
    {
        ["metadata"] = "metadata",
        ["device"] = "device",
        ["actor"] = "actor",
        ["process"] = "process",
        ["src_endpoint"] = "network_endpoint",
        ["dst_endpoint"] = "network_endpoint",
        ["connection_info"] = "network_connection_info",
        ["file"] = "file",
        ["user"] = "user",
        ["product"] = "product",
        ["parent_process"] = "process",
        ["hashes"] = "fingerprint"
    };

    private readonly string _root;
    private readonly Dictionary<string, JsonObject?> _cache = new(StringComparer.OrdinalIgnoreCase);

    public OcsfSchemaValidator(string schemaRoot)
    {
        _root = schemaRoot;
    }

    public IReadOnlyList<string> Validate(JsonObject ocsf, string classRelativePath)
    {
        var errors = new List<string>();
        var classSchema = Load(classRelativePath);
        if (classSchema is null)
        {
            errors.Add($"Schema class not found: {classRelativePath}");
            return errors;
        }

        var categoryName = classSchema["extends"]?.GetValue<string>();
        if (string.IsNullOrEmpty(categoryName))
        {
            errors.Add($"{classRelativePath} has no category ('extends').");
            return errors;
        }

        var categoryRelativePath = $"events/{categoryName}/{categoryName}.json";
        var categorySchema = Load(categoryRelativePath);
        if (categorySchema is null)
        {
            errors.Add($"Schema category not found: {categoryRelativePath}");
            return errors;
        }

        var baseSchema = Load("events/base_event.json")!;
        var attributeSets = new List<JsonObject> { baseSchema, categorySchema, classSchema };

        if (ocsf["metadata"]?["profiles"] is JsonArray profiles)
        {
            foreach (var profile in profiles)
            {
                var name = profile?.GetValue<string>();
                if (string.IsNullOrEmpty(name))
                {
                    errors.Add("metadata.profiles contains an empty profile name.");
                    continue;
                }

                var profileSchema = Load($"profiles/{name}.json");
                if (profileSchema is null)
                {
                    errors.Add($"Schema profile not found: {name}");
                    continue;
                }

                attributeSets.Add(profileSchema);
            }
        }

        var required = new HashSet<string>(StringComparer.Ordinal);
        foreach (var schema in attributeSets)
        {
            CollectRequired(schema, required, errors);
        }

        foreach (var name in required)
        {
            if (!HasValue(ocsf, name))
            {
                errors.Add($"Missing required field '{name}'.");
            }
        }

        var categoryUid = Load("categories.json")?["attributes"]?[categoryName]?["uid"]?.GetValue<int>();
        if (categoryUid.HasValue && ocsf["category_uid"]?.GetValue<int>() != categoryUid)
        {
            errors.Add($"category_uid must be {categoryUid} for {categoryName}.");
        }

        var classUid = classSchema["uid"]?.GetValue<int>();
        var fullClassUid = categoryUid.HasValue && classUid.HasValue
            ? categoryUid.Value * 1000 + classUid.Value
            : (int?)null;
        if (fullClassUid.HasValue && ocsf["class_uid"]?.GetValue<int>() != fullClassUid)
        {
            errors.Add($"class_uid must be {fullClassUid} for {classSchema["name"]?.GetValue<string>()}.");
        }

        var activityId = ocsf["activity_id"]?.GetValue<int>();
        var typeUid = ocsf["type_uid"]?.GetValue<int>();
        if (fullClassUid.HasValue && activityId.HasValue && typeUid.HasValue && typeUid != fullClassUid * 100 + activityId)
        {
            errors.Add($"type_uid {typeUid} does not equal class_uid*100+activity_id.");
        }

        if (ocsf["metadata"]?["version"]?.GetValue<string>() != "1.9.0")
        {
            errors.Add("metadata.version must be 1.9.0.");
        }

        foreach (var (attribute, objectName) in AttributeObjects)
        {
            if (ocsf[attribute] is JsonObject child)
            {
                ValidateObject(child, objectName, attribute, errors, 0);
            }
        }

        return errors;
    }

    private void ValidateObject(JsonObject value, string objectName, string path, List<string> errors, int depth)
    {
        if (depth > 8)
        {
            errors.Add($"{path}: object nesting exceeds the validator limit.");
            return;
        }

        var schema = LoadObject(objectName);
        if (schema is null)
        {
            return;
        }

        var required = new HashSet<string>(StringComparer.Ordinal);
        CollectRequired(schema, required, errors);
        foreach (var name in required)
        {
            if (!HasValue(value, name))
            {
                errors.Add($"Missing required field '{path}.{name}'.");
            }
        }

        foreach (var (attribute, childObject) in AttributeObjects)
        {
            if (attribute == "hashes")
            {
                if (value[attribute] is JsonArray hashes)
                {
                    for (var i = 0; i < hashes.Count; i++)
                    {
                        if (hashes[i] is JsonObject hash)
                        {
                            ValidateObject(hash, "fingerprint", $"{path}.hashes[{i}]", errors, depth + 1);
                        }
                    }
                }

                continue;
            }

            if (value[attribute] is JsonObject child)
            {
                ValidateObject(child, childObject, $"{path}.{attribute}", errors, depth + 1);
            }
        }
    }

    private JsonObject? LoadObject(string objectName)
    {
        var schema = Load($"objects/{objectName}.json");
        if (schema is null)
        {
            return null;
        }

        var parent = schema["extends"]?.GetValue<string>();
        if (string.IsNullOrEmpty(parent))
        {
            return schema;
        }

        var parentSchema = LoadObject(parent);
        if (parentSchema is null)
        {
            return schema;
        }

        var merged = new JsonObject();
        MergeAttributes(merged, parentSchema);
        MergeAttributes(merged, schema);
        return merged;
    }

    private static void MergeAttributes(JsonObject target, JsonObject source)
    {
        if (source["attributes"] is not JsonObject attributes)
        {
            return;
        }

        if (target["attributes"] is not JsonObject targetAttributes)
        {
            targetAttributes = new JsonObject();
            target["attributes"] = targetAttributes;
        }

        foreach (var (name, value) in attributes)
        {
            targetAttributes[name] = value?.DeepClone();
        }
    }

    private static void CollectRequired(JsonObject schema, HashSet<string> required, List<string> errors)
    {
        if (schema["attributes"] is not JsonObject attributes)
        {
            return;
        }

        foreach (var (name, value) in attributes)
        {
            if (name == "$include")
            {
                continue;
            }

            if (value is not JsonObject attribute)
            {
                continue;
            }

            if (attribute["requirement"]?.GetValue<string>() == "required")
            {
                required.Add(name);
            }
        }
    }

    private JsonObject? Load(string relativePath)
    {
        if (_cache.TryGetValue(relativePath, out var cached))
        {
            return cached;
        }

        var path = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(path))
        {
            _cache[relativePath] = null;
            return null;
        }

        var parsed = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
        _cache[relativePath] = parsed;
        return parsed;
    }

    private static bool HasValue(JsonObject obj, string name)
    {
        if (!obj.TryGetPropertyValue(name, out var value) || value is null)
        {
            return false;
        }

        if (value is JsonArray array && array.Count == 0)
        {
            return false;
        }

        return true;
    }
}
