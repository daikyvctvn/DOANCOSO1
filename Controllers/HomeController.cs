using Microsoft.AspNetCore.Authorization;
using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using TableOrderWeb.Models;
using TableOrderWeb.Services;

namespace TableOrderWeb.Controllers;

public class HomeController : Controller
{
    private readonly IMenuRepository _menuRepository;
    private readonly IMenuAdminService _menuAdminService;

    public HomeController(IMenuRepository menuRepository, IMenuAdminService menuAdminService)
    {
        _menuRepository = menuRepository;
        _menuAdminService = menuAdminService;
    }

    [AllowAnonymous]
    public IActionResult Index()
    {
        return View();
    }

    [AllowAnonymous]
    public async Task<IActionResult> Customer(CancellationToken cancellationToken)
    {
        var model = await _menuRepository.GetCustomerMenuAsync(cancellationToken);
        return View(model);
    }

    [Authorize(Roles = "Staff,Admin")]
    public IActionResult Staff()
    {
        return View();
    }

    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Admin(int? editItemId, CancellationToken cancellationToken)
    {
        var model = await _menuAdminService.GetAdminMenuPageAsync(editItemId, cancellationToken);
        model.StatusMessage = TempData["AdminStatusMessage"] as string;
        model.ErrorMessage ??= TempData["AdminErrorMessage"] as string;
        return View(model);
    }

    [HttpPost]
    [Authorize(Roles = "Admin")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveMenuItem(AdminMenuFormViewModel form, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            var model = await _menuAdminService.GetAdminMenuPageAsync(form.ItemId, cancellationToken);
            model.Form = form;
            model.ErrorMessage = string.Join(" ", ModelState.Values
                .SelectMany(x => x.Errors)
                .Select(x => x.ErrorMessage)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct());
            return View("Admin", model);
        }

        var result = await _menuAdminService.SaveMenuItemAsync(form, cancellationToken);
        if (!result.Succeeded)
        {
            var model = await _menuAdminService.GetAdminMenuPageAsync(form.ItemId, cancellationToken);
            model.Form = form;
            model.ErrorMessage = result.ErrorMessage ?? "Khong luu duoc mon an.";
            return View("Admin", model);
        }

        TempData["AdminStatusMessage"] = form.ItemId.HasValue
            ? "Da cap nhat mon an thanh cong."
            : "Da them mon moi thanh cong.";

        return RedirectToAction(nameof(Admin), new { editItemId = result.ItemId });
    }

    [HttpPost]
    [Authorize(Roles = "Admin")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteMenuItem(int itemId, CancellationToken cancellationToken)
    {
        var result = await _menuAdminService.DeleteMenuItemAsync(itemId, cancellationToken);
        if (!result.Succeeded)
        {
            TempData["AdminErrorMessage"] = result.ErrorMessage ?? "Khong xoa duoc mon an.";
            return RedirectToAction(nameof(Admin), new { editItemId = itemId });
        }

        TempData["AdminStatusMessage"] = "Da xoa mon an thanh cong.";
        return RedirectToAction(nameof(Admin));
    }

    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error()
    {
        return View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
    }
}
