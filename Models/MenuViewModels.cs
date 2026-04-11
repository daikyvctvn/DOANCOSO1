using System.ComponentModel.DataAnnotations;

namespace TableOrderWeb.Models;

public sealed class CustomerPageViewModel
{
    public List<MenuCategoryViewModel> Categories { get; set; } = new();
    public List<MenuDishViewModel> Dishes { get; set; } = new();
    public string? ErrorMessage { get; set; }
}

public sealed class AdminMenuPageViewModel
{
    public List<MenuCategoryViewModel> Categories { get; set; } = new();
    public List<AdminMenuItemViewModel> MenuItems { get; set; } = new();
    public AdminMenuFormViewModel Form { get; set; } = new();
    public string? StatusMessage { get; set; }
    public string? ErrorMessage { get; set; }
    public bool IsEditing => Form.ItemId.HasValue;
}

public sealed class MenuCategoryViewModel
{
    public int CategoryId { get; set; }
    public string CategoryCode { get; set; } = string.Empty;
    public string CategoryName { get; set; } = string.Empty;
    public int DisplayOrder { get; set; }
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
    public int PopularityScore { get; set; }
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
}

public sealed class AdminMenuFormViewModel
{
    public int? ItemId { get; set; }

    [Required(ErrorMessage = "Hay chon danh muc.")]
    [Range(1, int.MaxValue, ErrorMessage = "Danh muc khong hop le.")]
    public int CategoryId { get; set; }

    [Required(ErrorMessage = "Hay nhap ma mon.")]
    [StringLength(30, ErrorMessage = "Ma mon toi da 30 ky tu.")]
    public string ItemCode { get; set; } = string.Empty;

    [Required(ErrorMessage = "Hay nhap ten mon.")]
    [StringLength(150, ErrorMessage = "Ten mon toi da 150 ky tu.")]
    public string Name { get; set; } = string.Empty;

    [StringLength(500, ErrorMessage = "Mo ta toi da 500 ky tu.")]
    public string Description { get; set; } = string.Empty;

    [Range(typeof(decimal), "1000", "999999999", ErrorMessage = "Gia mon khong hop le.")]
    public decimal Price { get; set; }

    [StringLength(255, ErrorMessage = "Link anh toi da 255 ky tu.")]
    public string ImageUrl { get; set; } = string.Empty;

    [Range(1, 240, ErrorMessage = "Thoi gian che bien tu 1 den 240 phut.")]
    public int PreparationTimeMinutes { get; set; } = 10;

    [Range(0, 100000, ErrorMessage = "Diem pho bien khong hop le.")]
    public int PopularityScore { get; set; }

    public bool IsBestSeller { get; set; }
    public bool IsAvailable { get; set; } = true;
}

public sealed class MenuAdminOperationResult
{
    public bool Succeeded { get; set; }
    public string? ErrorMessage { get; set; }
    public int? ItemId { get; set; }
}
