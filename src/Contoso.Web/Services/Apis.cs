using System.ComponentModel.DataAnnotations;

namespace Contoso.Web.Services;

public sealed record Student(int Id, string FirstName, string LastName, DateTimeOffset EnrollmentDate);
public sealed record Course(int Id, string Title, int Credits, int DepartmentId, DepartmentSummary Department);
public sealed record DepartmentSummary(int Id, string Name);
public sealed record CoursePage(Course[] Courses, int Count, int Pages, int CurrentPage);
public sealed record Enrollment(int Id, int StudentId, int CourseId, string? Grade);
public sealed record EnrollmentPage(Enrollment[] Items, int Page, int PageSize, int TotalCount);

public sealed class EnrollmentInput
{
    [Range(1, int.MaxValue)]
    [Display(Name = "Student ID")]
    public int StudentId { get; set; }

    [Range(1, int.MaxValue)]
    [Display(Name = "Course ID")]
    public int CourseId { get; set; }

    [RegularExpression("^(A|B|C|D|F)$", ErrorMessage = "Choose A, B, C, D, F or ungraded.")]
    public string? Grade { get; set; }
}

public sealed class GradeInput
{
    [RegularExpression("^(A|B|C|D|F)$", ErrorMessage = "Choose A, B, C, D, F or ungraded.")]
    public string? Grade { get; set; }
}

public sealed class StudentsApi(BackendTransport transport)
{
    public async Task<Student> GetAsync(int id, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(id);
        var result = await transport.ReadAsync<Student>("Students", $"api/v1/students/{id}", cancellationToken);
        if (result.Id != id || string.IsNullOrWhiteSpace(result.FirstName) || string.IsNullOrWhiteSpace(result.LastName)
            || result.EnrollmentDate == default)
        {
            throw new BackendException(502);
        }
        return result;
    }
}

public sealed class AcademicsApi(BackendTransport transport)
{
    public async Task<CoursePage> ListAsync(int page, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(page);
        var result = await transport.ReadAsync<CoursePage>("Academics", $"api/v1/courses?page={page}", cancellationToken);
        if (result.Courses is null || result.Courses.Length > 50 || result.Count < 0
            || result.CurrentPage != page || result.Pages < 0 || result.Courses.Any(course => !Valid(course)))
        {
            throw new BackendException(502);
        }
        return result;
    }

    public async Task<Course> GetAsync(int id, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(id);
        var result = await transport.ReadAsync<Course>("Academics", $"api/v1/courses/{id}", cancellationToken);
        if (result.Id != id || !Valid(result))
        {
            throw new BackendException(502);
        }
        return result;
    }

    private static bool Valid(Course? course) => course is not null && course.Id > 0 && !string.IsNullOrWhiteSpace(course.Title)
        && course.Credits is >= 0 and <= 5 && course.DepartmentId > 0
        && course.Department is { Id: > 0, Name.Length: > 0 } && course.Department.Id == course.DepartmentId;
}

public sealed class EnrollmentsApi(BackendTransport transport)
{
    public async Task<EnrollmentPage> ListAsync(int page, CancellationToken cancellationToken)
    {
        if (page is < 1 or > 100000)
        {
            throw new ArgumentOutOfRangeException(nameof(page));
        }
        var result = await transport.ReadAsync<EnrollmentPage>("Enrollments", $"api/v1/enrollments?page={page}", cancellationToken);
        if (result.Items is null || result.Items.Length > 50 || result.Page != page
            || result.PageSize != 50 || result.TotalCount < 0 || result.Items.Any(item => !Valid(item)))
        {
            throw new BackendException(502);
        }
        return result;
    }

    public async Task<Enrollment> GetAsync(int id, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(id);
        var result = await transport.ReadAsync<Enrollment>("Enrollments", $"api/v1/enrollments/{id}", cancellationToken);
        if (result.Id != id || !Valid(result))
        {
            throw new BackendException(502);
        }
        return result;
    }

    public async Task<Enrollment> CreateAsync(EnrollmentInput input, CancellationToken cancellationToken)
    {
        Validator.ValidateObject(input, new ValidationContext(input), true);
        var result = await transport.CreateAsync<Enrollment>("Enrollments", "api/v1/enrollments", input, cancellationToken);
        if (!Valid(result) || result.StudentId != input.StudentId || result.CourseId != input.CourseId || result.Grade != input.Grade)
        {
            throw new BackendException(502);
        }
        return result;
    }

    public Task UpdateAsync(int id, GradeInput input, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(id);
        Validator.ValidateObject(input, new ValidationContext(input), true);
        return transport.MutateAsync("Enrollments", HttpMethod.Put, $"api/v1/enrollments/{id}", input, cancellationToken);
    }

    public Task DeleteAsync(int id, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(id);
        return transport.MutateAsync("Enrollments", HttpMethod.Delete, $"api/v1/enrollments/{id}", null, cancellationToken);
    }

    private static bool Valid(Enrollment? enrollment) => enrollment is not null && enrollment.Id > 0 && enrollment.StudentId > 0 && enrollment.CourseId > 0
        && enrollment.Grade is null or "A" or "B" or "C" or "D" or "F";
}
