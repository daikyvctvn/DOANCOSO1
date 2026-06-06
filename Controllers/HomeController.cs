using Microsoft.AspNetCore.Authorization;
using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using TableOrderWeb.Models;
using TableOrderWeb.Services;

namespace TableOrderWeb.Controllers;

public class HomeController : Controller
{
    private const string AdminAuthScheme = "TableOrderWeb.AdminAuth";
    private const string StaffAuthScheme = "TableOrderWeb.StaffAuth";
    private const string StaffOrAdminAuthSchemes = StaffAuthScheme + "," + AdminAuthScheme;
    private const string StaffOrAdminRoles = "Staff,Admin";
    private const string CustomerTableCookieName = "TableOrderWeb.CustomerTable";

    private readonly IConfiguration _configuration;
    private readonly IWebHostEnvironment _environment;
    private readonly IMenuRepository _menuRepository;
    private readonly IMenuAdminService _menuAdminService;
    private readonly ITableOrderService _tableOrderService;

    public HomeController(IConfiguration configuration, IWebHostEnvironment environment, IMenuRepository menuRepository, IMenuAdminService menuAdminService, ITableOrderService tableOrderService)
    {
        _configuration = configuration;
        _environment = environment;
        _menuRepository = menuRepository;
        _menuAdminService = menuAdminService;
        _tableOrderService = tableOrderService;
    }

    [AllowAnonymous]
    public IActionResult Index()
    {
        return View();
    }

    [AllowAnonymous]
    public async Task<IActionResult> Customer(string? tableCode, string? search, string? category, string? sort, CancellationToken cancellationToken)
    {
        var tableAccess = ResolveCustomerTableAccess(tableCode);
        if (tableAccess.Redirect is not null)
        {
            return tableAccess.Redirect;
        }

        if (await EnsureCustomerTableAvailableAsync(tableAccess.TableCode, cancellationToken) is { } blocked)
        {
            return blocked;
        }

        var model = await _menuRepository.GetCustomerMenuAsync(cancellationToken);
        var allDishes = model.Dishes.ToList();
        model.TableCode = tableAccess.TableCode;
        model.MenuUrl = BuildCustomerUrl(model.TableCode);
        await _tableOrderService.ApplyCustomerSessionAsync(model, cancellationToken);
        model.SearchTerm = (search ?? string.Empty).Trim();
        model.CategoryFilter = string.IsNullOrWhiteSpace(category) ? "all" : category.Trim();
        model.SortBy = string.IsNullOrWhiteSpace(sort) ? "popular" : sort.Trim().ToLowerInvariant();
        model.RecommendedDishes = BuildRecommendedDishes(allDishes, model.CartItems);
        model.Dishes = ApplyDishFilters(allDishes, model.SearchTerm, model.CategoryFilter, model.SortBy);
        model.StatusMessage = TempData["CustomerStatusMessage"] as string;
        model.ErrorMessage ??= TempData["CustomerErrorMessage"] as string;
        return View(model);
    }

    [AllowAnonymous]
    public async Task<IActionResult> Table(string? tableCode, CancellationToken cancellationToken)
    {
        var normalizedTableCode = TryNormalizeTableCode(tableCode);
        if (normalizedTableCode is null)
        {
            TempData["CustomerErrorMessage"] = "Mã bàn không hợp lệ. Hãy quét đúng QR tại bàn.";
            return RedirectToAction(nameof(Index));
        }

        if (await EnsureCustomerTableAvailableAsync(normalizedTableCode, cancellationToken) is { } blocked)
        {
            return blocked;
        }

        return RedirectToActionPermanent(nameof(Customer), new { tableCode = normalizedTableCode });
    }

    [Authorize(AuthenticationSchemes = StaffOrAdminAuthSchemes, Roles = StaffOrAdminRoles)]
    public async Task<IActionResult> Staff(CancellationToken cancellationToken)
    {
        var model = await _tableOrderService.GetStaffDashboardAsync(cancellationToken);
        model.StatusMessage = TempData["StaffStatusMessage"] as string;
        model.ErrorMessage = TempData["StaffErrorMessage"] as string;
        return View(model);
    }

    [Authorize(AuthenticationSchemes = StaffOrAdminAuthSchemes, Roles = StaffOrAdminRoles)]
    public async Task<IActionResult> Restaurant(CancellationToken cancellationToken)
    {
        var model = await _tableOrderService.GetRestaurantDashboardAsync(cancellationToken);
        model.StatusMessage = TempData["RestaurantStatusMessage"] as string;
        model.ErrorMessage = TempData["RestaurantErrorMessage"] as string;
        return View(model);
    }

    [Authorize(AuthenticationSchemes = AdminAuthScheme, Roles = "Admin")]
    public async Task<IActionResult> Admin(int? editItemId, CancellationToken cancellationToken)
    {
        if (IsPortalRole("Staff"))
        {
            return RedirectToAction("AccessDenied", "Account");
        }

        var model = await _menuAdminService.GetAdminMenuPageAsync(editItemId, cancellationToken);
        model.Operations = await _tableOrderService.GetAdminDashboardAsync(cancellationToken);
        await PopulateAdminQrCodesAsync(model, cancellationToken);
        model.StatusMessage = TempData["AdminStatusMessage"] as string;
        model.ErrorMessage ??= TempData["AdminErrorMessage"] as string;
        return View(model);
    }

