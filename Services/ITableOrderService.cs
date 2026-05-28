using TableOrderWeb.Models;

namespace TableOrderWeb.Services;

public interface ITableOrderService
{
    string NormalizeTableCode(string? tableCode);
    IReadOnlyList<string> GetTableCodes();
    Task ApplyCustomerSessionAsync(CustomerPageViewModel model, CancellationToken cancellationToken = default);
    Task<(bool Succeeded, string? ErrorMessage)> AddItemAsync(CustomerAddToCartInputModel request, MenuDishViewModel dish, CancellationToken cancellationToken = default);
    Task<(bool Succeeded, string? ErrorMessage, string? RemovedItemName)> RemoveItemAsync(CustomerRemoveCartItemInputModel request, CancellationToken cancellationToken = default);
    Task<(bool Succeeded, string? ErrorMessage, string? OrderCode)> SubmitOrderAsync(string tableCode, CancellationToken cancellationToken = default);
    Task<(bool Succeeded, string? ErrorMessage)> SendCustomerMessageAsync(CustomerChatInputModel request, CancellationToken cancellationToken = default);
    Task<(bool Succeeded, string? ErrorMessage)> RequestPaymentAsync(CustomerPaymentRequestInputModel request, CancellationToken cancellationToken = default);
    Task<(bool Succeeded, string? ErrorMessage)> DirectCheckoutAsync(RestaurantCheckoutInputModel request, CancellationToken cancellationToken = default);
    Task<(bool Succeeded, string? ErrorMessage)> SubmitReviewAsync(CustomerReviewInputModel request, CancellationToken cancellationToken = default);
    Task<StaffPageViewModel> GetStaffDashboardAsync(CancellationToken cancellationToken = default);
    Task<(bool Succeeded, string? ErrorMessage)> UpdateOrderStatusAsync(StaffOrderStatusInputModel request, CancellationToken cancellationToken = default);
    Task<(bool Succeeded, string? ErrorMessage)> SendStaffMessageAsync(StaffChatInputModel request, CancellationToken cancellationToken = default);
    Task<(bool Succeeded, string? ErrorMessage)> DeleteTableChatAsync(string tableCode, CancellationToken cancellationToken = default);
    Task<(bool Succeeded, string? ErrorMessage)> UpdateTableStateAsync(StaffTableStateInputModel request, CancellationToken cancellationToken = default);
    Task<RestaurantPageViewModel> GetRestaurantDashboardAsync(CancellationToken cancellationToken = default);
    Task<AdminOperationsDashboardViewModel> GetAdminDashboardAsync(CancellationToken cancellationToken = default);
    Task<(bool Succeeded, string? ErrorMessage, decimal Revenue, int OrderCount)> CloseCurrentShiftAsync(string closedBy, CancellationToken cancellationToken = default);
    Task<(bool Succeeded, string? ErrorMessage, decimal Revenue, int OrderCount)> CloseCurrentDayAsync(string closedBy, CancellationToken cancellationToken = default);
}
