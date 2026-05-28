using System.ComponentModel.DataAnnotations;

namespace TableOrderWeb.Models;

public sealed class RegisterViewModel
{
    [Required(ErrorMessage = "Nhập tên hiển thị.")]
    public string DisplayName { get; set; } = string.Empty;

    [Required(ErrorMessage = "Nhập tên đăng nhập.")]
    public string UserName { get; set; } = string.Empty;

    [Required(ErrorMessage = "Nhập mật khẩu.")]
    [DataType(DataType.Password)]
    [MinLength(6, ErrorMessage = "Mật khẩu tối thiểu 6 ký tự.")]
    public string Password { get; set; } = string.Empty;

    [Required(ErrorMessage = "Nhập lại mật khẩu.")]
    [DataType(DataType.Password)]
    [Compare(nameof(Password), ErrorMessage = "Mật khẩu nhập lại không khớp.")]
    public string ConfirmPassword { get; set; } = string.Empty;

    [Required(ErrorMessage = "Chọn vai trò.")]
    public string Role { get; set; } = "Staff";
}
