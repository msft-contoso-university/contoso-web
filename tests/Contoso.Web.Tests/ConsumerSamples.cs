using System.Text.Json;

namespace Contoso.Web.Tests;

internal sealed record ConsumerSample(string Schema, JsonElement Body);

internal static class ConsumerSamples
{
    internal static readonly Dictionary<string, string[]> Required = new()
    {
        ["contoso-students"] = ["Student"],
        ["contoso-academics"] = ["Course", "CoursePage"],
        ["contoso-enrollments"] = ["Enrollment", "EnrollmentPage"]
    };

    internal static List<ConsumerSample>? FromEnvironment()
    {
        var producer = Environment.GetEnvironmentVariable("CONTOSO_CONSUMER_PRODUCER");
        var directory = Environment.GetEnvironmentVariable("CONTOSO_CONSUMER_SAMPLES");
        return producer is null && directory is null ? null : Read(producer, directory);
    }

    internal static List<ConsumerSample> Read(string? producer, string? directory)
    {
        if (producer is null || !Required.TryGetValue(producer, out var required)
            || string.IsNullOrWhiteSpace(directory) || !Path.IsPathFullyQualified(directory)
            || directory.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(part => part is "." or ".."))
        {
            throw new InvalidDataException("A recognized producer and absolute sample directory are required.");
        }
        var root = new DirectoryInfo(directory);
        for (var ancestor = root; ancestor is not null; ancestor = ancestor.Parent)
        {
            if (!ancestor.Exists || (ancestor.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException("Sample directories must exist and must not traverse links.");
            }
        }
        var allowed = producer switch
        {
            "contoso-academics" => new[] { "Course", "CoursePage", "Department", "DepartmentPage", "Instructor", "InstructorPage" },
            "contoso-students" => ["Student", "StudentPage"],
            _ => ["Enrollment", "EnrollmentPage"]
        };
        var result = new List<ConsumerSample>();
        long total = 0;
        var count = 0;
        foreach (var entry in root.EnumerateFileSystemInfos())
        {
            if (++count > 32 || (entry.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0
                || !string.Equals(entry.Extension, ".json", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Samples require at most 32 regular top-level JSON files, without links or subdirectories.");
            }
            using var stream = new FileStream(entry.FullName, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length is < 1 or > 1_048_576 || (total += stream.Length) > 4_194_304)
            {
                throw new InvalidDataException("Sample size exceeds 1 MiB per file or 4 MiB total.");
            }
            var bytes = new byte[(int)stream.Length];
            stream.ReadExactly(bytes);
            if (stream.ReadByte() != -1)
            {
                throw new InvalidDataException("Sample changed while reading.");
            }
            try
            {
                using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
                var envelope = document.RootElement;
                if (envelope.ValueKind != JsonValueKind.Object || envelope.EnumerateObject().Count() != 2
                    || !envelope.TryGetProperty("schema", out var schema) || schema.ValueKind != JsonValueKind.String
                    || !allowed.Contains(schema.GetString(), StringComparer.Ordinal)
                    || !envelope.TryGetProperty("body", out var body) || body.ValueKind != JsonValueKind.Object)
                {
                    throw new InvalidDataException("Sample requires a recognized schema and an object body.");
                }
                result.Add(new ConsumerSample(schema.GetString()!, body.Clone()));
            }
            catch (JsonException)
            {
                throw new InvalidDataException("Sample must be valid JSON with maximum depth 32.");
            }
        }
        if (required.Except(result.Select(sample => sample.Schema), StringComparer.Ordinal).Any())
        {
            throw new InvalidDataException("Missing required consumer schema coverage: " + string.Join(", ", required));
        }
        return result.Where(sample => required.Contains(sample.Schema, StringComparer.Ordinal)).ToList();
    }
}
