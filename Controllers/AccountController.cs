using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using TableOrderWeb.Models;
using TableOrderWeb.Services;

namespace TableOrderWeb.Controllers;

public class AccountController : Controller
{
    private const string AdminAuthScheme = "TableOrderWeb.AdminAuth";
    private const string StaffAuthScheme = "TableOrderWeb.StaffAuth";
    private const string LegacyAuthCookieName = "TableOrderWeb.Auth";

    private readonly IUserAccountService _userAccountService;

    public AccountController(IUserAccountService userAccountService)
    {
        _userAccountService = userAccountService;
    }

    [HttpGet]
    [AllowAnonymous]
    public IActionResult Login(string? returnUrl = null, string? targetRole = null)
    {
        return View(new LoginViewModel
        {
            ReturnUrl = returnUrl,
            TargetRole = ResolveTargetRole(returnUrl, targetRole)
        });
    }

    [HttpPost]
    [AllowAnonymous]
    [IgnoreAntiforgeryToken]
    public async Task<IActionResult> Login(LoginViewModel model, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return View(model);
        }

        var user = await _userAccountService.ValidateLoginAsync(model.UserName, model.Password, cancellationToken);
        if (user is null)
        {
            ModelState.AddModelError(string.Empty, "Tên đăng nhập hoặc mật khẩu không đúng.");
            return View(model);
        }

        var targetRole = ResolveTargetRole(model.ReturnUrl, model.TargetRole);
        if (!CanLoginForTarget(user.Role, targetRole))
        {
            ModelState.AddModelError(string.Empty, BuildRoleMismatchMessage(targetRole));
            model.TargetRole = targetRole;
            return View(model);
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, user.DisplayName),
            new(ClaimTypes.NameIdentifier, user.UserName),
            new(ClaimTypes.Role, user.Role)
        };

        var authScheme = string.Equals(user.Role, "Admin", StringComparison.OrdinalIgnoreCase)
            ? AdminAuthScheme
            : StaffAuthScheme;
        var identity = new ClaimsIdentity(claims, authScheme);
        var principal = new ClaimsPrincipal(identity);

        await HttpContext.SignInAsync(
            authScheme,
            principal,
            new AuthenticationProperties
            {
                IsPersistent = true,
                ExpiresUtc = DateTimeOffset.UtcNow.AddHours(8)
            });
        Response.Cookies.Delete(LegacyAuthCookieName);
        model.TargetRole = targetRole;
        var portalRole = string.Equals(user.Role, "Admin", StringComparison.OrdinalIgnoreCase)
            ? "Admin"
            : "Staff";

        if (!string.IsNullOrWhiteSpace(model.ReturnUrl) && Url.IsLocalUrl(model.ReturnUrl))
        {
            return LocalRedirect(WithPortal(model.ReturnUrl, portalRole));
        }

        return user.Role == "Admin"
            ? RedirectToAction("Admin", "Home", new { portal = portalRole })
            : RedirectToAction("Staff", "Home", new { portal = portalRole });
    }

    [HttpGet]
    [AllowAnonymous]
    public IActionResult Register()
    {
        return View(new RegisterViewModel());
    }

    [HttpPost]
    [AllowAnonymous]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Register(RegisterViewModel model, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return View(model);
        }

        var result = await _userAccountService.RegisterAsync(model, cancellationToken);
        if (!result.Succeeded)
        {
            ModelState.AddModelError(string.Empty, result.ErrorMessage ?? "Đăng ký thất bại.");
            return View(model);
        }

        TempData["AuthMessage"] = "Đăng ký thanh cong. Hay dang nhap de tiep tuc.";
        return RedirectToAction(nameof(Login));
    }

    [Authorize(AuthenticationSchemes = AdminAuthScheme + "," + StaffAuthScheme)]
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Logout(string? schemeRole = null)
    {
        if (string.Equals(schemeRole, "Admin", StringComparison.OrdinalIgnoreCase))
        {
            await HttpContext.SignOutAsync(AdminAuthScheme);
        }
        else if (string.Equals(schemeRole, "Staff", StringComparison.OrdinalIgnoreCase))
        {
            await HttpContext.SignOutAsync(StaffAuthScheme);
        }
        else
        {
            await HttpContext.SignOutAsync(AdminAuthScheme);
            await HttpContext.SignOutAsync(StaffAuthScheme);
        }

        Response.Cookies.Delete(LegacyAuthCookieName);
        return RedirectToAction("Index", "Home");
    }

    private static string? ResolveTargetRole(string? returnUrl, string? targetRole)
    {
        if (string.Equals(targetRole, "Admin", StringComparison.OrdinalIgnoreCase))
        {
            return "Admin";
        }

        if (string.Equals(targetRole, "Staff", StringComparison.OrdinalIgnoreCase))
        {
            return "Staff";
        }

        if (string.IsNullOrWhiteSpace(returnUrl))
        {
            return null;
        }

        if (returnUrl.StartsWith("/Home/Admin", StringComparison.OrdinalIgnoreCase) ||
            returnUrl.StartsWith("/Admin", StringComparison.OrdinalIgnoreCase))
        {
            return "Admin";
        }

        if (returnUrl.StartsWith("/Home/Staff", StringComparison.OrdinalIgnoreCase) ||
            returnUrl.StartsWith("/Home/Restaurant", StringComparison.OrdinalIgnoreCase) ||
            returnUrl.StartsWith("/Staff", StringComparison.OrdinalIgnoreCase) ||
            returnUrl.StartsWith("/Restaurant", StringComparison.OrdinalIgnoreCase))
        {
            return "Staff";
        }

        return null;
    }

    private static string WithPortal(string returnUrl, string portalRole)
    {
        var fragment = string.Empty;
        var fragmentIndex = returnUrl.IndexOf('#');
        if (fragmentIndex >= 0)
        {
            fragment = returnUrl[fragmentIndex..];
            returnUrl = returnUrl[..fragmentIndex];
        }

        var query = string.Empty;
        var path = returnUrl;
        var queryIndex = returnUrl.IndexOf('?');
        if (queryIndex >= 0)
        {
            path = returnUrl[..queryIndex];
            query = returnUrl[(queryIndex + 1)..];
        }

        var queryPairs = QueryHelpers.ParseQuery(query)
            .Where(x => !string.Equals(x.Key, "portal", StringComparison.OrdinalIgnoreCase))
            .SelectMany(x => x.Value.Select(value => new KeyValuePair<string, string?>(x.Key, value)));
        var mergedQuery = QueryString.Create(queryPairs.Append(new KeyValuePair<string, string?>("portal", portalRole)));

        return $"{path}{mergedQuery.ToUriComponent()}{fragment}";
    }

    private static bool CanLoginForTarget(string userRole, string? targetRole)
    {
        if (string.IsNullOrWhiteSpace(targetRole))
        {
            return true;
        }

        if (string.Equals(userRole, "Admin", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return string.Equals(userRole, targetRole, StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildRoleMismatchMessage(string? targetRole)
    {
        return string.Equals(targetRole, "Admin", StringComparison.OrdinalIgnoreCase)
            ? "Tai khoan nay khong co quyen Quan tri. Hay dang nhap tai khoan Admin cho trang Admin."
            : "Tai khoan nay khong phai Nhan vien. Hay dang nhap tai khoan Nhan vien cho trang Nhan vien.";
    }

    [HttpGet]
    [AllowAnonymous]
    public IActionResult AccessDenied()
    {
        return View();
    }
}
