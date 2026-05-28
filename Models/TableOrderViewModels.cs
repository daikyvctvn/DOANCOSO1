using System.ComponentModel.DataAnnotations;

namespace TableOrderWeb.Models;

public sealed class TableQrViewModel
{
    public string TableCode { get; set; } = string.Empty;
    public string MenuUrl { get; set; } = string.Empty;
    public string ShortUrl { get; set; } = string.Empty;
    public string QrImageUrl { get; set; } = string.Empty;
}

public sealed class CustomerCartItemViewModel
{
    public int ItemId { get; set; }
    public string ItemCode { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public decimal UnitPrice { get; set; }
    public int Quantity { get; set; }
    public string Note { get; set; } = string.Empty;
    public decimal LineTotal => UnitPrice * Quantity;
}

public sealed class TableOrderLineViewModel
{
    public string Name { get; set; } = string.Empty;
    public int Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal LineTotal { get; set; }
    public string Note { get; set; } = string.Empty;
}

public sealed class SubmittedOrderSummaryViewModel
{
    public string OrderId { get; set; } = string.Empty;
    public string OrderCode { get; set; } = string.Empty;
    public string StatusKey { get; set; } = string.Empty;
    public string StatusLabel { get; set; } = string.Empty;
    public string SubmittedTimeLabel { get; set; } = string.Empty;
    public int ItemCount { get; set; }
    public decimal TotalAmount { get; set; }
    public string PaymentLabel { get; set; } = "Chưa yêu cầu thanh toán";
    public int ProgressStep { get; set; }
    public List<TableOrderLineViewModel> Items { get; set; } = new();
}

public sealed class ChatMessageViewModel
{
    public string MessageId { get; set; } = string.Empty;
    public string TableCode { get; set; } = string.Empty;
    public string SenderName { get; set; } = string.Empty;
    public string SenderRole { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string MessageType { get; set; } = "chat";
    public string TimeLabel { get; set; } = string.Empty;
    public string Direction { get; set; } = "neutral";
}

public sealed class PaymentRequestViewModel
{
    public string RequestId { get; set; } = string.Empty;
    public string TableCode { get; set; } = string.Empty;
    public string Method { get; set; } = string.Empty;
    public string MethodLabel { get; set; } = string.Empty;
    public string Note { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string StatusLabel { get; set; } = string.Empty;
    public string RequestedAtLabel { get; set; } = string.Empty;
    public decimal Amount { get; set; }
}

public sealed class ReviewEntryViewModel
{
    public string TableCode { get; set; } = string.Empty;
    public int FoodRating { get; set; }
    public int ServiceRating { get; set; }
    public string Comment { get; set; } = string.Empty;
    public string CreatedAtLabel { get; set; } = string.Empty;
}

public sealed class OrderTimelineEntryViewModel
{
    public string TimeLabel { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Detail { get; set; } = string.Empty;
    public string State { get; set; } = "neutral";
}

public sealed class CustomerAddToCartInputModel
{
    [Required]
    public string TableCode { get; set; } = string.Empty;

    [Range(1, int.MaxValue)]
    public int ItemId { get; set; }

    [Range(1, 20)]
    public int Quantity { get; set; } = 1;

    [StringLength(40)]
    public string SugarLevel { get; set; } = string.Empty;

    [StringLength(40)]
    public string IceLevel { get; set; } = string.Empty;

    [StringLength(80)]
    public string ToppingChoice { get; set; } = string.Empty;

    [StringLength(200)]
    public string Note { get; set; } = string.Empty;
}

public sealed class CustomerSubmitOrderInputModel
{
    [Required]
    public string TableCode { get; set; } = string.Empty;
}

public sealed class CustomerRemoveCartItemInputModel
{
    [Required]
    public string TableCode { get; set; } = string.Empty;

    [Range(1, int.MaxValue)]
    public int ItemId { get; set; }

    public string Note { get; set; } = string.Empty;
}

public sealed class CustomerChatInputModel
{
    [Required]
    public string TableCode { get; set; } = string.Empty;