    [HttpPost]
    [AllowAnonymous]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddToCart(CustomerAddToCartInputModel model, CancellationToken cancellationToken)
    {
        var tableAccess = ResolveCustomerTableAccess(model.TableCode);
        if (tableAccess.Redirect is not null)
        {
            return tableAccess.Redirect;
        }

        if (await EnsureCustomerTableAvailableAsync(tableAccess.TableCode, cancellationToken) is { } blocked)
        {
            return blocked;
        }

        var normalizedTableCode = tableAccess.TableCode;
        model.TableCode = normalizedTableCode;
        var menu = await _menuRepository.GetCustomerMenuAsync(cancellationToken);
        var dish = menu.Dishes.FirstOrDefault(x => x.ItemId == model.ItemId);

        if (dish is null)
        {
            TempData["CustomerErrorMessage"] = "Không tìm thấy món để thêm vào giỏ.";
            return RedirectToAction(nameof(Customer), new { tableCode = normalizedTableCode });
        }

        var result = await _tableOrderService.AddItemAsync(model, dish, cancellationToken);
        if (!result.Succeeded)
        {
            TempData["CustomerErrorMessage"] = result.ErrorMessage ?? "Không thể thêm món vào giỏ.";
            return RedirectToAction(nameof(Customer), new { tableCode = normalizedTableCode });
        }

        TempData["CustomerStatusMessage"] = $"Đã thêm {dish.Name} vào giỏ của bàn {normalizedTableCode}.";
        return RedirectToAction(nameof(Customer), new { tableCode = normalizedTableCode });
    }

    [HttpPost]
    [AllowAnonymous]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RemoveCartItem(CustomerRemoveCartItemInputModel model, CancellationToken cancellationToken)
    {
        var tableAccess = ResolveCustomerTableAccess(model.TableCode);
        if (tableAccess.Redirect is not null)
        {
            return tableAccess.Redirect;
        }

        if (await EnsureCustomerTableAvailableAsync(tableAccess.TableCode, cancellationToken) is { } blocked)
        {
            return blocked;
        }

        var normalizedTableCode = tableAccess.TableCode;
        model.TableCode = normalizedTableCode;
        var result = await _tableOrderService.RemoveItemAsync(model, cancellationToken);

        if (!result.Succeeded)
        {
            TempData["CustomerErrorMessage"] = result.ErrorMessage ?? "Không hủy được món trong giỏ.";
            return RedirectToAction(nameof(Customer), new { tableCode = normalizedTableCode });
        }

        TempData["CustomerStatusMessage"] = $"Đã hủy {result.RemovedItemName} khỏi giỏ của bàn {normalizedTableCode}.";
        return RedirectToAction(nameof(Customer), new { tableCode = normalizedTableCode });
    }

    [HttpPost]
    [AllowAnonymous]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SubmitOrder(CustomerSubmitOrderInputModel model, CancellationToken cancellationToken)
    {
        var tableAccess = ResolveCustomerTableAccess(model.TableCode);
        if (tableAccess.Redirect is not null)
        {
            return tableAccess.Redirect;
        }

        if (await EnsureCustomerTableAvailableAsync(tableAccess.TableCode, cancellationToken) is { } blocked)
        {
            return blocked;
        }

        var normalizedTableCode = tableAccess.TableCode;
        var result = await _tableOrderService.SubmitOrderAsync(normalizedTableCode, cancellationToken);

        if (!result.Succeeded)
        {
            TempData["CustomerErrorMessage"] = result.ErrorMessage ?? "Không gửi được order.";
            return RedirectToAction(nameof(Customer), new { tableCode = normalizedTableCode });
        }

        TempData["CustomerStatusMessage"] = $"Đã gửi order #{result.OrderCode} cho bàn {normalizedTableCode}.";
        return RedirectToAction(nameof(Customer), new { tableCode = normalizedTableCode });
    }

    [HttpPost]
    [AllowAnonymous]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SendCustomerMessage(CustomerChatInputModel model, CancellationToken cancellationToken)
    {
        var tableAccess = ResolveCustomerTableAccess(model.TableCode);
        if (tableAccess.Redirect is not null)
        {
            return tableAccess.Redirect;
        }

        if (await EnsureCustomerTableAvailableAsync(tableAccess.TableCode, cancellationToken, IsAjaxRequest()) is { } blocked)
        {
            return blocked;
        }

        var normalizedTableCode = tableAccess.TableCode;
        model.TableCode = normalizedTableCode;
        var result = await _tableOrderService.SendCustomerMessageAsync(model, cancellationToken);
        if (IsAjaxRequest())
        {
            return Json(new
            {
                succeeded = result.Succeeded,
                errorMessage = result.ErrorMessage,
                messages = result.Succeeded
                    ? await _tableOrderService.GetCustomerChatMessagesAsync(normalizedTableCode, cancellationToken)
                    : Array.Empty<ChatMessageViewModel>().AsEnumerable()
            });
        }

        TempData[result.Succeeded ? "CustomerStatusMessage" : "CustomerErrorMessage"] = result.Succeeded
            ? "Đã gửi tin nhắn cho nhân viên."
            : result.ErrorMessage ?? "Không gửi được tin nhắn.";

        return RedirectToAction(nameof(Customer), new { tableCode = normalizedTableCode });
    }

