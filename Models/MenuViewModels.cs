using System.ComponentModel.DataAnnotations;

namespace TableOrderWeb.Models;

public sealed class CustomerPageViewModel
{
    public string TableCode { get; set; } = "A01";
    public string MenuUrl { get; set; } = string.Empty;
    public string? StatusMessage { get; set; }
    public List<MenuCategoryViewModel> Categories { get; set; } = new();
    public List<MenuDishViewModel> Dishes { get; set; } = new();
    public List<CustomerCartItemViewModel> CartItems { get; set; } = new();
    public List<OrderTimelineEntryViewModel> Timeline { get; set; } = new();
    public decimal CartSubtotal { get; set; }
    public decimal ServiceFee { get; set; }
    public decimal CartTotal { get; set; }
    public decimal SubmittedSubtotal { get; set; }
    public decimal SubmittedServiceFee { get; set; }
    public decimal SubmittedTotal { get; set; }
    public int SubmittedOrderCount { get; set; }
    public string? ErrorMessage { get; set; }
    public string SearchTerm { get; set; } = string.Empty;
    public string CategoryFilter { get; set; } = "all";
    public string SortBy { get; set; } = "popular";
    public List<MenuDishViewModel> RecommendedDishes { get; set; } = new();
    public List<SubmittedOrderSummaryViewModel> SubmittedOrders { get; set; } = new();
    public List<ChatMessageViewModel> ChatMessages { get; set; } = new();
    public PaymentRequestViewModel? LatestPaymentRequest { get; set; }
    public List<ReviewEntryViewModel> RecentReviews { get; set; } = new();
    public bool CanSubmitReview { get; set; }
    public string TableStateLabel { get; set; } = "Đang phục vụ";
}

public sealed class AdminMenuPageViewModel
{
    public List<MenuCategoryViewModel> Categories { get; set; } = new();
    public List<AdminMenuItemViewModel> MenuItems { get; set; } = new();
    public List<TableQrViewModel> TableQrs { get; set; } = new();
    public AdminMenuFormViewModel Form { get; set; } = new();
    public AdminCategoryFormViewModel CategoryForm { get; set; } = new();
    public string? StatusMessage { get; set; }
    public string? ErrorMessage { get; set; }
    public AdminOperationsDashboardViewModel Operations { get; set; } = new();
    public bool IsEditing => Form.ItemId.HasValue;
}

public sealed class MenuCategoryViewModel
{
    public int CategoryId { get; set; }
    public string CategoryCode { get; set; } = string.Empty;
    public string CategoryName { get; set; } = string.Empty;
    public int DisplayOrder { get; set; }
}

public sealed class AdminCategoryFormViewModel
{
    [StringLength(30, ErrorMessage = "Mã danh mục tối đa 30 ký tự.")]
    public string CategoryCode { get; set; } = string.Empty;

    [Required(ErrorMessage = "Hãy nhập tên danh mục.")]
    [StringLength(100, ErrorMessage = "Tên danh mục tối đa 100 ký tự.")]
    public string CategoryName { get; set; } = string.Empty;

    [Range(1, 999, ErrorMessage = "Thứ tự hiển thị từ 1 đến 999.")]
    public int DisplayOrder { get; set; } = 1;
}

public sealed class MenuDishViewModel
{
    public int ItemId { get; set; }
    public int CategoryId { get; set; }
    public string CategoryCode { get; set; } = string.Empty;
    public string CategoryName { get; set; } = string.Empty;
    public string ItemCode { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public decimal Price { get; set; }
    public int PreparationTimeMinutes { get; set; }
    public bool IsBestSeller { get; set; }
    public bool IsAvailable { get; set; } = true;
    public int PopularityScore { get; set; }
    public int SpiceLevel { get; set; }
    public string Accent { get; set; } = "warm";
    public string ImageUrl { get; set; } = string.Empty;
    public string IngredientSummary { get; set; } = string.Empty;
    public string ServingSuggestion { get; set; } = string.Empty;
    public string CustomizationSummary { get; set; } = string.Empty;
}

public sealed class AdminMenuItemViewModel
{
    public int ItemId { get; set; }
    public int CategoryId { get; set; }
    public string CategoryName { get; set; } = string.Empty;
    public string ItemCode { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public decimal Price { get; set; }
    public string ImageUrl { get; set; } = string.Empty;
    public int PreparationTimeMinutes { get; set; }
    public bool IsBestSeller { get; set; }
    public bool IsAvailable { get; set; }
    public int PopularityScore { get; set; }
    public int SpiceLevel { get; set; }
}

public sealed class AdminMenuFormViewModel
{
    public int? ItemId { get; set; }

    [Required(ErrorMessage = "Hãy chọn danh mục.")]
    [Range(1, int.MaxValue, ErrorMessage = "Danh mục không hợp lệ.")]
    public int CategoryId { get; set; }

    [StringLength(30, ErrorMessage = "Mã món tối đa 30 ký tự.")]
    public string ItemCode { get; set; } = string.Empty;

    [Required(ErrorMessage = "Hãy nhập tên món.")]
    [StringLength(150, ErrorMessage = "Tên món tối đa 150 ký tự.")]
    public string Name { get; set; } = string.Empty;

    [StringLength(500, ErrorMessage = "Mô tả tối đa 500 ký tự.")]
    public string Description { get; set; } = string.Empty;

    [Range(typeof(decimal), "1000", "999999999", ErrorMessage = "Giá món không hợp lệ.")]
    public decimal Price { get; set; }

    [StringLength(255, ErrorMessage = "Link ảnh tối đa 255 ký tự.")]
    public string ImageUrl { get; set; } = string.Empty;

    public Microsoft.AspNetCore.Http.IFormFile? ImageFile { get; set; }

    [Range(1, 240, ErrorMessage = "Thoi gian che bien tu 1 den 240 phút.")]
    public int PreparationTimeMinutes { get; set; } = 10;

    [Range(0, 100000, ErrorMessage = "Điểm phổ biến không hợp lệ.")]
    public int PopularityScore { get; set; }

    [Range(0, 5, ErrorMessage = "Muc cay tu 0 den 5 qua ot.")]
    public int SpiceLevel { get; set; }

    public bool IsBestSeller { get; set; }
    public bool IsAvailable { get; set; } = true;
    public bool IsSoldOut { get; set; }
}

public sealed class AdminGalleryImageInputModel
{
    [Range(1, 6, ErrorMessage = "Slot ảnh không hợp lệ.")]
    public int Slot { get; set; }

    public Microsoft.AspNetCore.Http.IFormFile? Image { get; set; }
}

public sealed class MenuAdminOperationResult
{
    public bool Succeeded { get; set; }
    public string? ErrorMessage { get; set; }
    public int? ItemId { get; set; }
    public string ItemCode { get; set; } = string.Empty;
    public int? CategoryId { get; set; }
    public string CategoryCode { get; set; } = string.Empty;
}
