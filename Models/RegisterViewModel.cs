using System.ComponentModel.DataAnnotations;

namespace TableOrderWeb.Models;

public sealed class RegisterViewModel
{
    [Required(ErrorMessage = "Nhap ten hien thi.")]
    public string DisplayName { get; set; } = string.Empty;

    [Required(ErrorMessage = "Nhap ten dang nhap.")]
    public string UserName { get; set; } = string.Empty;

    [Required(ErrorMessage = "Nhap mat khau.")]
    [DataType(DataType.Password)]
    [MinLength(6, ErrorMessage = "Mat khau toi thieu 6 ky tu.")]
    public string Password { get; set; } = string.Empty;

    [Required(ErrorMessage = "Nhap lai mat khau.")]
    [DataType(DataType.Password)]
    [Compare(nameof(Password), ErrorMessage = "Mat khau nhap lai khong khop.")]
    public string ConfirmPassword { get; set; } = string.Empty;

    [Required(ErrorMessage = "Chon vai tro.")]
    public string Role { get; set; } = "Staff";
}
