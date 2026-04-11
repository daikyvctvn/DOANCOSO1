using System.ComponentModel.DataAnnotations;

namespace TableOrderWeb.Models;

public sealed class LoginViewModel
{
    [Required(ErrorMessage = "Nhap ten dang nhap.")]
    public string UserName { get; set; } = string.Empty;

    [Required(ErrorMessage = "Nhap mat khau.")]
    [DataType(DataType.Password)]
    public string Password { get; set; } = string.Empty;

    public string? ReturnUrl { get; set; }
}
