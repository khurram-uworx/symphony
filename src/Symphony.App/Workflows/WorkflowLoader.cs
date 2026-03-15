using Microsoft.Extensions.Logging;
using System.Text;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Symphony.App.Workflows;

class WorkflowLoader
{
    static IReadOnlyDictionary<string, object> toStringKeyDictionary(IDictionary<object, object> source)
    {
        var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in source)
        {
            if (key is null)
                continue;

            var keyString = key.ToString() ?? string.Empty;
            result[keyString] = normalizeYamlValue(value);
        }

        return result;
    }

    static object normalizeYamlValue(object? value)
    {
        if (value is null)
            return string.Empty;

        if (value is IDictionary<object, object> map)
            return toStringKeyDictionary(map);

        if (value is IList<object> list)
        {
            var normalized = new List<object>(list.Count);
            foreach (var item in list)
                normalized.Add(normalizeYamlValue(item));

            return normalized;
        }

        return value;
    }

    readonly ILogger<WorkflowLoader> logger;
    readonly IDeserializer deserializer;

    public WorkflowLoader(ILogger<WorkflowLoader> logger)
    {
        this.logger = logger;
        deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .Build();
    }

    public WorkflowDefinition Load(string path)
    {
        if (!File.Exists(path))
            throw new WorkflowException("missing_workflow_file", $"WORKFLOW.md not found at {path}");

        var content = File.ReadAllText(path, Encoding.UTF8);
        if (!content.StartsWith("---"))
            return new WorkflowDefinition(new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase), content.Trim());

        using var reader = new StringReader(content);
        var firstLine = reader.ReadLine();
        if (firstLine is null || firstLine.Trim() != "---")
            return new WorkflowDefinition(new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase), content.Trim());

        var frontMatterBuilder = new StringBuilder();
        string? line;
        var foundTerminator = false;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Trim() == "---")
            {
                foundTerminator = true;
                break;
            }

            frontMatterBuilder.AppendLine(line);
        }

        if (!foundTerminator)
            throw new WorkflowException("workflow_parse_error", "Front matter start found but no closing delimiter.");

        var frontMatter = frontMatterBuilder.ToString();
        IReadOnlyDictionary<string, object> config;
        try
        {
            var yamlObject = deserializer.Deserialize<object>(frontMatter);
            if (yamlObject is null)
                config = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            else if (yamlObject is IDictionary<object, object> map)
                config = toStringKeyDictionary(map);
            else
                throw new WorkflowException("workflow_front_matter_not_a_map", "Front matter must be a map/object.");
        }
        catch (WorkflowException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new WorkflowException("workflow_parse_error", "Failed to parse WORKFLOW.md front matter.", ex);
        }

        var remaining = reader.ReadToEnd();
        var prompt = remaining.Trim();
        logger.LogDebug("Loaded workflow config with {KeyCount} keys", config.Count);

        return new WorkflowDefinition(config, prompt);
    }
}