    [Required]
    [StringLength(300, ErrorMessage = "Tin nhắn tối đa 300 ký tự.")]
    public string Message { get; set; } = string.Empty;
}

public sealed class CustomerPaymentRequestInputModel
{
    [Required]
    public string TableCode { get; set; } = string.Empty;

    [Required]
    [StringLength(20)]
    public string PaymentMethod { get; set; } = string.Empty;

    [StringLength(200)]
    public string Note { get; set; } = string.Empty;
}

public sealed class RestaurantCheckoutInputModel
{
    [Required]
    public string TableCode { get; set; } = string.Empty;

    public List<string> PaymentMethods { get; set; } = new();

    public List<decimal> PaymentAmounts { get; set; } = new();

    [StringLength(200)]
    public string Note { get; set; } = string.Empty;
}

public sealed class CustomerReviewInputModel
{
    [Required]
    public string TableCode { get; set; } = string.Empty;

    [Range(1, 5)]
    public int FoodRating { get; set; }

    [Range(1, 5)]
    public int ServiceRating { get; set; }

    [StringLength(300)]
    public string Comment { get; set; } = string.Empty;
}

public sealed class StaffPageViewModel
{
    public int NewOrderCount { get; set; }
    public int DraftCartCount { get; set; }
    public int ActiveTableCount { get; set; }
    public int PendingChatCount { get; set; }
    public int PendingPaymentCount { get; set; }
    public int KitchenItemCount { get; set; }
    public List<StaffOrderTicketViewModel> Orders { get; set; } = new();
    public List<StaffKitchenItemViewModel> KitchenItems { get; set; } = new();
    public List<TableStatusViewModel> TableStatuses { get; set; } = new();
    public List<StaffChatThreadViewModel> ChatThreads { get; set; } = new();
    public List<PaymentRequestViewModel> PaymentRequests { get; set; } = new();
    public string? StatusMessage { get; set; }
    public string? ErrorMessage { get; set; }
}

public sealed class StaffOrderTicketViewModel
{
    public string OrderId { get; set; } = string.Empty;
    public string TableCode { get; set; } = string.Empty;
    public string OrderCode { get; set; } = string.Empty;
    public string ItemsSummary { get; set; } = string.Empty;
    public string NotesSummary { get; set; } = string.Empty;
    public string StatusKey { get; set; } = string.Empty;
    public string StatusLabel { get; set; } = string.Empty;
    public string SubmittedTimeLabel { get; set; } = string.Empty;
    public decimal TotalAmount { get; set; }
    public string PaymentLabel { get; set; } = string.Empty;
}

public sealed class StaffKitchenItemViewModel
{
    public string OrderId { get; set; } = string.Empty;
    public string OrderCode { get; set; } = string.Empty;
    public string TableCode { get; set; } = string.Empty;
    public string ItemName { get; set; } = string.Empty;
    public int Quantity { get; set; }
    public string Note { get; set; } = string.Empty;
    public string StatusKey { get; set; } = string.Empty;
    public string StatusLabel { get; set; } = string.Empty;
    public string SubmittedTimeLabel { get; set; } = string.Empty;
}

public sealed class StaffChatThreadViewModel
{
    public string TableCode { get; set; } = string.Empty;
    public string LastMessageLabel { get; set; } = string.Empty;
    public int PendingCount { get; set; }
    public List<ChatMessageViewModel> Messages { get; set; } = new();
}

public sealed class TableStatusViewModel
{
    public string TableCode { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public decimal OutstandingTotal { get; set; }
    public string LastActivityLabel { get; set; } = string.Empty;
    public bool HasActiveOrder { get; set; }
    public int ActiveOrderCount { get; set; }
    public List<string> OrderedItems { get; set; } = new();
    public string OrderedItemsSummary { get; set; } = string.Empty;
    public List<TableOrderLineViewModel> DetailedItems { get; set; } = new();
}

public sealed class RestaurantPageViewModel
{
    public int TotalTableCount { get; set; }
    public int ActiveTableCount { get; set; }
    public int AvailableTableCount { get; set; }
    public List<TableStatusViewModel> Tables { get; set; } = new();
    public string? StatusMessage { get; set; }
    public string? ErrorMessage { get; set; }
}

public sealed class StaffOrderStatusInputModel
{
    [Required]
    public string OrderId { get; set; } = string.Empty;

