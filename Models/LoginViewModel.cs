using System.ComponentModel.DataAnnotations;

namespace TableOrderWeb.Models;

public sealed class LoginViewModel
{
    [Required(ErrorMessage = "Nhập tên đăng nhập.")]
    public string UserName { get; set; } = string.Empty;

    [Required(ErrorMessage = "Nhập mật khẩu.")]
    [DataType(DataType.Password)]
    public string Password { get; set; } = string.Empty;

    public string? ReturnUrl { get; set; }

    public string? TargetRole { get; set; }
}