    [HttpGet]
    [AllowAnonymous]
    public async Task<IActionResult> CustomerChatMessages(string tableCode, CancellationToken cancellationToken)
    {
        var tableAccess = ResolveCustomerTableAccess(tableCode);
        if (tableAccess.Redirect is not null)
        {
            return Unauthorized(new { messages = Array.Empty<ChatMessageViewModel>() });
        }

        if (await EnsureCustomerTableAvailableAsync(tableAccess.TableCode, cancellationToken, ajax: true) is { } blocked)
        {
            return blocked;
        }

        var messages = await _tableOrderService.GetCustomerChatMessagesAsync(tableAccess.TableCode, cancellationToken);
        return Json(new
        {
            tableCode = tableAccess.TableCode,
            count = messages.Count,
            messages
        });
    }

    [HttpPost]
    [AllowAnonymous]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RequestPayment(CustomerPaymentRequestInputModel model, CancellationToken cancellationToken)
    {
        var tableAccess = ResolveCustomerTableAccess(model.TableCode);
        if (tableAccess.Redirect is not null)
        {
            return tableAccess.Redirect;
        }

        if (await EnsureCustomerTableAvailableAsync(tableAccess.TableCode, cancellationToken) is { } blocked)
        {
            return blocked;
        }

        var normalizedTableCode = tableAccess.TableCode;
        model.TableCode = normalizedTableCode;
        var result = await _tableOrderService.RequestPaymentAsync(model, cancellationToken);

        if (!result.Succeeded)
        {
            TempData["CustomerErrorMessage"] = result.ErrorMessage ?? "Không gửi được yêu cầu thanh toán.";
            return RedirectToAction(nameof(Customer), new { tableCode = normalizedTableCode });
        }

        TempData["CustomerStatusMessage"] = $"Đã gửi yêu cầu thanh toán cho bàn {normalizedTableCode}.";
        return RedirectToAction(nameof(Customer), new { tableCode = normalizedTableCode });
    }

