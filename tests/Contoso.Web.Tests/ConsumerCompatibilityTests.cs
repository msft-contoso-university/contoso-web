using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Contoso.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Contoso.Web.Tests;

[TestClass]
[TestCategory("ConsumerCompatibility")]
public sealed class ConsumerCompatibilityTests
{
    private static readonly Dictionary<string, string> Fixtures = new()
    {
        ["Student"] = """{"id":7,"firstName":"Ada","lastName":"Lovelace","enrollmentDate":"2024-09-01T00:00:00Z"}""",
        ["Course"] = """{"id":9,"title":"Applied mathematics","credits":0,"departmentId":2,"department":{"id":2,"name":"Science"}}""",
        ["CoursePage"] = """{"courses":[{"id":9,"title":"Applied mathematics","credits":0,"departmentId":2,"department":{"id":2,"name":"Science"}}],"count":1,"pages":1,"currentPage":1}""",
        ["Enrollment"] = """{"id":3,"studentId":7,"courseId":9,"grade":null}""",
        ["EnrollmentPage"] = """{"items":[{"id":3,"studentId":7,"courseId":9,"grade":null}],"page":1,"pageSize":50,"totalCount":1}"""
    };

    [TestMethod]
    public async Task ActualProducerSamplesPassThroughTypedClients()
    {
        var samples = ConsumerSamples.FromEnvironment() ?? Fixtures.Select(pair =>
            new ConsumerSample(pair.Key, JsonSerializer.SerializeToElement(JsonNode.Parse(pair.Value)))).ToList();
        foreach (var sample in samples)
        {
            await Consume(sample.Schema, sample.Body.GetRawText());
        }
    }

    [TestMethod]
    public async Task RenamedRequiredFieldsFailInRealConsumers()
    {
        foreach (var (schema, field) in new[] { ("Student", "firstName"), ("Course", "credits"),
                     ("CoursePage", "count"), ("Enrollment", "studentId"), ("EnrollmentPage", "totalCount") })
        {
            var body = JsonNode.Parse(Fixtures[schema])!.AsObject();
            body["renamed"] = body[field]!.DeepClone();
            body.Remove(field);
            var error = await Assert.ThrowsAsync<BackendException>(() => Consume(schema, body.ToJsonString()));
            Assert.AreEqual(502, error.StatusCode, schema);
        }
    }

    [TestMethod]
    public async Task WrongTypesInvalidValuesAndNestedBreaksFailClosed()
    {
        foreach (var (schema, body) in new[]
        {
            ("Student", Fixtures["Student"].Replace("\"Ada\"", "null", StringComparison.Ordinal)),
            ("Student", Fixtures["Student"].Replace("\"id\":7", "\"id\":\"7\"", StringComparison.Ordinal)),
            ("Course", Fixtures["Course"].Replace("\"name\":\"Science\"", "\"name\":null", StringComparison.Ordinal)),
            ("CoursePage", Fixtures["CoursePage"].Replace("\"credits\":0,", "", StringComparison.Ordinal)),
            ("Enrollment", Fixtures["Enrollment"].Replace("\"grade\":null", "\"grade\":\"Z\"", StringComparison.Ordinal)),
            ("EnrollmentPage", Fixtures["EnrollmentPage"].Replace("\"items\":[{", "\"items\":[null,{", StringComparison.Ordinal)),
            ("Student", "[]"), ("CoursePage", "null")
        })
        {
            var error = await Assert.ThrowsAsync<BackendException>(() => Consume(schema, body));
            Assert.AreEqual(502, error.StatusCode, schema);
        }
    }

    [TestMethod]
    public void ExternalInputConfigurationFailsClosed()
    {
        Assert.ThrowsExactly<InvalidDataException>(() => ConsumerSamples.Read(null, null));
        Assert.ThrowsExactly<InvalidDataException>(() => ConsumerSamples.Read("unknown", AppContext.BaseDirectory));
        Assert.ThrowsExactly<InvalidDataException>(() => ConsumerSamples.Read("contoso-students", "relative"));
        Assert.ThrowsExactly<InvalidDataException>(() => ConsumerSamples.Read("contoso-students", Path.Combine(AppContext.BaseDirectory, "..")));
    }

