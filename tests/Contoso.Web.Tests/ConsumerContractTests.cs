using System.Net;
using System.Text.Json;
using Contoso.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Contoso.Web.Tests;

[TestClass]
public sealed class ConsumerContractTests
{
    [TestMethod]
    public async Task PublishedMessagesAreConsumedByRealTypedClients()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Contoso.Web.slnx")))
        {
            root = root.Parent;
        }
        Assert.IsNotNull(root);
        using var fixtures = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root.FullName, "contracts", "consumer-fixtures.json")));
        var interactions = new List<object>();
        await using var app = new WebFactory();
        using var scope = app.Services.CreateScope();
        var students = scope.ServiceProvider.GetRequiredService<StudentsApi>();
        var academics = scope.ServiceProvider.GetRequiredService<AcademicsApi>();
        var enrollments = scope.ServiceProvider.GetRequiredService<EnrollmentsApi>();

        void Respond(string service, string fixture, HttpStatusCode status = HttpStatusCode.OK)
        {
            app.Handler.Respond = request =>
            {
                var responseBody = fixture.Length == 0 ? (JsonElement?)null : fixtures.RootElement.GetProperty(fixture).Clone();
                interactions.Add(new
                {
                    service,
                    method = request.Method.ToLowerInvariant(),
                    path = request.Uri.AbsolutePath,
                    query = request.Uri.Query,
                    status = ((int)status).ToString(System.Globalization.CultureInfo.InvariantCulture),
                    requestBody = request.Body is null ? (JsonElement?)null : JsonDocument.Parse(request.Body).RootElement.Clone(),
                    responseBody
                });
                return fixture.Length == 0 ? new HttpResponseMessage(status) : ControlledHandler.Json(responseBody!.Value.GetRawText(), status);
            };
        }

        Respond("students", "student");
        Assert.AreEqual("Ada", (await students.GetAsync(7, CancellationToken.None)).FirstName);
        Respond("academics", "courses");
        Assert.AreEqual(9, (await academics.ListAsync(1, CancellationToken.None)).Courses.Single().Id);
        Respond("academics", "course");
        Assert.AreEqual(2, (await academics.GetAsync(9, CancellationToken.None)).Department.Id);
        Respond("enrollments", "enrollments");
        Assert.AreEqual(3, (await enrollments.ListAsync(1, CancellationToken.None)).Items.Single().Id);
        Respond("enrollments", "enrollment");
        Assert.IsNull((await enrollments.GetAsync(3, CancellationToken.None)).Grade);
        Respond("enrollments", "enrollment", HttpStatusCode.Created);
        Assert.AreEqual(3, (await enrollments.CreateAsync(new EnrollmentInput { StudentId = 7, CourseId = 9 }, CancellationToken.None)).Id);
        Respond("enrollments", "", HttpStatusCode.NoContent);
        await enrollments.UpdateAsync(3, new GradeInput { Grade = "A" }, CancellationToken.None);
        await enrollments.UpdateAsync(3, new GradeInput { Grade = null }, CancellationToken.None);
        await enrollments.DeleteAsync(3, CancellationToken.None);

        var directory = Path.Combine(root.FullName, "artifacts");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "consumer-interactions.json"), JsonSerializer.Serialize(interactions));
        Assert.HasCount(9, interactions);
    }
}
