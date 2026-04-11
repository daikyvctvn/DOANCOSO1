using TableOrderWeb.Models;

namespace TableOrderWeb.Services;

public interface IMenuAdminService
{
    Task<AdminMenuPageViewModel> GetAdminMenuPageAsync(int? editItemId = null, CancellationToken cancellationToken = default);
    Task<MenuAdminOperationResult> SaveMenuItemAsync(AdminMenuFormViewModel form, CancellationToken cancellationToken = default);
    Task<MenuAdminOperationResult> DeleteMenuItemAsync(int itemId, CancellationToken cancellationToken = default);
}
