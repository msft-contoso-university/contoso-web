using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using Contoso.Web.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Identity.Web;

namespace Contoso.Web.Controllers;

public sealed record ErrorView(string Title, string Message, string TraceId);

public sealed class BackendErrorFilter : IExceptionFilter
{
    public void OnException(ExceptionContext context)
    {
        if (context.Exception is not BackendException exception)
        {
            return;
        }
        var (title, message) = exception.StatusCode switch
        {
            400 => ("Request not accepted", "Check the entered values and that the student and course exist."),
            401 => ("Sign-in required", "The service could not authenticate this session. Sign out and sign in again."),
            403 => ("Access denied", "The service requires the appropriate delegated scope and Reader or Registrar role. Contact your administrator."),
            404 => ("Record not found", "This record does not exist or is no longer available."),
            409 => ("Record conflict", "This enrollment may already exist or have changed. Refresh before trying again."),
            _ => ("Service unavailable", "The university service could not return a valid response. Try again later.")
        };
        context.Result = new ViewResult
        {
            ViewName = "~/Views/Shared/Error.cshtml",
            StatusCode = exception.StatusCode,
            ViewData = new ViewDataDictionary<ErrorView>(new EmptyModelMetadataProvider(), context.ModelState)
            {
                Model = new ErrorView(title, message, Activity.Current?.TraceId.ToString() ?? context.HttpContext.TraceIdentifier)
            }
        };
        context.ExceptionHandled = true;
    }
}

public sealed class HomeController : Controller
{
    [AllowAnonymous]
    public IActionResult Index() => View();

    [AllowAnonymous]
    [Route("/home/error")]
    public IActionResult Error()
    {
        Response.StatusCode = 500;
        return View("~/Views/Shared/Error.cshtml", new ErrorView("Something went wrong",
            "The request could not be completed. Contact support with the reference below.",
            Activity.Current?.TraceId.ToString() ?? HttpContext.TraceIdentifier));
    }
}

public sealed class AccountController : Controller
{
    [HttpGet]
    [AllowAnonymous]
    public IActionResult SignIn() => Challenge(new AuthenticationProperties { RedirectUri = "/" }, OpenIdConnectDefaults.AuthenticationScheme);

    [HttpPost]
    [Authorize]
    public IActionResult SignOutUser() => SignOut(new AuthenticationProperties { RedirectUri = "/" },
        CookieAuthenticationDefaults.AuthenticationScheme, OpenIdConnectDefaults.AuthenticationScheme);

    [HttpGet]
    [AllowAnonymous]
    public IActionResult Error()
    {
        Response.StatusCode = 401;
        return View("~/Views/Shared/Error.cshtml", new ErrorView("Sign-in was not completed",
            "Try signing in again or contact your administrator to check application access and consent.",
            Activity.Current?.TraceId.ToString() ?? HttpContext.TraceIdentifier));
    }
}

[Authorize]
public sealed class StudentsController(StudentsApi students) : Controller
{
    [HttpGet]
    [AuthorizeForScopes(ScopeKeySection = "Services:Students:ReadScopes")]
    public async Task<IActionResult> Index([Range(1, int.MaxValue)] int? id, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            Response.StatusCode = 400;
            return View();
        }
        return View(id.HasValue ? await students.GetAsync(id.Value, cancellationToken) : null);
    }
}

[Authorize]
public sealed class CatalogController(AcademicsApi academics) : Controller
{
    [HttpGet]
    [AuthorizeForScopes(ScopeKeySection = "Services:Academics:ReadScopes")]
    public async Task<IActionResult> Index([Range(1, 100000)] int page = 1, CancellationToken cancellationToken = default)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest();
        }
        return View(await academics.ListAsync(page, cancellationToken));
    }

    [HttpGet]
    [AuthorizeForScopes(ScopeKeySection = "Services:Academics:ReadScopes")]
    public async Task<IActionResult> Details([Range(1, int.MaxValue)] int id, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest();
        }
        return View(await academics.GetAsync(id, cancellationToken));
    }
}

[Authorize]
public sealed class EnrollmentsController(EnrollmentsApi enrollments) : Controller
{
    [HttpGet]
    [AuthorizeForScopes(ScopeKeySection = "Services:Enrollments:ReadScopes")]
    public async Task<IActionResult> Index([Range(1, 100000)] int page = 1, CancellationToken cancellationToken = default)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest();
        }
        return View(await enrollments.ListAsync(page, cancellationToken));
    }

    [HttpGet]
    public IActionResult Create([Range(1, int.MaxValue)] int? courseId)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest();
        }
        return View(new EnrollmentInput { CourseId = courseId ?? 0 });
    }

    [HttpPost]
    [AuthorizeForScopes(ScopeKeySection = "Services:Enrollments:WriteScopes")]
    public async Task<IActionResult> Create(EnrollmentInput input, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            Response.StatusCode = 400;
            return View(input);
        }
        await enrollments.CreateAsync(input, cancellationToken);
        return RedirectToAction(nameof(Index));
    }

    [HttpGet]
    [AuthorizeForScopes(ScopeKeySection = "Services:Enrollments:ReadScopes")]
    public async Task<IActionResult> Edit([Range(1, int.MaxValue)] int id, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest();
        }
        var enrollment = await enrollments.GetAsync(id, cancellationToken);
        ViewData["Enrollment"] = enrollment;
        return View(new GradeInput { Grade = enrollment.Grade });
    }

    [HttpPost]
    [AuthorizeForScopes(ScopeKeySection = "Services:Enrollments:WriteScopes")]
    public async Task<IActionResult> Edit([Range(1, int.MaxValue)] int id, GradeInput input, CancellationToken cancellationToken)
    {
        if (id <= 0)
        {
            return BadRequest();
        }
        if (!ModelState.IsValid)
        {
            Response.StatusCode = 400;
            ViewData["Id"] = id;
            return View(input);
        }
        await enrollments.UpdateAsync(id, input, cancellationToken);
        return RedirectToAction(nameof(Index));
    }

    [HttpGet]
    [AuthorizeForScopes(ScopeKeySection = "Services:Enrollments:ReadScopes")]
    public async Task<IActionResult> Delete([Range(1, int.MaxValue)] int id, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest();
        }
        return View(await enrollments.GetAsync(id, cancellationToken));
    }

    [HttpPost]
    [ActionName("Delete")]
    [AuthorizeForScopes(ScopeKeySection = "Services:Enrollments:WriteScopes")]
    public async Task<IActionResult> DeleteConfirmed([Range(1, int.MaxValue)] int id, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest();
        }
        await enrollments.DeleteAsync(id, cancellationToken);
        return RedirectToAction(nameof(Index));
    }
}
