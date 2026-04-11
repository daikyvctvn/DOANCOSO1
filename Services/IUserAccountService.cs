using TableOrderWeb.Models;

namespace TableOrderWeb.Services;

public interface IUserAccountService
{
    Task<(bool Succeeded, string? ErrorMessage)> RegisterAsync(RegisterViewModel model, CancellationToken cancellationToken = default);
    Task<AppUser?> ValidateLoginAsync(string userName, string password, CancellationToken cancellationToken = default);
}