    [TestMethod]
    public void ExternalFilesRequireCoverageAndBoundedJsonEnvelopes()
    {
        var root = Directory.CreateDirectory(Path.Combine("TestResults", "consumer-samples-" + Guid.NewGuid().ToString("N")));
        var path = Path.Combine(root.FullName, "sample.json");
        try
        {
            Assert.ThrowsExactly<InvalidDataException>(() => ConsumerSamples.Read("contoso-students", root.FullName));
            File.WriteAllText(path, """{"schema":"Student","body":{"id":7}}""");
            Assert.HasCount(1, ConsumerSamples.Read("contoso-students", root.FullName));
            Assert.ThrowsExactly<InvalidDataException>(() => ConsumerSamples.Read("contoso-academics", root.FullName));
            foreach (var invalid in new[] { "{", "[]", """{"schema":"Student","body":null}""",
                         """{"schema":"Unknown","body":{}}""", """{"schema":"Student","body":{},"url":"https://invalid.test"}""",
                         new string(' ', 1_048_577), """{"schema":"Student","body":{"nested":""" + new string('[', 33) + "0" + new string(']', 33) + "}}" })
            {
                File.WriteAllText(path, invalid);
                Assert.ThrowsExactly<InvalidDataException>(() => ConsumerSamples.Read("contoso-students", root.FullName));
            }
            File.WriteAllText(path, """{"schema":"Student","body":{}}""");
            Directory.CreateDirectory(Path.Combine(root.FullName, "nested"));
            Assert.ThrowsExactly<InvalidDataException>(() => ConsumerSamples.Read("contoso-students", root.FullName));
            Directory.Delete(Path.Combine(root.FullName, "nested"));
            for (var i = 0; i < 32; i++)
            {
                File.WriteAllText(Path.Combine(root.FullName, $"{i}.json"), """{"schema":"Student","body":{}}""");
            }
            Assert.ThrowsExactly<InvalidDataException>(() => ConsumerSamples.Read("contoso-students", root.FullName));
            foreach (var file in root.EnumerateFiles()) { file.Delete(); }
            for (var i = 0; i < 5; i++)
            {
                File.WriteAllText(Path.Combine(root.FullName, $"{i}.json"), """{"schema":"Student","body":{}}""".PadRight(900_000));
            }
            Assert.ThrowsExactly<InvalidDataException>(() => ConsumerSamples.Read("contoso-students", root.FullName));
        }
        finally
        {
            root.Delete(true);
        }
    }

    private static async Task Consume(string schema, string body)
    {
        await using var app = new WebFactory();
        app.Handler.Respond = _ => ControlledHandler.Json(body);
        using var scope = app.Services.CreateScope();
        var services = scope.ServiceProvider;
        using var json = JsonDocument.Parse(body);
        // Request IDs/pages come from the capture, never a URL supplied by the artifact.
        var id = json.RootElement.ValueKind == JsonValueKind.Object && json.RootElement.TryGetProperty("id", out var value)
            && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var parsed) ? parsed : 7;
        var pageField = schema == "CoursePage" ? "currentPage" : "page";
        var page = json.RootElement.ValueKind == JsonValueKind.Object && json.RootElement.TryGetProperty(pageField, out var p)
            && p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var parsedPage) ? parsedPage : 1;
        switch (schema)
        {
            case "Student":
                Assert.AreEqual(id, (await services.GetRequiredService<StudentsApi>().GetAsync(id, CancellationToken.None)).Id);
                break;
            case "Course":
                Assert.AreEqual(id, (await services.GetRequiredService<AcademicsApi>().GetAsync(id, CancellationToken.None)).Id);
                break;
            case "CoursePage":
                Assert.AreEqual(page, (await services.GetRequiredService<AcademicsApi>().ListAsync(page, CancellationToken.None)).CurrentPage);
                break;
            case "Enrollment":
                var api = services.GetRequiredService<EnrollmentsApi>();
                var enrollment = await api.GetAsync(id, CancellationToken.None);
                app.Handler.Respond = _ => ControlledHandler.Json(body, HttpStatusCode.Created);
                Assert.AreEqual(enrollment, await api.CreateAsync(new EnrollmentInput
                {
                    StudentId = enrollment.StudentId,
                    CourseId = enrollment.CourseId,
                    Grade = enrollment.Grade
                }, CancellationToken.None));
                break;
            case "EnrollmentPage":
                Assert.AreEqual(page, (await services.GetRequiredService<EnrollmentsApi>().ListAsync(page, CancellationToken.None)).Page);
                break;
            default:
                Assert.Fail("Unrecognized consumer schema.");
                break;
        }
        Assert.IsTrue(app.Handler.Requests.All(request => request.Uri.Host.EndsWith(".example.test", StringComparison.Ordinal)));
    }
}