    [HttpPost]
    [Authorize(AuthenticationSchemes = StaffOrAdminAuthSchemes, Roles = StaffOrAdminRoles)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CheckoutTable(RestaurantCheckoutInputModel model, CancellationToken cancellationToken)
    {
        var normalizedTableCode = _tableOrderService.NormalizeTableCode(model.TableCode);
        var result = await _tableOrderService.DirectCheckoutAsync(model, cancellationToken);

        if (!result.Succeeded)
        {
            TempData["RestaurantErrorMessage"] = result.ErrorMessage ?? "Không thanh toán được cho bàn này.";
            return RedirectToAction(nameof(Restaurant), CurrentPortalRoute());
        }

        TempData["RestaurantStatusMessage"] = $"Đã thanh toán xong cho bàn {normalizedTableCode}.";
        return RedirectToAction(nameof(Restaurant), CurrentPortalRoute());
    }

    [HttpPost]
    [Authorize(AuthenticationSchemes = StaffOrAdminAuthSchemes, Roles = StaffOrAdminRoles)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> TransferTable(RestaurantTransferTableInputModel model, CancellationToken cancellationToken)
    {
        var result = await _tableOrderService.TransferTableAsync(model, cancellationToken);
        TempData[result.Succeeded ? "RestaurantStatusMessage" : "RestaurantErrorMessage"] = result.Succeeded
            ? $"Đã chuyển toàn bộ bàn {model.FromTableCode} sang bàn {model.ToTableCode}."
            : result.ErrorMessage ?? "Không chuyển được bàn.";
        return RedirectToAction(nameof(Restaurant), CurrentPortalRoute());
    }

    [HttpPost]
    [Authorize(AuthenticationSchemes = StaffOrAdminAuthSchemes, Roles = StaffOrAdminRoles)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SplitItemToTable(RestaurantSplitItemInputModel model, CancellationToken cancellationToken)
    {
        var result = await _tableOrderService.SplitItemToTableAsync(model, cancellationToken);
        TempData[result.Succeeded ? "RestaurantStatusMessage" : "RestaurantErrorMessage"] = result.Succeeded
            ? $"Đã tách món từ bàn {model.FromTableCode} sang bàn {model.ToTableCode}."
            : result.ErrorMessage ?? "Không tách được món.";
        return RedirectToAction(nameof(Restaurant), CurrentPortalRoute());
    }

    [HttpPost]
    [AllowAnonymous]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SubmitReview(CustomerReviewInputModel model, CancellationToken cancellationToken)
    {
        var tableAccess = ResolveCustomerTableAccess(model.TableCode);
        if (tableAccess.Redirect is not null)
        {
            return tableAccess.Redirect;
        }

        if (await EnsureCustomerTableAvailableAsync(tableAccess.TableCode, cancellationToken) is { } blocked)
        {
            return blocked;
        }

        var normalizedTableCode = tableAccess.TableCode;
        if (model.ServiceRating <= 0)
        {
            model.ServiceRating = model.FoodRating;
        }

        var result = await _tableOrderService.SubmitReviewAsync(model, cancellationToken);
        TempData[result.Succeeded ? "CustomerStatusMessage" : "CustomerErrorMessage"] = result.Succeeded
            ? "Cam on ban da gui danh gia."
            : result.ErrorMessage ?? "Khong gui duoc danh gia.";
        return RedirectToAction(nameof(Customer), new { tableCode = normalizedTableCode });
    }

    [HttpPost]
    [Authorize(AuthenticationSchemes = StaffOrAdminAuthSchemes, Roles = StaffOrAdminRoles)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateOrderStatus(StaffOrderStatusInputModel model, CancellationToken cancellationToken)
    {
        var result = await _tableOrderService.UpdateOrderStatusAsync(model, cancellationToken);
        TempData[result.Succeeded ? "StaffStatusMessage" : "StaffErrorMessage"] = result.Succeeded
            ? "Đã cập nhật trạng thái đơn."
            : result.ErrorMessage ?? "Không cập nhật được đơn.";
        return RedirectToAction(nameof(Staff));
    }

    [HttpPost]
    [Authorize(AuthenticationSchemes = StaffOrAdminAuthSchemes, Roles = StaffOrAdminRoles)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SendStaffMessage(StaffChatInputModel model, CancellationToken cancellationToken)
    {
        var result = await _tableOrderService.SendStaffMessageAsync(model, cancellationToken);
        if (IsAjaxRequest())
        {
            var threads = await _tableOrderService.GetStaffChatThreadsAsync(cancellationToken);
            return Json(new
            {
                succeeded = result.Succeeded,
                errorMessage = result.ErrorMessage,
                pendingCount = threads.Sum(x => x.PendingCount),
                threads
            });
        }

        TempData[result.Succeeded ? "StaffStatusMessage" : "StaffErrorMessage"] = result.Succeeded
            ? $"Đã gửi phản hồi cho bàn {model.TableCode}."
            : result.ErrorMessage ?? "Không gửi được phản hồi.";
        return RedirectToAction(nameof(Staff));
    }

    [HttpGet]
    [Authorize(AuthenticationSchemes = StaffOrAdminAuthSchemes, Roles = StaffOrAdminRoles)]
    public async Task<IActionResult> StaffChatThreads(CancellationToken cancellationToken)
    {
        var threads = await _tableOrderService.GetStaffChatThreadsAsync(cancellationToken);
        return Json(new
        {
            pendingCount = threads.Sum(x => x.PendingCount),
            threads
        });
    }

    [HttpPost]
    [Authorize(AuthenticationSchemes = StaffOrAdminAuthSchemes, Roles = StaffOrAdminRoles)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteTableChat(string tableCode, CancellationToken cancellationToken)
    {
        var normalizedTableCode = _tableOrderService.NormalizeTableCode(tableCode);
        var result = await _tableOrderService.DeleteTableChatAsync(normalizedTableCode, cancellationToken);
        TempData[result.Succeeded ? "StaffStatusMessage" : "StaffErrorMessage"] = result.Succeeded
            ? $"Đã xóa hội thoại bàn {normalizedTableCode}."
            : result.ErrorMessage ?? "Không xóa được hội thoại.";
        return RedirectToAction(nameof(Staff));
    }

    [HttpPost]
    [Authorize(AuthenticationSchemes = StaffOrAdminAuthSchemes, Roles = StaffOrAdminRoles)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateTableState(StaffTableStateInputModel model, CancellationToken cancellationToken)
    {
        var result = await _tableOrderService.UpdateTableStateAsync(model, cancellationToken);
        TempData[result.Succeeded ? "StaffStatusMessage" : "StaffErrorMessage"] = result.Succeeded
            ? $"Đã cập nhật trạng thái bàn {model.TableCode}."
            : result.ErrorMessage ?? "Không cập nhật được bàn.";
        return RedirectToAction(nameof(Staff));
    }

    [HttpPost]
    [Authorize(AuthenticationSchemes = AdminAuthScheme, Roles = "Admin")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CloseShift(CancellationToken cancellationToken)
    {
        var closedBy = User?.Identity?.Name ?? "Admin";
        var result = await _tableOrderService.CloseCurrentShiftAsync(closedBy, cancellationToken);
        TempData[result.Succeeded ? "AdminStatusMessage" : "AdminErrorMessage"] = result.Succeeded
            ? $"Đã kết ca: {result.OrderCount} đơn, tổng doanh thu {result.Revenue:N0}đ."
            : result.ErrorMessage ?? "Không kết ca được.";

        return RedirectToAction(nameof(Admin));
    }

    [HttpPost]
    [Authorize(AuthenticationSchemes = AdminAuthScheme, Roles = "Admin")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CloseDay(CancellationToken cancellationToken)
    {
        var closedBy = User?.Identity?.Name ?? "Admin";
        var result = await _tableOrderService.CloseCurrentDayAsync(closedBy, cancellationToken);
        TempData[result.Succeeded ? "AdminStatusMessage" : "AdminErrorMessage"] = result.Succeeded
            ? $"Đã kết ngày: {result.OrderCount} đơn, tổng doanh thu {result.Revenue:N0}đ. Các thông báo cũ đã được reset."
            : result.ErrorMessage ?? "Không kết ngày được.";

        return RedirectToAction(nameof(Admin));
    }

    [HttpPost]
    [Authorize(AuthenticationSchemes = AdminAuthScheme, Roles = "Admin")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveCategory(CancellationToken cancellationToken)
    {
        var form = ReadAdminCategoryFormFromRequest();
        ModelState.Clear();
        TryValidateModel(form, "CategoryForm");

        if (!ModelState.IsValid)
        {
            var model = await _menuAdminService.GetAdminMenuPageAsync(null, cancellationToken);
            model.Operations = await _tableOrderService.GetAdminDashboardAsync(cancellationToken);
            await PopulateAdminQrCodesAsync(model, cancellationToken);
            model.CategoryForm = form;
            model.ErrorMessage = string.Join(" ", ModelState.Values
                .SelectMany(x => x.Errors)
                .Select(x => x.ErrorMessage)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct());
            return View("Admin", model);
        }

        var result = await _menuAdminService.SaveCategoryAsync(form, cancellationToken);
        if (!result.Succeeded)
        {
            var model = await _menuAdminService.GetAdminMenuPageAsync(null, cancellationToken);
            model.Operations = await _tableOrderService.GetAdminDashboardAsync(cancellationToken);
            await PopulateAdminQrCodesAsync(model, cancellationToken);
            model.CategoryForm = form;
            model.ErrorMessage = $"Không lưu được danh mục. {result.ErrorMessage}".Trim();
            return View("Admin", model);
        }

        TempData["AdminStatusMessage"] = $"Đã thêm danh mục {result.CategoryCode}.";
        return RedirectToAction(nameof(Admin));
    }

    [HttpPost]
    [Authorize(AuthenticationSchemes = AdminAuthScheme, Roles = "Admin")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveMenuItem(CancellationToken cancellationToken)
    {
        var form = ReadAdminMenuFormFromRequest(forceCreate: false);
        return await SaveMenuItemCore(form, form.ImageFile, forceCreate: false, cancellationToken);
    }

    [HttpPost]
    [Authorize(AuthenticationSchemes = AdminAuthScheme, Roles = "Admin")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateMenuItem(CancellationToken cancellationToken)
    {
        var form = ReadAdminMenuFormFromRequest(forceCreate: true);

        return await SaveMenuItemCore(form, form.ImageFile, forceCreate: true, cancellationToken);
    }

    private async Task<IActionResult> SaveMenuItemCore(AdminMenuFormViewModel form, IFormFile? imageFile, bool forceCreate, CancellationToken cancellationToken)
    {
        ModelState.Clear();
        TryValidateModel(form, "Form");
        form.ImageFile ??= imageFile ?? Request.Form.Files.FirstOrDefault();
        foreach (var key in ModelState.Keys.Where(x => x.EndsWith("ImageFile", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            ModelState.Remove(key);
        }

        if (!ModelState.IsValid)
        {
            var model = await _menuAdminService.GetAdminMenuPageAsync(form.ItemId, cancellationToken);
            model.Operations = await _tableOrderService.GetAdminDashboardAsync(cancellationToken);
            await PopulateAdminQrCodesAsync(model, cancellationToken);
            model.Form = form;
            model.ErrorMessage = string.Join(" ", ModelState.Values
                .SelectMany(x => x.Errors)
                .Select(x => x.ErrorMessage)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct());
            return View("Admin", model);
        }

        var imageResult = await SaveMenuImageFileAsync(form, cancellationToken);
        if (!imageResult.Succeeded)
        {
            var model = await _menuAdminService.GetAdminMenuPageAsync(form.ItemId, cancellationToken);
            model.Operations = await _tableOrderService.GetAdminDashboardAsync(cancellationToken);
            await PopulateAdminQrCodesAsync(model, cancellationToken);
            model.Form = form;
            model.ErrorMessage = imageResult.ErrorMessage;
            return View("Admin", model);
        }

        var normalizedImageUrl = NormalizeMenuImageUrl(form.ImageUrl);
        if (normalizedImageUrl is null && !string.IsNullOrWhiteSpace(form.ImageUrl))
        {
            var model = await _menuAdminService.GetAdminMenuPageAsync(form.ItemId, cancellationToken);
            model.Operations = await _tableOrderService.GetAdminDashboardAsync(cancellationToken);
            await PopulateAdminQrCodesAsync(model, cancellationToken);
            model.Form = form;
            model.ErrorMessage = "Link ảnh phải là link trực tiếp đến file ảnh JPG, PNG, WEBP hoặc đường dẫn /images/...";
            return View("Admin", model);
        }

        form.ImageUrl = normalizedImageUrl ?? string.Empty;

        var result = await _menuAdminService.SaveMenuItemAsync(form, cancellationToken);
        if (!result.Succeeded)
        {
            var model = await _menuAdminService.GetAdminMenuPageAsync(form.ItemId, cancellationToken);
            model.Operations = await _tableOrderService.GetAdminDashboardAsync(cancellationToken);
            await PopulateAdminQrCodesAsync(model, cancellationToken);
            model.Form = form;
            model.ErrorMessage = $"Không lưu được món ăn. {result.ErrorMessage}".Trim();
            return View("Admin", model);
        }

        TempData["AdminStatusMessage"] = form.ItemId.HasValue
            ? "Đã cập nhật món ăn thành công."
            : $"Đã thêm món mới {result.ItemCode} vào SQL, danh sách món và menu khách.";

        return RedirectToAction(nameof(Admin), forceCreate ? null : new { editItemId = result.ItemId });
    }

    private AdminMenuFormViewModel ReadAdminMenuFormFromRequest(bool forceCreate)
    {
        var values = Request.Form;
        var form = new AdminMenuFormViewModel
        {
            ItemId = forceCreate ? null : ParseNullableInt(values["Form.ItemId"].FirstOrDefault()),
            CategoryId = ParseInt(values["Form.CategoryId"].FirstOrDefault()),
            ItemCode = (values["Form.ItemCode"].FirstOrDefault() ?? string.Empty).Trim(),
            Name = (values["Form.Name"].FirstOrDefault() ?? string.Empty).Trim(),
            Description = (values["Form.Description"].FirstOrDefault() ?? string.Empty).Trim(),
            Price = ParseDecimal(values["Form.Price"].FirstOrDefault()),
            ImageUrl = (values["Form.ImageUrl"].FirstOrDefault() ?? string.Empty).Trim(),
            ImageFile = values.Files.FirstOrDefault(x => string.Equals(x.Name, "ImageFile", StringComparison.OrdinalIgnoreCase)),
            PreparationTimeMinutes = ParseInt(values["Form.PreparationTimeMinutes"].FirstOrDefault()),
            PopularityScore = ParseInt(values["Form.PopularityScore"].FirstOrDefault()),
            SpiceLevel = Math.Clamp(ParseInt(values["Form.SpiceLevel"].FirstOrDefault()), 0, 5),
            IsBestSeller = ParseBool(values["Form.IsBestSeller"]),
            IsAvailable = ParseBool(values["Form.IsAvailable"]),
            IsSoldOut = ParseBool(values["Form.IsSoldOut"])
        };

        if (form.PreparationTimeMinutes <= 0)
        {
            form.PreparationTimeMinutes = 10;
        }

        return form;
    }

    private AdminCategoryFormViewModel ReadAdminCategoryFormFromRequest()
    {
        var values = Request.Form;
        return new AdminCategoryFormViewModel
        {
            CategoryCode = (values["CategoryForm.CategoryCode"].FirstOrDefault() ?? string.Empty).Trim(),
            CategoryName = (values["CategoryForm.CategoryName"].FirstOrDefault() ?? string.Empty).Trim(),
            DisplayOrder = ParseInt(values["CategoryForm.DisplayOrder"].FirstOrDefault())
        };
    }

    private static int? ParseNullableInt(string? value)
        => int.TryParse(value, out var parsed) && parsed > 0 ? parsed : null;

    private static int ParseInt(string? value)
        => int.TryParse(value, out var parsed) ? parsed : 0;

    private static decimal ParseDecimal(string? value)
        => decimal.TryParse(value, out var parsed) ? parsed : 0;

    private static bool ParseBool(Microsoft.Extensions.Primitives.StringValues values)
        => values.Any(x => string.Equals(x, "true", StringComparison.OrdinalIgnoreCase) || string.Equals(x, "on", StringComparison.OrdinalIgnoreCase));

    [HttpPost]
    [Authorize(AuthenticationSchemes = AdminAuthScheme, Roles = "Admin")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteMenuItem(int itemId, CancellationToken cancellationToken)
    {
        var result = await _menuAdminService.DeleteMenuItemAsync(itemId, cancellationToken);
        if (!result.Succeeded)
        {
            TempData["AdminErrorMessage"] = result.ErrorMessage ?? "Không xóa được món ăn.";
            return RedirectToAction(nameof(Admin), new { editItemId = itemId });
        }

        TempData["AdminStatusMessage"] = "Đã xóa món ăn thành công.";
        return RedirectToAction(nameof(Admin));
    }

    private async Task<(bool Succeeded, string? ErrorMessage)> SaveMenuImageFileAsync(AdminMenuFormViewModel form, CancellationToken cancellationToken)
    {
        if (form.ImageFile is null || form.ImageFile.Length == 0)
        {
            return (true, null);
        }

        var extension = Path.GetExtension(form.ImageFile.FileName).ToLowerInvariant();
        var allowedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".webp" };
        if (!allowedExtensions.Contains(extension))
        {
            return (false, "Ảnh món chỉ nhận JPG, PNG hoặc WEBP.");
        }

        const long maxFileSize = 8 * 1024 * 1024;
        if (form.ImageFile.Length > maxFileSize)
        {
            return (false, "Ảnh món tối đa 8MB.");
        }

        var webRootPath = _environment.WebRootPath ?? Path.Combine(_environment.ContentRootPath, "wwwroot");
        var menuImagePath = Path.Combine(webRootPath, "images", "menu");
        Directory.CreateDirectory(menuImagePath);

        var itemCode = string.IsNullOrWhiteSpace(form.ItemCode)
            ? Guid.NewGuid().ToString("N")[..8]
            : new string(form.ItemCode.Trim().ToLowerInvariant()
                .Select(ch => char.IsLetterOrDigit(ch) ? ch : '-')
                .ToArray()).Trim('-');
        if (string.IsNullOrWhiteSpace(itemCode))
        {
            itemCode = Guid.NewGuid().ToString("N")[..8];
        }

        var fileName = $"{itemCode}-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}{extension}";
        var targetPath = Path.Combine(menuImagePath, fileName);
        await using var stream = System.IO.File.Create(targetPath);
        await form.ImageFile.CopyToAsync(stream, cancellationToken);
        form.ImageUrl = $"/images/menu/{fileName}";

        return (true, null);
    }

    private static string? NormalizeMenuImageUrl(string? value)
    {
        var imageUrl = (value ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(imageUrl))
        {
            return null;
        }

        if (imageUrl.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
        {
            imageUrl = $"https://{imageUrl}";
        }

        if (!imageUrl.StartsWith("/", StringComparison.Ordinal) &&
            !imageUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !imageUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            imageUrl = $"/{imageUrl.TrimStart('/')}";
        }

        var path = Uri.TryCreate(imageUrl, UriKind.Absolute, out var absoluteUri)
            ? absoluteUri.AbsolutePath
            : imageUrl.Split('?', '#')[0];
        var extension = Path.GetExtension(path);
        var allowedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png", ".webp", ".gif", ".svg" };

        return allowedExtensions.Contains(extension) ? imageUrl : null;
    }

    [HttpPost]
    [Authorize(AuthenticationSchemes = AdminAuthScheme, Roles = "Admin")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveGalleryImage(AdminGalleryImageInputModel model, CancellationToken cancellationToken)
    {
        if (model.Slot < 1 || model.Slot > 6)
        {
            TempData["AdminErrorMessage"] = "Slot ảnh không hợp lệ.";
            return RedirectToAction(nameof(Admin));
        }

        if (model.Image is null || model.Image.Length == 0)
        {
            TempData["AdminErrorMessage"] = "Hãy chọn file ảnh trước khi lưu.";
            return RedirectToAction(nameof(Admin));
        }

        var extension = Path.GetExtension(model.Image.FileName);
        if (!string.Equals(extension, ".jpg", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(extension, ".jpeg", StringComparison.OrdinalIgnoreCase))
        {
            TempData["AdminErrorMessage"] = "Anh slideshow hien nhan file JPG hoac JPEG.";
            return RedirectToAction(nameof(Admin));
        }

        const long maxFileSize = 8 * 1024 * 1024;
        if (model.Image.Length > maxFileSize)
        {
            TempData["AdminErrorMessage"] = "Ảnh tối đa 8MB.";
            return RedirectToAction(nameof(Admin));
        }

        var galleryPath = Path.Combine(_environment.WebRootPath, "images", "gallery");
        Directory.CreateDirectory(galleryPath);

        var targetPath = Path.Combine(galleryPath, $"anh-{model.Slot}.jpg");
        await using var stream = System.IO.File.Create(targetPath);
        await model.Image.CopyToAsync(stream, cancellationToken);

        TempData["AdminStatusMessage"] = $"Đã cập nhật ảnh slideshow số {model.Slot}.";
        return RedirectToAction(nameof(Admin));
    }

    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error()
    {
        return View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
    }

    private (string TableCode, IActionResult? Redirect) ResolveCustomerTableAccess(string? requestedTableCode)
    {
        var normalizedRequested = TryNormalizeTableCode(requestedTableCode);
        var isStaffOrAdmin = User.IsInRole("Staff") || User.IsInRole("Admin");

        if (isStaffOrAdmin)
        {
            if (normalizedRequested is not null)
            {
                return (normalizedRequested, null);
            }

            TempData["CustomerErrorMessage"] = "Mã bàn không hợp lệ.";
            return (string.Empty, RedirectToAction(nameof(Index)));
        }

        var pinnedTableCode = TryNormalizeTableCode(Request.Cookies[CustomerTableCookieName]);
        if (pinnedTableCode is not null)
        {
            if (normalizedRequested is null)
            {
                return (pinnedTableCode, RedirectToAction(nameof(Customer), new { tableCode = pinnedTableCode }));
            }

            if (!string.Equals(pinnedTableCode, normalizedRequested, StringComparison.OrdinalIgnoreCase))
            {
                SetCustomerTableCookie(normalizedRequested);
                return (normalizedRequested, null);
            }

            return (normalizedRequested, null);
        }

        if (normalizedRequested is null)
        {
            TempData["CustomerErrorMessage"] = "Hãy quét đúng QR tại bàn để mở menu.";
            return (string.Empty, RedirectToAction(nameof(Index)));
        }

        SetCustomerTableCookie(normalizedRequested);

        return (normalizedRequested, null);
    }

    private void SetCustomerTableCookie(string tableCode)
    {
        Response.Cookies.Append(CustomerTableCookieName, tableCode, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            Secure = Request.IsHttps,
            Expires = DateTimeOffset.UtcNow.AddHours(8)
        });
    }

    private async Task<IActionResult?> EnsureCustomerTableAvailableAsync(string tableCode, CancellationToken cancellationToken, bool ajax = false)
    {
        if (User.IsInRole("Staff") || User.IsInRole("Admin"))
        {
            return null;
        }

        var availability = await _tableOrderService.GetCustomerTableAvailabilityAsync(tableCode, cancellationToken);
        if (availability.IsAvailable)
        {
            return null;
        }

        Response.Cookies.Delete(CustomerTableCookieName);
        var message = $"Bàn {tableCode} hiện đang ở trạng thái \"{availability.StateLabel}\" nên tạm ngưng nhận khách. Vui lòng chọn bàn khác hoặc báo nhân viên.";
        if (ajax)
        {
            return Json(new
            {
                succeeded = false,
                errorMessage = message,
                tableCode,
                count = 0,
                messages = Array.Empty<ChatMessageViewModel>()
            });
        }

        TempData["CustomerErrorMessage"] = message;
        return RedirectToAction(nameof(Index));
    }

    private string? TryNormalizeTableCode(string? tableCode)
    {
        var normalized = (tableCode ?? string.Empty).Trim().ToUpperInvariant();
        return _tableOrderService.GetTableCodes().Contains(normalized, StringComparer.OrdinalIgnoreCase)
            ? normalized
            : null;
    }

    private async Task PopulateAdminQrCodesAsync(AdminMenuPageViewModel model, CancellationToken cancellationToken)
    {
        var tableQrs = new List<TableQrViewModel>();
        foreach (var tableCode in _tableOrderService.GetTableCodes())
        {
            var availability = await _tableOrderService.GetCustomerTableAvailabilityAsync(tableCode, cancellationToken);
            var menuUrl = BuildCustomerUrl(tableCode);
            var shortUrl = BuildShortCustomerUrl(tableCode);
            var qrImageUrl = $"https://api.qrserver.com/v1/create-qr-code/?size=220x220&data={Uri.EscapeDataString(shortUrl)}";

            tableQrs.Add(new TableQrViewModel
            {
                TableCode = tableCode,
                MenuUrl = menuUrl,
                ShortUrl = shortUrl,
                QrImageUrl = qrImageUrl,
                StateLabel = availability.StateLabel,
                BlocksCustomerAccess = !availability.IsAvailable
            });
        }

        model.TableQrs = tableQrs;
    }

    private string BuildCustomerUrl(string tableCode)
    {
        var configuredBaseUrl = (_configuration["TableQr:PublicBaseUrl"] ?? string.Empty).Trim().TrimEnd('/');
        if (!string.IsNullOrWhiteSpace(configuredBaseUrl))
        {
            return $"{configuredBaseUrl}/ban/{tableCode}";
        }

        return Url.Action(nameof(Customer), "Home", new { tableCode }, Request.Scheme)
            ?? $"{Request.Scheme}://{Request.Host}/ban/{tableCode}";
    }

    private string BuildShortCustomerUrl(string tableCode)
    {
        var configuredBaseUrl = (_configuration["TableQr:PublicBaseUrl"] ?? string.Empty).Trim().TrimEnd('/');
        if (!string.IsNullOrWhiteSpace(configuredBaseUrl))
        {
            return $"{configuredBaseUrl}/t/{tableCode}";
        }

        return Url.Action(nameof(Table), "Home", new { tableCode }, Request.Scheme)
            ?? $"{Request.Scheme}://{Request.Host}/t/{tableCode}";
    }

    private static List<MenuDishViewModel> ApplyDishFilters(IEnumerable<MenuDishViewModel> dishes, string searchTerm, string categoryFilter, string sortBy)
    {
        var query = dishes;
        if (!string.IsNullOrWhiteSpace(searchTerm))
        {
            query = query.Where(d =>
                d.Name.Contains(searchTerm, StringComparison.OrdinalIgnoreCase) ||
                d.Description.Contains(searchTerm, StringComparison.OrdinalIgnoreCase) ||
                d.ItemCode.Contains(searchTerm, StringComparison.OrdinalIgnoreCase) ||
                d.CategoryName.Contains(searchTerm, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.Equals(categoryFilter, "all", StringComparison.OrdinalIgnoreCase))
        {
            query = query.Where(d =>
                string.Equals(d.CategoryCode, categoryFilter, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(d.CategoryName, categoryFilter, StringComparison.OrdinalIgnoreCase));
        }

        return sortBy switch
        {
            "price-asc" => query.OrderBy(d => d.Price).ThenBy(d => d.Name).ToList(),
            "price-desc" => query.OrderByDescending(d => d.Price).ThenBy(d => d.Name).ToList(),
            "name" => query.OrderBy(d => d.Name).ToList(),
            _ => query.OrderByDescending(d => d.IsBestSeller)
                .ThenByDescending(d => d.PopularityScore)
                .ThenBy(d => d.CategoryId)
                .ThenByDescending(d => d.ItemId)
                .ThenBy(d => d.Name)
                .ToList()
        };
    }

    private static List<MenuDishViewModel> BuildRecommendedDishes(IEnumerable<MenuDishViewModel> dishes, IEnumerable<CustomerCartItemViewModel> cartItems)
    {
        var existingIds = cartItems.Select(x => x.ItemId).ToHashSet();
        return dishes
            .Where(d => !existingIds.Contains(d.ItemId))
            .OrderByDescending(d => d.IsBestSeller)
            .ThenByDescending(d => d.PopularityScore)
            .Take(4)
            .ToList();
    }

    private bool IsAjaxRequest()
        => string.Equals(Request.Headers["X-Requested-With"], "XMLHttpRequest", StringComparison.OrdinalIgnoreCase);

    private bool IsPortalRole(string role)
        => string.Equals(Request.Query["portal"].FirstOrDefault(), role, StringComparison.OrdinalIgnoreCase);

    private object? CurrentPortalRoute()
    {
        var portal = Request.Query["portal"].FirstOrDefault();
        return string.IsNullOrWhiteSpace(portal) ? null : new { portal };
    }
}