    [Required]
    [StringLength(30)]
    public string Status { get; set; } = string.Empty;
}

public sealed class StaffChatInputModel
{
    [Required]
    public string TableCode { get; set; } = string.Empty;

    [Required]
    [StringLength(300)]
    public string Message { get; set; } = string.Empty;
}

public sealed class StaffTableStateInputModel
{
    [Required]
    public string TableCode { get; set; } = string.Empty;

    [Required]
    [StringLength(40)]
    public string State { get; set; } = string.Empty;
}

public sealed class AdminOperationsDashboardViewModel
{
    public decimal ShiftRevenue { get; set; }
    public decimal TodayRevenue { get; set; }
    public decimal MonthRevenue { get; set; }
    public int ShiftOrderCount { get; set; }
    public int TodayOrderCount { get; set; }
    public int CancelledOrderCount { get; set; }
    public int PendingPaymentCount { get; set; }
    public List<AdminOrderHistoryItemViewModel> Orders { get; set; } = new();
    public List<AdminPaymentHistoryItemViewModel> PaymentHistory { get; set; } = new();
    public List<ChatMessageViewModel> RecentChats { get; set; } = new();
    public List<StaffChatThreadViewModel> ChatThreads { get; set; } = new();
    public List<AdminBestSellerEntryViewModel> BestSellers { get; set; } = new();
    public List<AdminTableUsageEntryViewModel> TableServiceUsage { get; set; } = new();
    public List<ReviewEntryViewModel> RecentReviews { get; set; } = new();
    public List<AdminShiftCloseViewModel> RecentShiftClosures { get; set; } = new();
    public List<AdminDayCloseViewModel> RecentDayClosures { get; set; } = new();
    public string ShiftLabel { get; set; } = string.Empty;
    public string ShiftTimeRangeLabel { get; set; } = string.Empty;
}

public sealed class AdminShiftCloseViewModel
{
    public string ShiftLabel { get; set; } = string.Empty;
    public string TimeRangeLabel { get; set; } = string.Empty;
    public string ClosedAtLabel { get; set; } = string.Empty;
    public string ClosedBy { get; set; } = string.Empty;
    public decimal Revenue { get; set; }
    public int OrderCount { get; set; }
}

public sealed class AdminDayCloseViewModel
{
    public string DayLabel { get; set; } = string.Empty;
    public string ClosedAtLabel { get; set; } = string.Empty;
    public string ClosedBy { get; set; } = string.Empty;
    public decimal Revenue { get; set; }
    public int OrderCount { get; set; }
}

public sealed class AdminOrderHistoryItemViewModel
{
    public string OrderCode { get; set; } = string.Empty;
    public string TableCode { get; set; } = string.Empty;
    public string SubmittedTimeLabel { get; set; } = string.Empty;
    public string StatusLabel { get; set; } = string.Empty;
    public string PaymentLabel { get; set; } = string.Empty;
    public decimal TotalAmount { get; set; }
    public string ItemsSummary { get; set; } = string.Empty;
}

public sealed class AdminPaymentHistoryItemViewModel
{
    public string RequestId { get; set; } = string.Empty;
    public string TableCode { get; set; } = string.Empty;
    public string OrderCode { get; set; } = string.Empty;
    public string MethodLabel { get; set; } = string.Empty;
    public string StatusLabel { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public string RequestedAtLabel { get; set; } = string.Empty;
    public string UpdatedAtLabel { get; set; } = string.Empty;
    public string Note { get; set; } = string.Empty;
}

public sealed class AdminBestSellerEntryViewModel
{
    public string Name { get; set; } = string.Empty;
    public int Quantity { get; set; }
}

public sealed class AdminTableUsageEntryViewModel
{
    public string TableCode { get; set; } = string.Empty;
    public int ServiceRequestCount { get; set; }
}
