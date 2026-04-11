using TableOrderWeb.Models;

namespace TableOrderWeb.Services;

public interface IMenuRepository
{
    Task<CustomerPageViewModel> GetCustomerMenuAsync(CancellationToken cancellationToken = default);
}
