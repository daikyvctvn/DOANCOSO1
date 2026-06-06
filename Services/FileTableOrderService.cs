using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using TableOrderWeb.Models;

namespace TableOrderWeb.Services;

public sealed class FileTableOrderService : ITableOrderService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly string[] TableCodes =
    [
        "A01", "A02", "A03", "A04", "A05", "A06", "A07", "A08",
        "B01", "B02", "B03", "B04", "B05", "B06", "C01", "C02", "C03", "C04"
    ];

    private readonly string _filePath;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly SemaphoreSlim _sqlSyncLock = new(1, 1);
    private readonly IConfiguration _configuration;
    private readonly ILogger<FileTableOrderService> _logger;
    private bool _sqlSchemaReady;

    public FileTableOrderService(IWebHostEnvironment environment, IConfiguration configuration, ILogger<FileTableOrderService> logger)
    {
        _configuration = configuration;
        _logger = logger;
        var directory = Path.Combine(environment.ContentRootPath, "data");
        Directory.CreateDirectory(directory);
        _filePath = Path.Combine(directory, "orders.json");
    }

    public string NormalizeTableCode(string? tableCode)
    {
        var normalized = (tableCode ?? string.Empty).Trim().ToUpperInvariant();
        return TableCodes.Contains(normalized, StringComparer.OrdinalIgnoreCase) ? normalized : TableCodes[0];
    }

    public IReadOnlyList<string> GetTableCodes() => TableCodes;

    public async Task<(bool IsAvailable, string StateLabel)> GetCustomerTableAvailabilityAsync(string tableCode, CancellationToken cancellationToken = default)
    {
        var normalizedTableCode = NormalizeTableCode(tableCode);
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var store = await ReadStoreAsync(cancellationToken);
            var stateLabel = ResolveTableStateLabel(store, normalizedTableCode);
            return (!BlocksCustomerAccess(stateLabel), stateLabel);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SyncSqlHistoryAsync(CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var store = await ReadStoreAsync(cancellationToken);
            await SyncSqlHistoryBestEffortAsync(store, cancellationToken);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task ApplyCustomerSessionAsync(CustomerPageViewModel model, CancellationToken cancellationToken = default)
    {
        var normalizedTableCode = NormalizeTableCode(model.TableCode);

        await _lock.WaitAsync(cancellationToken);
        try
        {
            var store = await ReadStoreAsync(cancellationToken);
            var tableOrders = store.Orders
                .Where(x => string.Equals(x.TableCode, normalizedTableCode, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(x => x.UpdatedAtUtc)
                .ToList();

            var draft = tableOrders.FirstOrDefault(x => x.Status == OrderStatus.Draft);
            var submittedOrders = tableOrders
                .Where(x => x.Status is not OrderStatus.Draft and not OrderStatus.Cancelled and not OrderStatus.Refunded and not OrderStatus.Paid)
                .OrderByDescending(x => x.SubmittedAtUtc ?? x.UpdatedAtUtc)
                .ToList();

            var paymentRequests = store.PaymentRequests
                .Where(x => string.Equals(x.TableCode, normalizedTableCode, StringComparison.OrdinalIgnoreCase))
                .Where(x => x.Status == PaymentStatuses.Pending ||
                    submittedOrders.Any(order => string.Equals(order.Id, x.OrderId, StringComparison.OrdinalIgnoreCase)))
                .OrderByDescending(x => x.UpdatedAtUtc)
                .ToList();

            var latestDayClosureUtc = FindLatestDayClosureUtc(store);
            var hasCurrentSession = draft is not null || submittedOrders.Count > 0 || paymentRequests.Any(x => x.Status == PaymentStatuses.Pending);
            var chats = store.ChatMessages
                .Where(x => string.Equals(x.TableCode, normalizedTableCode, StringComparison.OrdinalIgnoreCase))
                .Where(x => !latestDayClosureUtc.HasValue || x.CreatedAtUtc > latestDayClosureUtc.Value)
                .Where(x => x.SenderRole != ChatRoles.System)
                .OrderByDescending(x => x.CreatedAtUtc)
                .Take(80)
                .OrderBy(x => x.CreatedAtUtc)
                .ToList();

            List<TableReviewRecord> reviews = hasCurrentSession
                ? store.Reviews
                    .Where(x => string.Equals(x.TableCode, normalizedTableCode, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(x => x.CreatedAtUtc)
                    .Take(4)
                    .ToList()
                : [];

            model.TableCode = normalizedTableCode;
            model.CartItems = draft?.Items.Select(MapCartItem).ToList() ?? [];
            model.CartSubtotal = model.CartItems.Sum(x => x.LineTotal);
            model.ServiceFee = CalculateServiceFee(model.CartSubtotal);
            model.CartTotal = model.CartSubtotal + model.ServiceFee;
            model.SubmittedOrders = submittedOrders.Take(8).Select(order =>
            {
                var latestPayment = paymentRequests.FirstOrDefault(x => string.Equals(x.OrderId, order.Id, StringComparison.OrdinalIgnoreCase))
                    ?? paymentRequests.FirstOrDefault();
                return MapSubmittedOrder(order, latestPayment);
            }).ToList();
            model.SubmittedOrderCount = submittedOrders.Count;
            model.SubmittedSubtotal = submittedOrders.Sum(CalculateOrderSubtotal);
            model.SubmittedServiceFee = CalculateServiceFee(model.SubmittedSubtotal);
            model.SubmittedTotal = model.SubmittedSubtotal + model.SubmittedServiceFee;
            model.ChatMessages = chats.Select(MapChatMessage).ToList();
            model.LatestPaymentRequest = paymentRequests.FirstOrDefault() is { } latestRequest
                ? MapPaymentRequest(latestRequest, model.SubmittedTotal)
                : null;
            model.RecentReviews = reviews.Select(MapReview).ToList();
            model.CanSubmitReview = submittedOrders.Any(x => x.Status is OrderStatus.Served or OrderStatus.Paid);
            model.TableStateLabel = ResolveTableStateLabel(store, normalizedTableCode);
            model.Timeline = BuildTimeline(draft, submittedOrders, paymentRequests, chats, reviews);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<(bool Succeeded, string? ErrorMessage)> AddItemAsync(CustomerAddToCartInputModel request, MenuDishViewModel dish, CancellationToken cancellationToken = default)
    {
        var normalizedTableCode = NormalizeTableCode(request.TableCode);
        if (request.Quantity < 1 || request.Quantity > 20)
        {
            return (false, "Số lượng món phải từ 1 đến 20.");
        }

        if (!dish.IsAvailable)
        {
            return (false, $"{dish.Name} hiện đã hết hàng.");
        }

        await _lock.WaitAsync(cancellationToken);
        try
        {
            var store = await ReadStoreAsync(cancellationToken);
            var draft = store.Orders.FirstOrDefault(x =>
                string.Equals(x.TableCode, normalizedTableCode, StringComparison.OrdinalIgnoreCase) &&
                x.Status == OrderStatus.Draft);

            if (draft is null)
            {
                draft = new TableOrderRecord
                {
                    Id = Guid.NewGuid().ToString("N"),
                    TableCode = normalizedTableCode,
                    Status = OrderStatus.Draft,
                    CreatedAtUtc = DateTime.UtcNow,
                    UpdatedAtUtc = DateTime.UtcNow
                };
                store.Orders.Add(draft);
            }

            var normalizedNote = BuildCustomizationNote(request);
            var existingItem = draft.Items.FirstOrDefault(x =>
                x.ItemId == dish.ItemId &&
                string.Equals(x.Note, normalizedNote, StringComparison.OrdinalIgnoreCase));

            if (existingItem is null)
            {
                draft.Items.Add(new TableOrderItemRecord
                {
                    ItemId = dish.ItemId,
                    ItemCode = dish.ItemCode,
                    Name = dish.Name,
                    UnitPrice = dish.Price,
                    Quantity = request.Quantity,
                    Note = normalizedNote
                });
            }
            else
            {
                existingItem.Quantity += request.Quantity;
            }

            draft.UpdatedAtUtc = DateTime.UtcNow;
            await WriteStoreAsync(store, cancellationToken);
            return (true, null);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<(bool Succeeded, string? ErrorMessage, string? RemovedItemName)> RemoveItemAsync(CustomerRemoveCartItemInputModel request, CancellationToken cancellationToken = default)
    {
        var normalizedTableCode = NormalizeTableCode(request.TableCode);

        await _lock.WaitAsync(cancellationToken);
        try
        {
            var store = await ReadStoreAsync(cancellationToken);
            var draft = store.Orders.FirstOrDefault(x =>
                string.Equals(x.TableCode, normalizedTableCode, StringComparison.OrdinalIgnoreCase) &&
                x.Status == OrderStatus.Draft);

            if (draft is null || draft.Items.Count == 0)
            {
                return (false, "Giỏ hàng của bạn đang trống.", null);
            }

            var normalizedNote = (request.Note ?? string.Empty).Trim();
            var item = draft.Items.FirstOrDefault(x =>
                x.ItemId == request.ItemId &&
                string.Equals(x.Note, normalizedNote, StringComparison.OrdinalIgnoreCase));

            if (item is null)
            {
                return (false, "Không tìm thấy món cần hủy trong giỏ.", null);
            }

            var removedItemName = item.Name;
            draft.Items.Remove(item);
            draft.UpdatedAtUtc = DateTime.UtcNow;

            await WriteStoreAsync(store, cancellationToken);
            return (true, null, removedItemName);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<(bool Succeeded, string? ErrorMessage, string? OrderCode)> SubmitOrderAsync(string tableCode, CancellationToken cancellationToken = default)
    {
        var normalizedTableCode = NormalizeTableCode(tableCode);

        await _lock.WaitAsync(cancellationToken);
        try
        {
            var store = await ReadStoreAsync(cancellationToken);
            var draft = store.Orders.FirstOrDefault(x =>
                string.Equals(x.TableCode, normalizedTableCode, StringComparison.OrdinalIgnoreCase) &&
                x.Status == OrderStatus.Draft);

            if (draft is null || draft.Items.Count == 0)
            {
                return (false, "Giỏ hàng của bạn đang trống.", null);
            }

            var nextNumber = store.Orders.Count(x =>
                string.Equals(x.TableCode, normalizedTableCode, StringComparison.OrdinalIgnoreCase) &&
                x.Status is not OrderStatus.Draft and not OrderStatus.Cancelled and not OrderStatus.Refunded and not OrderStatus.Paid) + 1;

            draft.Status = OrderStatus.Submitted;
            draft.OrderCode = $"{normalizedTableCode}-{nextNumber:000}";
            draft.SubmittedAtUtc = DateTime.UtcNow;
            draft.UpdatedAtUtc = draft.SubmittedAtUtc.Value;

            store.ChatMessages.Add(CreateSystemMessage(
                normalizedTableCode,
                $"Đơn #{draft.OrderCode} đã được gửi xuống bộ phận phục vụ và bếp/bar.",
                "order"));

            await WriteStoreAsync(store, cancellationToken);
            return (true, null, draft.OrderCode);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<(bool Succeeded, string? ErrorMessage)> SendCustomerMessageAsync(CustomerChatInputModel request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Message))
        {
            return (false, "Nội dung tin nhắn đang trống.");
        }

        var normalizedTableCode = NormalizeTableCode(request.TableCode);
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var store = await ReadStoreAsync(cancellationToken);
            store.ChatMessages.Add(new TableChatMessageRecord
            {
                Id = Guid.NewGuid().ToString("N"),
                TableCode = normalizedTableCode,
                SenderName = $"Bàn {normalizedTableCode}",
                SenderRole = ChatRoles.Customer,
                Message = request.Message.Trim(),
                MessageType = InferCustomerMessageType(request.Message),
                CreatedAtUtc = DateTime.UtcNow,
                IsReadByCustomer = true,
                IsReadByStaff = false
            });

            await WriteStoreAsync(store, cancellationToken);
            return (true, null);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<List<ChatMessageViewModel>> GetCustomerChatMessagesAsync(string tableCode, CancellationToken cancellationToken = default)
    {
        var normalizedTableCode = NormalizeTableCode(tableCode);
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var store = await ReadStoreAsync(cancellationToken);
            var latestDayClosureUtc = FindLatestDayClosureUtc(store);

            return store.ChatMessages
                .Where(x => string.Equals(x.TableCode, normalizedTableCode, StringComparison.OrdinalIgnoreCase))
                .Where(x => !latestDayClosureUtc.HasValue || x.CreatedAtUtc > latestDayClosureUtc.Value)
                .Where(x => x.SenderRole != ChatRoles.System)
                .OrderByDescending(x => x.CreatedAtUtc)
                .Take(80)
                .OrderBy(x => x.CreatedAtUtc)
                .Select(MapChatMessage)
                .ToList();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<(bool Succeeded, string? ErrorMessage)> RequestPaymentAsync(CustomerPaymentRequestInputModel request, CancellationToken cancellationToken = default)
    {
        var normalizedTableCode = NormalizeTableCode(request.TableCode);
        var normalizedMethod = NormalizePaymentMethod(request.PaymentMethod);
        if (normalizedMethod is null)
        {
            return (false, "Phương thức thanh toán không hợp lệ.");
        }

        await _lock.WaitAsync(cancellationToken);
        try
        {
            var store = await ReadStoreAsync(cancellationToken);
            var orders = store.Orders
                .Where(x => string.Equals(x.TableCode, normalizedTableCode, StringComparison.OrdinalIgnoreCase))
                .Where(x => x.Status is not OrderStatus.Draft and not OrderStatus.Cancelled and not OrderStatus.Refunded and not OrderStatus.Paid)
                .ToList();

            if (orders.Count == 0)
            {
                return (false, "Bàn chưa có đơn nào để thanh toán.");
            }

            var amount = orders.Sum(CalculateOrderSubtotal);
            var requestRecord = new TablePaymentRequestRecord
            {
                Id = Guid.NewGuid().ToString("N"),
                TableCode = normalizedTableCode,
                OrderId = orders.OrderByDescending(x => x.UpdatedAtUtc).First().Id,
                Method = normalizedMethod,
                Note = (request.Note ?? string.Empty).Trim(),
                Status = PaymentStatuses.Pending,
                RequestedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow,
                Amount = amount + CalculateServiceFee(amount)
            };

            store.PaymentRequests.Add(requestRecord);
            store.ChatMessages.Add(CreateSystemMessage(
                normalizedTableCode,
                $"Khách đã yêu cầu thanh toán bằng {MapPaymentMethodLabel(normalizedMethod)}.",
                "payment"));

            await WriteStoreAsync(store, cancellationToken);
            return (true, null);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<(bool Succeeded, string? ErrorMessage)> DirectCheckoutAsync(RestaurantCheckoutInputModel request, CancellationToken cancellationToken = default)
    {
        var normalizedTableCode = NormalizeTableCode(request.TableCode);

        await _lock.WaitAsync(cancellationToken);
        try
        {
            var store = await ReadStoreAsync(cancellationToken);
            var orders = store.Orders
                .Where(x => string.Equals(x.TableCode, normalizedTableCode, StringComparison.OrdinalIgnoreCase))
                .Where(x => x.Status is not OrderStatus.Draft and not OrderStatus.Cancelled and not OrderStatus.Refunded and not OrderStatus.Paid)
                .ToList();

            if (orders.Count == 0)
            {
                return (false, "Bàn này không có đơn nào cần thanh toán.");
            }

            var paidAtUtc = DateTime.UtcNow;
            var totalAmount = orders.Sum(CalculateOrderSubtotal);
            var finalAmount = totalAmount + CalculateServiceFee(totalAmount);
            var paymentEntries = new List<(string Method, decimal Amount)>();
            var maxCount = Math.Max(request.PaymentMethods.Count, request.PaymentAmounts.Count);
            for (var index = 0; index < maxCount; index++)
            {
                var method = index < request.PaymentMethods.Count ? NormalizePaymentMethod(request.PaymentMethods[index]) : null;
                var amount = index < request.PaymentAmounts.Count ? request.PaymentAmounts[index] : 0;

                if (method is null && amount <= 0)
                {
                    continue;
                }

                if (method is null)
                {
                    return (false, $"Dòng thanh toán thứ {index + 1} chưa chọn phương thức hợp lệ.");
                }

                if (amount <= 0)
                {
                    return (false, $"Dòng thanh toán thứ {index + 1} phải có số tiền lớn hơn 0.");
                }

                paymentEntries.Add((method, amount));
            }

            if (paymentEntries.Count == 0)
            {
                return (false, "Hãy nhập ít nhất một hình thức thanh toán.");
            }

            var totalPaid = paymentEntries.Sum(x => x.Amount);
            if (totalPaid < finalAmount)
            {
                return (false, $"Khách mới thanh toán {totalPaid:N0}đ, còn thiếu {(finalAmount - totalPaid):N0}đ.");
            }

            foreach (var order in orders)
            {
                order.Status = OrderStatus.Paid;
                order.PaidAtUtc = paidAtUtc;
                order.UpdatedAtUtc = paidAtUtc;
            }
            var completedPayments = store.PaymentRequests
                .Where(x => string.Equals(x.TableCode, normalizedTableCode, StringComparison.OrdinalIgnoreCase) && x.Status == PaymentStatuses.Pending)
                .ToList();
            var noteParts = new List<string>();
            if (!string.IsNullOrWhiteSpace(request.Note))
            {
                noteParts.Add(request.Note.Trim());
            }

            var paymentSummary = string.Join(", ", paymentEntries.Select(x => $"{MapPaymentMethodLabel(x.Method)} {x.Amount:N0}đ"));
            noteParts.Add($"Thanh toán: {paymentSummary}");

            var changeAmount = Math.Max(0, totalPaid - finalAmount);
            if (changeAmount > 0)
            {
                noteParts.Add($"Tiền thừa {changeAmount:N0}đ");
            }

            var paymentNote = string.Join(" | ", noteParts);
            if (completedPayments.Count == 0)
            {
                foreach (var entry in paymentEntries)
                {
                    store.PaymentRequests.Add(new TablePaymentRequestRecord
                    {
                        Id = Guid.NewGuid().ToString("N"),
                        TableCode = normalizedTableCode,
                        OrderId = orders.OrderByDescending(x => x.UpdatedAtUtc).First().Id,
                        Method = entry.Method,
                        Note = paymentNote,
                        Status = PaymentStatuses.Completed,
                        RequestedAtUtc = paidAtUtc,
                        UpdatedAtUtc = paidAtUtc,
                        Amount = entry.Amount
                    });
                }
            }
            else
            {
                for (var index = 0; index < paymentEntries.Count; index++)
                {
                    var entry = paymentEntries[index];
                    var payment = index < completedPayments.Count ? completedPayments[index] : null;
                    if (payment is null)
                    {
                        store.PaymentRequests.Add(new TablePaymentRequestRecord
                        {
                            Id = Guid.NewGuid().ToString("N"),
                            TableCode = normalizedTableCode,
                            OrderId = orders.OrderByDescending(x => x.UpdatedAtUtc).First().Id,
                            Method = entry.Method,
                            Note = paymentNote,
                            Status = PaymentStatuses.Completed,
                            RequestedAtUtc = paidAtUtc,
                            UpdatedAtUtc = paidAtUtc,
                            Amount = entry.Amount
                        });
                        continue;
                    }

                    payment.Method = entry.Method;
                    payment.Note = paymentNote;
                    payment.Status = PaymentStatuses.Completed;
                    payment.UpdatedAtUtc = paidAtUtc;
                    payment.Amount = entry.Amount;
                }

                foreach (var extraPayment in completedPayments.Skip(paymentEntries.Count))
                {
                    extraPayment.Status = PaymentStatuses.Completed;
                    extraPayment.Note = paymentNote;
                    extraPayment.UpdatedAtUtc = paidAtUtc;
                    extraPayment.Amount = 0;
                }
            }

            ResetTableSession(store, normalizedTableCode, paidAtUtc, "Restaurant");
            ClearTableChatSession(store, normalizedTableCode);

            store.ChatMessages.Add(CreateSystemMessage(
                normalizedTableCode,
                $"Bàn {normalizedTableCode} đã thanh toán {finalAmount:N0}đ qua {paymentSummary}.",
                "payment"));

            await WriteStoreAsync(store, cancellationToken);
            return (true, null);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<(bool Succeeded, string? ErrorMessage)> TransferTableAsync(RestaurantTransferTableInputModel request, CancellationToken cancellationToken = default)
    {
        var fromTableCode = NormalizeTableCode(request.FromTableCode);
        var toTableCode = NormalizeTableCode(request.ToTableCode);
        if (string.Equals(fromTableCode, toTableCode, StringComparison.OrdinalIgnoreCase))
        {
            return (false, "Bàn chuyển đến phải khác bàn hiện tại.");
        }

        await _lock.WaitAsync(cancellationToken);
        try
        {
            var store = await ReadStoreAsync(cancellationToken);
            var movingOrders = store.Orders
                .Where(x => string.Equals(x.TableCode, fromTableCode, StringComparison.OrdinalIgnoreCase))
                .Where(x => x.Status is not OrderStatus.Cancelled and not OrderStatus.Refunded and not OrderStatus.Paid)
                .Where(x => x.Items.Count > 0)
                .ToList();

            if (movingOrders.Count == 0)
            {
                return (false, $"Bàn {fromTableCode} không có món hoặc bill đang mở để chuyển.");
            }

            var updatedAtUtc = DateTime.UtcNow;
            foreach (var order in movingOrders)
            {
                order.TableCode = toTableCode;
                order.OrderCode = order.Status == OrderStatus.Draft ? null : GenerateNextOrderCode(store, toTableCode, order.Id);
                order.UpdatedAtUtc = updatedAtUtc;
            }

            foreach (var payment in store.PaymentRequests.Where(x =>
                         string.Equals(x.TableCode, fromTableCode, StringComparison.OrdinalIgnoreCase) &&
                         x.Status == PaymentStatuses.Pending))
            {
                payment.TableCode = toTableCode;
                payment.UpdatedAtUtc = updatedAtUtc;
            }

            MoveTableChatMessages(store, fromTableCode, toTableCode);
            SetTableState(store, fromTableCode, "Bàn trống", updatedAtUtc, "Restaurant");
            SetTableState(store, toTableCode, "Đang phục vụ", updatedAtUtc, "Restaurant");
            UpdatePendingPaymentAmountsForTable(store, fromTableCode);
            UpdatePendingPaymentAmountsForTable(store, toTableCode);
            store.ChatMessages.Add(CreateSystemMessage(toTableCode, $"Đã chuyển toàn bộ bill từ bàn {fromTableCode} sang bàn {toTableCode}.", "table-transfer"));

            await WriteStoreAsync(store, cancellationToken);
            return (true, null);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<(bool Succeeded, string? ErrorMessage)> SplitItemToTableAsync(RestaurantSplitItemInputModel request, CancellationToken cancellationToken = default)
    {
        var fromTableCode = NormalizeTableCode(request.FromTableCode);
        var toTableCode = NormalizeTableCode(request.ToTableCode);
        if (string.Equals(fromTableCode, toTableCode, StringComparison.OrdinalIgnoreCase))
        {
            return (false, "Bàn nhận món phải khác bàn hiện tại.");
        }

        if (string.IsNullOrWhiteSpace(request.LineKey))
        {
            return (false, "Hãy chọn món cần tách.");
        }

        await _lock.WaitAsync(cancellationToken);
        try
        {
            var store = await ReadStoreAsync(cancellationToken);
            var line = FindOrderLineByKey(store, request.LineKey);
            if (line is null || !string.Equals(line.Order.TableCode, fromTableCode, StringComparison.OrdinalIgnoreCase))
            {
                return (false, "Không tìm thấy món cần tách ở bàn hiện tại.");
            }

            if (line.Order.Status is OrderStatus.Cancelled or OrderStatus.Refunded or OrderStatus.Paid)
            {
                return (false, "Món này thuộc bill đã đóng nên không thể tách.");
            }

            var quantity = Math.Clamp(request.Quantity, 1, line.Item.Quantity);
            var movedItem = CloneOrderItem(line.Item, quantity);
            line.Item.Quantity -= quantity;
            if (line.Item.Quantity <= 0)
            {
                line.Order.Items.RemoveAt(line.ItemIndex);
            }

            var updatedAtUtc = DateTime.UtcNow;
            line.Order.UpdatedAtUtc = updatedAtUtc;
            if (line.Order.Items.Count == 0)
            {
                store.Orders.Remove(line.Order);
            }

            var targetOrder = FindTargetOrderForSplit(store, toTableCode, line.Order.Status);
            if (targetOrder is null)
            {
                targetOrder = CreateSplitTargetOrder(store, toTableCode, line.Order, updatedAtUtc);
                store.Orders.Add(targetOrder);
            }

            AddOrMergeOrderItem(targetOrder, movedItem);
            targetOrder.UpdatedAtUtc = updatedAtUtc;

            SetTableState(store, toTableCode, "Đang phục vụ", updatedAtUtc, "Restaurant");
            if (!HasOpenItems(store, fromTableCode))
            {
                SetTableState(store, fromTableCode, "Bàn trống", updatedAtUtc, "Restaurant");
            }

            UpdatePendingPaymentAmountsForTable(store, fromTableCode);
            UpdatePendingPaymentAmountsForTable(store, toTableCode);
            store.ChatMessages.Add(CreateSystemMessage(toTableCode, $"Đã tách {quantity} x {movedItem.Name} từ bàn {fromTableCode} sang bàn {toTableCode}.", "item-split"));

            await WriteStoreAsync(store, cancellationToken);
            return (true, null);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<(bool Succeeded, string? ErrorMessage)> SubmitReviewAsync(CustomerReviewInputModel request, CancellationToken cancellationToken = default)
    {
        var normalizedTableCode = NormalizeTableCode(request.TableCode);
        if (request.ServiceRating <= 0)
        {
            request.ServiceRating = request.FoodRating;
        }

        if (request.FoodRating is < 1 or > 5 || request.ServiceRating is < 1 or > 5)
        {
            return (false, "Diem danh gia phai tu 1 den 5.");
        }

        await _lock.WaitAsync(cancellationToken);
        try
        {
            var store = await ReadStoreAsync(cancellationToken);
            var canReview = store.Orders.Any(x =>
                string.Equals(x.TableCode, normalizedTableCode, StringComparison.OrdinalIgnoreCase) &&
                x.Status is OrderStatus.Served or OrderStatus.Paid);

            if (!canReview)
            {
                return (false, "Khi mon da ra ban ban moi co the danh gia.");
            }

            store.Reviews.Add(new TableReviewRecord
            {
                Id = Guid.NewGuid().ToString("N"),
                TableCode = normalizedTableCode,
                FoodRating = request.FoodRating,
                ServiceRating = request.ServiceRating,
                Comment = (request.Comment ?? string.Empty).Trim(),
                CreatedAtUtc = DateTime.UtcNow
            });

            await WriteStoreAsync(store, cancellationToken);
            return (true, null);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<StaffPageViewModel> GetStaffDashboardAsync(CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var store = await ReadStoreAsync(cancellationToken);
            var orders = store.Orders
                .Where(x => x.Status is not OrderStatus.Draft and not OrderStatus.Cancelled and not OrderStatus.Refunded and not OrderStatus.Paid and not OrderStatus.Served)
                .OrderByDescending(x => x.SubmittedAtUtc ?? x.UpdatedAtUtc)
                .ToList();

            var drafts = store.Orders.Where(x => x.Status == OrderStatus.Draft && x.Items.Count > 0).ToList();
            var pendingChats = store.ChatMessages.Count(x => x.SenderRole == ChatRoles.Customer && !x.IsReadByStaff);
            var pendingPayments = store.PaymentRequests.Count(x => x.Status == PaymentStatuses.Pending);
            var kitchenItems = BuildKitchenItems(orders);

            return new StaffPageViewModel
            {
                NewOrderCount = orders.Count(x => x.Status == OrderStatus.Submitted),
                DraftCartCount = drafts.Count,
                ActiveTableCount = store.Orders
                    .Where(x => x.Items.Count > 0 &&
                        x.Status is not OrderStatus.Draft and not OrderStatus.Cancelled and not OrderStatus.Refunded and not OrderStatus.Paid)
                    .Select(x => x.TableCode)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count(),
                PendingChatCount = pendingChats,
                PendingPaymentCount = pendingPayments,
                KitchenItemCount = kitchenItems.Count,
                Orders = orders.Take(12).Select(order =>
                {
                    var latestPayment = store.PaymentRequests
                        .Where(x => string.Equals(x.OrderId, order.Id, StringComparison.OrdinalIgnoreCase)
                            || string.Equals(x.TableCode, order.TableCode, StringComparison.OrdinalIgnoreCase))
                        .OrderByDescending(x => x.UpdatedAtUtc)
                        .FirstOrDefault();
                    return MapStaffOrder(order, latestPayment);
                }).ToList(),
                KitchenItems = kitchenItems.Take(16).ToList(),
                TableStatuses = BuildTableStatuses(store),
                ChatThreads = BuildChatThreads(store),
                PaymentRequests = store.PaymentRequests
                    .Where(x => x.Status == PaymentStatuses.Pending)
                    .OrderByDescending(x => x.UpdatedAtUtc)
                    .Take(8)
                    .Select(x => MapPaymentRequest(x, x.Amount))
                    .ToList()
            };
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<RestaurantPageViewModel> GetRestaurantDashboardAsync(CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var store = await ReadStoreAsync(cancellationToken);
            var tables = BuildTableStatuses(store);

            return new RestaurantPageViewModel
            {
                TotalTableCount = tables.Count,
                ActiveTableCount = tables.Count(x => x.HasActiveOrder),
                AvailableTableCount = tables.Count(x => !x.HasActiveOrder),
                Tables = tables
            };
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<List<StaffChatThreadViewModel>> GetStaffChatThreadsAsync(CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var store = await ReadStoreAsync(cancellationToken);
            return BuildChatThreads(store);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<(bool Succeeded, string? ErrorMessage)> UpdateOrderStatusAsync(StaffOrderStatusInputModel request, CancellationToken cancellationToken = default)
    {
        var normalizedStatus = NormalizeOrderStatus(request.Status);
        if (string.IsNullOrWhiteSpace(request.OrderId) || normalizedStatus is null)
        {
            return (false, "Trạng thái đơn không hợp lệ.");
        }

        await _lock.WaitAsync(cancellationToken);
        try
        {
            var store = await ReadStoreAsync(cancellationToken);
            var order = store.Orders.FirstOrDefault(x => string.Equals(x.Id, request.OrderId, StringComparison.OrdinalIgnoreCase));
            if (order is null)
            {
                return (false, "Không tìm thấy đơn cần cập nhật.");
            }

            if ((order.Status == OrderStatus.Paid || order.PaidAtUtc.HasValue) && normalizedStatus != OrderStatus.Paid)
            {
                return (false, "Đơn đã thanh toán nên không thể chuyển lại trạng thái đang xử lý.");
            }

            order.Status = normalizedStatus;
            order.UpdatedAtUtc = DateTime.UtcNow;
            if (normalizedStatus == OrderStatus.Accepted) order.AcceptedAtUtc = order.UpdatedAtUtc;
            if (normalizedStatus == OrderStatus.Ready) order.ReadyAtUtc = order.UpdatedAtUtc;
            if (normalizedStatus == OrderStatus.Served) order.ServedAtUtc = order.UpdatedAtUtc;
            if (normalizedStatus == OrderStatus.Paid)
            {
                order.PaidAtUtc = order.UpdatedAtUtc;
                foreach (var payment in store.PaymentRequests.Where(x =>
                             string.Equals(x.TableCode, order.TableCode, StringComparison.OrdinalIgnoreCase) &&
                             x.Status == PaymentStatuses.Pending))
                {
                    payment.Status = PaymentStatuses.Completed;
                    payment.UpdatedAtUtc = order.UpdatedAtUtc;
                }

                var hasOtherActiveOrder = store.Orders.Any(x =>
                    !string.Equals(x.Id, order.Id, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(x.TableCode, order.TableCode, StringComparison.OrdinalIgnoreCase) &&
                    x.Status is not OrderStatus.Draft and not OrderStatus.Cancelled and not OrderStatus.Refunded and not OrderStatus.Paid);
                if (!hasOtherActiveOrder)
                {
                    ResetTableSession(store, order.TableCode, order.UpdatedAtUtc, "Staff");
                    ClearTableChatSession(store, order.TableCode);
                }
            }

            store.ChatMessages.Add(CreateSystemMessage(
                order.TableCode,
                $"Đơn #{order.OrderCode ?? order.Id[..6].ToUpperInvariant()} đã chuyển sang trạng thái {MapOrderStatusLabel(normalizedStatus)}.",
                "order"));

            await WriteStoreAsync(store, cancellationToken);
            return (true, null);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<(bool Succeeded, string? ErrorMessage)> SendStaffMessageAsync(StaffChatInputModel request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Message))
        {
            return (false, "Nội dung tin nhắn đang trống.");
        }

        var normalizedTableCode = NormalizeTableCode(request.TableCode);
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var store = await ReadStoreAsync(cancellationToken);

            foreach (var item in store.ChatMessages.Where(x =>
                         string.Equals(x.TableCode, normalizedTableCode, StringComparison.OrdinalIgnoreCase) &&
                         x.SenderRole == ChatRoles.Customer))
            {
                item.IsReadByStaff = true;
            }

            store.ChatMessages.Add(new TableChatMessageRecord
            {
                Id = Guid.NewGuid().ToString("N"),
                TableCode = normalizedTableCode,
                SenderName = "Nhân viên",
                SenderRole = ChatRoles.Staff,
                Message = request.Message.Trim(),
                MessageType = "reply",
                CreatedAtUtc = DateTime.UtcNow,
                IsReadByCustomer = false,
                IsReadByStaff = true
            });

            await WriteStoreAsync(store, cancellationToken);
            return (true, null);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<(bool Succeeded, string? ErrorMessage)> DeleteTableChatAsync(string tableCode, CancellationToken cancellationToken = default)
    {
        var normalizedTableCode = NormalizeTableCode(tableCode);
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var store = await ReadStoreAsync(cancellationToken);
            ClearTableChatSession(store, normalizedTableCode);
            await WriteStoreAsync(store, cancellationToken);
            return (true, null);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<(bool Succeeded, string? ErrorMessage)> UpdateTableStateAsync(StaffTableStateInputModel request, CancellationToken cancellationToken = default)
    {
        var normalizedTableCode = NormalizeTableCode(request.TableCode);
        var normalizedState = NormalizeTableState(request.State);
        if (normalizedState is null)
        {
            return (false, "Trạng thái bàn không hợp lệ.");
        }

        await _lock.WaitAsync(cancellationToken);
        try
        {
            var store = await ReadStoreAsync(cancellationToken);
            var tableState = store.TableStates.FirstOrDefault(x =>
                string.Equals(x.TableCode, normalizedTableCode, StringComparison.OrdinalIgnoreCase));

            if (tableState is null)
            {
                tableState = new TableStateRecord { TableCode = normalizedTableCode };
                store.TableStates.Add(tableState);
            }

            tableState.State = normalizedState;
            tableState.UpdatedAtUtc = DateTime.UtcNow;
            tableState.UpdatedBy = "Staff";
            await WriteStoreAsync(store, cancellationToken);
            return (true, null);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<AdminOperationsDashboardViewModel> GetAdminDashboardAsync(CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var store = await ReadStoreAsync(cancellationToken);
            var nowLocal = DateTime.Now;
            var today = nowLocal.Date;
            var monthStart = new DateTime(nowLocal.Year, nowLocal.Month, 1);
            var shiftRange = ResolveShiftRange(nowLocal);
            var latestDayClosureUtc = FindLatestDayClosureUtc(store);
            var activeRevenueOrders = store.Orders
                .Where(x => x.Status is not OrderStatus.Draft and not OrderStatus.Cancelled and not OrderStatus.Refunded)
                .ToList();
            var currentRevenueOrders = activeRevenueOrders
                .Where(x => !latestDayClosureUtc.HasValue || (x.SubmittedAtUtc ?? x.UpdatedAtUtc) > latestDayClosureUtc.Value)
                .ToList();
            var openDashboardOrders = store.Orders
                .Where(x => x.Status != OrderStatus.Draft)
                .Where(x => !latestDayClosureUtc.HasValue || (x.SubmittedAtUtc ?? x.UpdatedAtUtc) > latestDayClosureUtc.Value)
                .ToList();

            var todayOrders = activeRevenueOrders
                .Where(x => ToLocalDate(x.SubmittedAtUtc ?? x.UpdatedAtUtc) == today)
                .Where(x => !latestDayClosureUtc.HasValue || (x.SubmittedAtUtc ?? x.UpdatedAtUtc) > latestDayClosureUtc.Value)
                .ToList();
            var monthOrders = activeRevenueOrders.Where(x => ToLocalDate(x.SubmittedAtUtc ?? x.UpdatedAtUtc) >= monthStart).ToList();
            var latestShiftClosureUtc = FindLatestShiftClosureUtc(store, shiftRange.Start, shiftRange.End);
            var shiftOrders = activeRevenueOrders.Where(x =>
            {
                var orderTimeUtc = x.SubmittedAtUtc ?? x.UpdatedAtUtc;
                if (latestShiftClosureUtc.HasValue && orderTimeUtc <= latestShiftClosureUtc.Value)
                {
                    return false;
                }

                var local = orderTimeUtc.ToLocalTime();
                return local >= shiftRange.Start && local < shiftRange.End;
            }).ToList();

            return new AdminOperationsDashboardViewModel
            {
                ShiftLabel = shiftRange.Label,
                ShiftTimeRangeLabel = $"{shiftRange.Start:HH:mm} - {shiftRange.End:HH:mm}",
                ShiftRevenue = shiftOrders.Sum(CalculateOrderSubtotal),
                ShiftOrderCount = shiftOrders.Count,
                TodayRevenue = todayOrders.Sum(CalculateOrderSubtotal),
                MonthRevenue = monthOrders.Sum(CalculateOrderSubtotal),
                TodayOrderCount = todayOrders.Count,
                CancelledOrderCount = store.Orders.Count(x => x.Status == OrderStatus.Cancelled),
                PendingPaymentCount = store.PaymentRequests.Count(x => x.Status == PaymentStatuses.Pending),
                Orders = openDashboardOrders
                    .OrderByDescending(x => x.SubmittedAtUtc ?? x.UpdatedAtUtc)
                    .Take(18)
                    .Select(x =>
                    {
                        return new AdminOrderHistoryItemViewModel
                        {
                            OrderCode = x.OrderCode ?? x.Id[..6].ToUpperInvariant(),
                            TableCode = x.TableCode,
                            SubmittedTimeLabel = (x.SubmittedAtUtc ?? x.UpdatedAtUtc).ToLocalTime().ToString("dd/MM HH:mm"),
                            StatusLabel = MapOrderStatusLabel(x.Status),
                            PaymentLabel = ResolveAdminPaymentLabel(x, store.PaymentRequests),
                            TotalAmount = CalculateOrderSubtotal(x),
                            ItemsSummary = string.Join(", ", x.Items.Select(i => $"{i.Name} x{i.Quantity}"))
                        };
                    })
                    .ToList(),
                PaymentHistory = store.PaymentRequests
                    .Where(x => !latestDayClosureUtc.HasValue || x.UpdatedAtUtc > latestDayClosureUtc.Value)
                    .OrderByDescending(x => x.UpdatedAtUtc)
                    .ThenByDescending(x => x.RequestedAtUtc)
                    .Select(x => MapAdminPaymentHistory(x, store.Orders))
                    .ToList(),
                RecentChats = store.ChatMessages
                    .Where(x => !latestDayClosureUtc.HasValue || x.CreatedAtUtc > latestDayClosureUtc.Value)
                    .OrderByDescending(x => x.CreatedAtUtc)
                    .Take(18)
                    .OrderBy(x => x.CreatedAtUtc)
                    .Select(MapChatMessage)
                    .ToList(),
                ChatThreads = BuildAdminChatThreads(store, latestDayClosureUtc),
                BestSellers = currentRevenueOrders
                    .SelectMany(x => x.Items)
                    .GroupBy(x => x.Name)
                    .Select(x => new AdminBestSellerEntryViewModel
                    {
                        Name = x.Key,
                        Quantity = x.Sum(i => i.Quantity)
                    })
                    .OrderByDescending(x => x.Quantity)
                    .Take(8)
                    .ToList(),
                TableServiceUsage = TableCodes
                    .Select(code => new AdminTableUsageEntryViewModel
                    {
                        TableCode = code,
                        ServiceRequestCount = store.ChatMessages.Count(x =>
                            string.Equals(x.TableCode, code, StringComparison.OrdinalIgnoreCase) &&
                            (!latestDayClosureUtc.HasValue || x.CreatedAtUtc > latestDayClosureUtc.Value) &&
                            x.SenderRole == ChatRoles.Customer)
                    })
                    .OrderByDescending(x => x.ServiceRequestCount)
                    .Take(8)
                    .ToList(),
                RecentReviews = store.Reviews
                    .Where(x => !latestDayClosureUtc.HasValue || x.CreatedAtUtc > latestDayClosureUtc.Value)
                    .OrderByDescending(x => x.CreatedAtUtc)
                    .Take(10)
                    .Select(MapReview)
                    .ToList(),
                RecentShiftClosures = store.ShiftClosures
                    .OrderByDescending(x => x.ClosedAtUtc)
                    .Take(8)
                    .Select(MapShiftClosure)
                    .ToList(),
                RecentDayClosures = store.DayClosures
                    .OrderByDescending(x => x.ClosedAtUtc)
                    .Take(8)
                    .Select(MapDayClosure)
                    .ToList()
            };
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<(bool Succeeded, string? ErrorMessage, decimal Revenue, int OrderCount)> CloseCurrentShiftAsync(string closedBy, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var store = await ReadStoreAsync(cancellationToken);
            var nowLocal = DateTime.Now;
            var shiftRange = ResolveShiftRange(nowLocal);
            var latestShiftClosureUtc = FindLatestShiftClosureUtc(store, shiftRange.Start, shiftRange.End);
            var shiftOrders = store.Orders
                .Where(x => x.Status is not OrderStatus.Draft and not OrderStatus.Cancelled and not OrderStatus.Refunded)
                .Where(x =>
                {
                    var orderTimeUtc = x.SubmittedAtUtc ?? x.UpdatedAtUtc;
                    if (latestShiftClosureUtc.HasValue && orderTimeUtc <= latestShiftClosureUtc.Value)
                    {
                        return false;
                    }

                    var local = orderTimeUtc.ToLocalTime();
                    return local >= shiftRange.Start && local < shiftRange.End;
                })
                .ToList();

            var revenue = shiftOrders.Sum(CalculateOrderSubtotal);
            var orderCount = shiftOrders.Count;
            store.ShiftClosures.Add(new ShiftClosureRecord
            {
                Id = Guid.NewGuid().ToString("N"),
                ShiftLabel = shiftRange.Label,
                ShiftStartUtc = shiftRange.Start.ToUniversalTime(),
                ShiftEndUtc = shiftRange.End.ToUniversalTime(),
                ClosedAtUtc = DateTime.UtcNow,
                ClosedBy = string.IsNullOrWhiteSpace(closedBy) ? "Admin" : closedBy.Trim(),
                Revenue = revenue,
                OrderCount = orderCount
            });

            await WriteStoreAsync(store, cancellationToken);
            return (true, null, revenue, orderCount);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<(bool Succeeded, string? ErrorMessage, decimal Revenue, int OrderCount)> CloseCurrentDayAsync(string closedBy, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var store = await ReadStoreAsync(cancellationToken);
            var nowLocal = DateTime.Now;
            var today = nowLocal.Date;
            var dayOrders = store.Orders
                .Where(x => x.Status is not OrderStatus.Draft and not OrderStatus.Cancelled and not OrderStatus.Refunded)
                .Where(x => ToLocalDate(x.SubmittedAtUtc ?? x.UpdatedAtUtc) == today)
                .ToList();

            var revenue = dayOrders.Sum(CalculateOrderSubtotal);
            var orderCount = dayOrders.Count;
            var closedAtUtc = DateTime.UtcNow;
            var closedByName = string.IsNullOrWhiteSpace(closedBy) ? "Admin" : closedBy.Trim();

            store.DayClosures.Add(new DayClosureRecord
            {
                Id = Guid.NewGuid().ToString("N"),
                Day = today,
                ClosedAtUtc = closedAtUtc,
                ClosedBy = closedByName,
                Revenue = revenue,
                OrderCount = orderCount
            });

            store.Orders.RemoveAll(x => x.Status == OrderStatus.Draft);
            foreach (var order in store.Orders.Where(x => x.Status is not OrderStatus.Cancelled and not OrderStatus.Refunded and not OrderStatus.Paid))
            {
                order.Status = OrderStatus.Paid;
                order.PaidAtUtc ??= closedAtUtc;
                order.UpdatedAtUtc = closedAtUtc;
            }

            foreach (var payment in store.PaymentRequests.Where(x => x.Status == PaymentStatuses.Pending))
            {
                payment.Status = PaymentStatuses.Completed;
                payment.UpdatedAtUtc = closedAtUtc;
                if (string.IsNullOrWhiteSpace(payment.Note))
                {
                    payment.Note = "Kết ngày tự động chốt.";
                }
            }

            foreach (var message in store.ChatMessages)
            {
                message.IsReadByStaff = true;
                message.IsReadByCustomer = true;
            }

            store.TableStates = TableCodes.Select(code => new TableStateRecord
            {
                TableCode = code,
                State = "Bàn trống",
                UpdatedAtUtc = closedAtUtc,
                UpdatedBy = closedByName
            }).ToList();

            await WriteStoreAsync(store, cancellationToken);
            return (true, null, revenue, orderCount);
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<OrderStoreRecord> ReadStoreAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_filePath))
        {
            return new OrderStoreRecord();
        }

        await using var stream = File.OpenRead(_filePath);
        return await JsonSerializer.DeserializeAsync<OrderStoreRecord>(stream, JsonOptions, cancellationToken)
            ?? new OrderStoreRecord();
    }

    private async Task WriteStoreAsync(OrderStoreRecord store, CancellationToken cancellationToken)
    {
        await using var stream = File.Create(_filePath);
        await JsonSerializer.SerializeAsync(stream, store, JsonOptions, cancellationToken);
        await SyncSqlHistoryBestEffortAsync(store, cancellationToken);
    }

    private async Task SyncSqlHistoryBestEffortAsync(OrderStoreRecord store, CancellationToken cancellationToken)
    {
        try
        {
            if (!CanUseSqlCmd())
            {
                return;
            }

            await _sqlSyncLock.WaitAsync(cancellationToken);
            try
            {
                await EnsureSqlHistorySchemaAsync(cancellationToken);
                await ExecuteSqlAsync(BuildSqlHistorySyncScript(store), cancellationToken);
            }
            finally
            {
                _sqlSyncLock.Release();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Khong the dong bo lich su order/bill sang SQL Server.");
        }
    }

    private bool CanUseSqlCmd()
    {
        var sqlCmdPath = _configuration["SqlMenu:SqlCmdPath"];
        return !string.IsNullOrWhiteSpace(sqlCmdPath) && File.Exists(sqlCmdPath);
    }

    private async Task EnsureSqlHistorySchemaAsync(CancellationToken cancellationToken)
    {
        if (_sqlSchemaReady)
        {
            return;
        }

        await ExecuteSqlAsync(@"SET NOCOUNT ON;
IF OBJECT_ID(N'dbo.TableOrderHistory', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.TableOrderHistory
    (
        OrderId nvarchar(64) NOT NULL CONSTRAINT PK_TableOrderHistory PRIMARY KEY,
        TableCode nvarchar(20) NOT NULL,
        OrderCode nvarchar(50) NULL,
        Status nvarchar(50) NOT NULL,
        CreatedAtUtc datetime2(7) NOT NULL,
        UpdatedAtUtc datetime2(7) NOT NULL,
        SubmittedAtUtc datetime2(7) NULL,
        AcceptedAtUtc datetime2(7) NULL,
        ReadyAtUtc datetime2(7) NULL,
        ServedAtUtc datetime2(7) NULL,
        PaidAtUtc datetime2(7) NULL,
        Subtotal decimal(18,2) NOT NULL,
        ServiceFee decimal(18,2) NOT NULL,
        TotalAmount decimal(18,2) NOT NULL
    );
END;

IF OBJECT_ID(N'dbo.TableOrderHistoryItem', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.TableOrderHistoryItem
    (
        OrderId nvarchar(64) NOT NULL,
        LineNumber int NOT NULL,
        ItemId int NOT NULL,
        ItemCode nvarchar(50) NOT NULL,
        ItemName nvarchar(255) NOT NULL,
        UnitPrice decimal(18,2) NOT NULL,
        Quantity int NOT NULL,
        LineTotal decimal(18,2) NOT NULL,
        Note nvarchar(1000) NULL,
        CONSTRAINT PK_TableOrderHistoryItem PRIMARY KEY (OrderId, LineNumber),
        CONSTRAINT FK_TableOrderHistoryItem_Order FOREIGN KEY (OrderId) REFERENCES dbo.TableOrderHistory(OrderId) ON DELETE CASCADE
    );
END;

IF OBJECT_ID(N'dbo.TablePaymentHistory', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.TablePaymentHistory
    (
        PaymentId nvarchar(64) NOT NULL CONSTRAINT PK_TablePaymentHistory PRIMARY KEY,
        TableCode nvarchar(20) NOT NULL,
        OrderId nvarchar(64) NULL,
        Method nvarchar(50) NOT NULL,
        Status nvarchar(50) NOT NULL,
        Amount decimal(18,2) NOT NULL,
        Note nvarchar(1000) NULL,
        RequestedAtUtc datetime2(7) NOT NULL,
        UpdatedAtUtc datetime2(7) NOT NULL
    );
END;", cancellationToken);

        _sqlSchemaReady = true;
    }

    private static string BuildSqlHistorySyncScript(OrderStoreRecord store)
    {
        var sql = new StringBuilder("SET NOCOUNT ON;\n");

        foreach (var order in store.Orders.Where(x => x.Status != OrderStatus.Draft))
        {
            var subtotal = CalculateOrderSubtotal(order);
            var serviceFee = CalculateServiceFee(subtotal);
            var totalAmount = subtotal + serviceFee;

            sql.AppendLine($@"MERGE dbo.TableOrderHistory AS target
USING (SELECT {SqlUnicode(order.Id)} AS OrderId) AS source
ON target.OrderId = source.OrderId
WHEN MATCHED THEN UPDATE SET
    TableCode = {SqlUnicode(order.TableCode)},
    OrderCode = {SqlNullableUnicode(order.OrderCode)},
    Status = {SqlUnicode(NormalizeStatusText(order.Status))},
    CreatedAtUtc = {SqlDate(order.CreatedAtUtc)},
    UpdatedAtUtc = {SqlDate(order.UpdatedAtUtc)},
    SubmittedAtUtc = {SqlNullableDate(order.SubmittedAtUtc)},
    AcceptedAtUtc = {SqlNullableDate(order.AcceptedAtUtc)},
    ReadyAtUtc = {SqlNullableDate(order.ReadyAtUtc)},
    ServedAtUtc = {SqlNullableDate(order.ServedAtUtc)},
    PaidAtUtc = {SqlNullableDate(order.PaidAtUtc)},
    Subtotal = {SqlDecimal(subtotal)},
    ServiceFee = {SqlDecimal(serviceFee)},
    TotalAmount = {SqlDecimal(totalAmount)}
WHEN NOT MATCHED THEN INSERT
    (OrderId, TableCode, OrderCode, Status, CreatedAtUtc, UpdatedAtUtc, SubmittedAtUtc, AcceptedAtUtc, ReadyAtUtc, ServedAtUtc, PaidAtUtc, Subtotal, ServiceFee, TotalAmount)
VALUES
    ({SqlUnicode(order.Id)}, {SqlUnicode(order.TableCode)}, {SqlNullableUnicode(order.OrderCode)}, {SqlUnicode(NormalizeStatusText(order.Status))}, {SqlDate(order.CreatedAtUtc)}, {SqlDate(order.UpdatedAtUtc)}, {SqlNullableDate(order.SubmittedAtUtc)}, {SqlNullableDate(order.AcceptedAtUtc)}, {SqlNullableDate(order.ReadyAtUtc)}, {SqlNullableDate(order.ServedAtUtc)}, {SqlNullableDate(order.PaidAtUtc)}, {SqlDecimal(subtotal)}, {SqlDecimal(serviceFee)}, {SqlDecimal(totalAmount)});

DELETE FROM dbo.TableOrderHistoryItem WHERE OrderId = {SqlUnicode(order.Id)};");

            for (var index = 0; index < order.Items.Count; index++)
            {
                var item = order.Items[index];
                sql.AppendLine($@"INSERT INTO dbo.TableOrderHistoryItem
    (OrderId, LineNumber, ItemId, ItemCode, ItemName, UnitPrice, Quantity, LineTotal, Note)
VALUES
    ({SqlUnicode(order.Id)}, {index + 1}, {item.ItemId}, {SqlUnicode(item.ItemCode)}, {SqlUnicode(item.Name)}, {SqlDecimal(item.UnitPrice)}, {item.Quantity}, {SqlDecimal(item.UnitPrice * item.Quantity)}, {SqlNullableUnicode(item.Note)});");
            }
        }

        foreach (var payment in store.PaymentRequests)
        {
            sql.AppendLine($@"MERGE dbo.TablePaymentHistory AS target
USING (SELECT {SqlUnicode(payment.Id)} AS PaymentId) AS source
ON target.PaymentId = source.PaymentId
WHEN MATCHED THEN UPDATE SET
    TableCode = {SqlUnicode(payment.TableCode)},
    OrderId = {SqlNullableUnicode(payment.OrderId)},
    Method = {SqlUnicode(payment.Method)},
    Status = {SqlUnicode(payment.Status)},
    Amount = {SqlDecimal(payment.Amount)},
    Note = {SqlNullableUnicode(payment.Note)},
    RequestedAtUtc = {SqlDate(payment.RequestedAtUtc)},
    UpdatedAtUtc = {SqlDate(payment.UpdatedAtUtc)}
WHEN NOT MATCHED THEN INSERT
    (PaymentId, TableCode, OrderId, Method, Status, Amount, Note, RequestedAtUtc, UpdatedAtUtc)
VALUES
    ({SqlUnicode(payment.Id)}, {SqlUnicode(payment.TableCode)}, {SqlNullableUnicode(payment.OrderId)}, {SqlUnicode(payment.Method)}, {SqlUnicode(payment.Status)}, {SqlDecimal(payment.Amount)}, {SqlNullableUnicode(payment.Note)}, {SqlDate(payment.RequestedAtUtc)}, {SqlDate(payment.UpdatedAtUtc)});");
        }

        return sql.ToString();
    }

    private async Task<string> ExecuteSqlAsync(string sql, CancellationToken cancellationToken)
    {
        var sqlCmdPath = _configuration["SqlMenu:SqlCmdPath"];
        var server = _configuration["SqlMenu:Server"] ?? "localhost";
        var database = _configuration["SqlMenu:Database"] ?? "TableOrderDb";

        if (string.IsNullOrWhiteSpace(sqlCmdPath) || !File.Exists(sqlCmdPath))
        {
            throw new InvalidOperationException("Khong tim thay sqlcmd de thao tac voi SQL Server.");
        }

        var tempQueryFile = Path.Combine(Path.GetTempPath(), $"order-history-query-{Guid.NewGuid():N}.sql");
        var tempOutputFile = Path.Combine(Path.GetTempPath(), $"order-history-output-{Guid.NewGuid():N}.txt");

        try
        {
            await File.WriteAllTextAsync(tempQueryFile, sql, Encoding.UTF8, cancellationToken);

            var psi = new ProcessStartInfo
            {
                FileName = sqlCmdPath,
                Arguments = $"-S {server} -d {database} -E -b -No -u -h -1 -s \"|\" -w 65535 -y 8000 -Y 8000 -i \"{tempQueryFile}\" -o \"{tempOutputFile}\"",
                RedirectStandardError = true,
                StandardErrorEncoding = Encoding.Unicode,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = new Process { StartInfo = psi };
            process.Start();
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);

            var error = (await errorTask).Trim();
            var output = File.Exists(tempOutputFile)
                ? (await File.ReadAllTextAsync(tempOutputFile, Encoding.Unicode, cancellationToken)).Trim()
                : string.Empty;

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? "sqlcmd tra ve loi khong xac dinh." : error);
            }

            return output;
        }
        finally
        {
            TryDelete(tempQueryFile);
            TryDelete(tempOutputFile);
        }
    }

    private static string NormalizeStatusText(string status)
    {
        return status switch
        {
            "SubmitteÄ‘" or "Submitteđ" => "Submitted",
            "AccepteÄ‘" or "Accepteđ" => "Accepted",
            "ServeÄ‘" or "Serveđ" => "Served",
            "PaiÄ‘" or "Paiđ" => "Paid",
            "CancelleÄ‘" or "Cancelleđ" => "Cancelled",
            "RefundeÄ‘" or "Refundeđ" => "Refunded",
            _ => status
        };
    }

    private static string SqlDate(DateTime value)
        => $"CONVERT(datetime2(7), '{value.ToUniversalTime():yyyy-MM-ddTHH:mm:ss.fffffff}', 126)";

    private static string SqlNullableDate(DateTime? value)
        => value.HasValue ? SqlDate(value.Value) : "NULL";

    private static string SqlDecimal(decimal value)
        => value.ToString("0.##", CultureInfo.InvariantCulture);

    private static string SqlUnicode(string value)
        => $"N'{EscapeSql(value)}'";

    private static string SqlNullableUnicode(string? value)
        => string.IsNullOrWhiteSpace(value) ? "NULL" : SqlUnicode(value.Trim());

    private static string EscapeSql(string value)
        => value.Trim().Replace("'", "''");

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    private static CustomerCartItemViewModel MapCartItem(TableOrderItemRecord item)
    {
        return new CustomerCartItemViewModel
        {
            ItemId = item.ItemId,
            ItemCode = item.ItemCode,
            Name = item.Name,
            UnitPrice = item.UnitPrice,
            Quantity = item.Quantity,
            Note = item.Note
        };
    }

    private static SubmittedOrderSummaryViewModel MapSubmittedOrder(TableOrderRecord order, TablePaymentRequestRecord? payment)
    {
        return new SubmittedOrderSummaryViewModel
        {
            OrderId = order.Id,
            OrderCode = order.OrderCode ?? order.Id[..6].ToUpperInvariant(),
            StatusKey = order.Status,
            StatusLabel = MapOrderStatusLabel(order.Status),
            SubmittedTimeLabel = (order.SubmittedAtUtc ?? order.UpdatedAtUtc).ToLocalTime().ToString("HH:mm dd/MM"),
            ItemCount = order.Items.Sum(x => x.Quantity),
            TotalAmount = CalculateOrderSubtotal(order),
            ProgressStep = ResolveCustomerProgressStep(order.Status),
            Items = order.Items.Select(item => new TableOrderLineViewModel
            {
                Name = item.Name,
                Quantity = item.Quantity,
                UnitPrice = item.UnitPrice,
                LineTotal = item.UnitPrice * item.Quantity,
                Note = item.Note
            }).ToList(),
            PaymentLabel = payment is null
                ? "Chưa yêu cầu thanh toán"
                : $"{payment.StatusLabelOrDefault()} - {MapPaymentMethodLabel(payment.Method)}"
        };
    }

    private static ChatMessageViewModel MapChatMessage(TableChatMessageRecord message)
    {
        var direction = message.SenderRole switch
        {
            ChatRoles.Customer => "customer",
            ChatRoles.Staff => "staff",
            _ => "system"
        };

        return new ChatMessageViewModel
        {
            MessageId = message.Id,
            TableCode = message.TableCode,
            SenderName = ResolveChatSenderName(message),
            SenderRole = message.SenderRole,
            Message = message.Message,
            MessageType = message.MessageType,
            TimeLabel = message.CreatedAtUtc.ToLocalTime().ToString("HH:mm"),
            Direction = direction
        };
    }

    private static string ResolveChatSenderName(TableChatMessageRecord message)
    {
        return message.SenderRole switch
        {
            ChatRoles.Staff => "Nhân viên",
            ChatRoles.System => "Hệ thống",
            ChatRoles.Customer => string.IsNullOrWhiteSpace(message.SenderName) ? $"Bàn {message.TableCode}" : message.SenderName,
            _ => string.IsNullOrWhiteSpace(message.SenderName) ? "Tin nhắn" : message.SenderName
        };
    }

    private static PaymentRequestViewModel MapPaymentRequest(TablePaymentRequestRecord payment, decimal fallbackAmount)
    {
        return new PaymentRequestViewModel
        {
            RequestId = payment.Id,
            TableCode = payment.TableCode,
            Method = payment.Method,
            MethodLabel = MapPaymentMethodLabel(payment.Method),
            Note = payment.Note,
            Status = payment.Status,
            StatusLabel = payment.StatusLabelOrDefault(),
            RequestedAtLabel = payment.RequestedAtUtc.ToLocalTime().ToString("HH:mm dd/MM"),
            Amount = payment.Amount <= 0 ? fallbackAmount : payment.Amount
        };
    }

    private static AdminPaymentHistoryItemViewModel MapAdminPaymentHistory(TablePaymentRequestRecord payment, List<TableOrderRecord> orders)
    {
        var order = orders.FirstOrDefault(x => string.Equals(x.Id, payment.OrderId, StringComparison.OrdinalIgnoreCase));
        return new AdminPaymentHistoryItemViewModel
        {
            RequestId = payment.Id,
            TableCode = payment.TableCode,
            OrderCode = order?.OrderCode ?? (string.IsNullOrWhiteSpace(payment.OrderId) ? "-" : payment.OrderId[..Math.Min(6, payment.OrderId.Length)].ToUpperInvariant()),
            MethodLabel = MapPaymentMethodLabel(payment.Method),
            StatusLabel = payment.StatusLabelOrDefault(),
            Amount = payment.Amount,
            RequestedAtLabel = payment.RequestedAtUtc.ToLocalTime().ToString("dd/MM HH:mm"),
            UpdatedAtLabel = payment.UpdatedAtUtc.ToLocalTime().ToString("dd/MM HH:mm"),
            Note = string.IsNullOrWhiteSpace(payment.Note) ? "-" : payment.Note
        };
    }

    private static AdminShiftCloseViewModel MapShiftClosure(ShiftClosureRecord shift)
    {
        var start = shift.ShiftStartUtc.ToLocalTime();
        var end = shift.ShiftEndUtc.ToLocalTime();
        return new AdminShiftCloseViewModel
        {
            ShiftLabel = shift.ShiftLabel,
            TimeRangeLabel = $"{start:dd/MM HH:mm} - {end:HH:mm}",
            ClosedAtLabel = shift.ClosedAtUtc.ToLocalTime().ToString("dd/MM HH:mm"),
            ClosedBy = shift.ClosedBy,
            Revenue = shift.Revenue,
            OrderCount = shift.OrderCount
        };
    }

    private static DateTime? FindLatestShiftClosureUtc(OrderStoreRecord store, DateTime shiftStart, DateTime shiftEnd)
    {
        var shiftStartUtc = shiftStart.ToUniversalTime();
        var shiftEndUtc = shiftEnd.ToUniversalTime();

        return store.ShiftClosures
            .Where(x => x.ShiftStartUtc == shiftStartUtc && x.ShiftEndUtc == shiftEndUtc)
            .OrderByDescending(x => x.ClosedAtUtc)
            .Select(x => (DateTime?)x.ClosedAtUtc)
            .FirstOrDefault();
    }

    private static DateTime? FindLatestDayClosureUtc(OrderStoreRecord store)
    {
        return store.DayClosures
            .OrderByDescending(x => x.ClosedAtUtc)
            .Select(x => (DateTime?)x.ClosedAtUtc)
            .FirstOrDefault();
    }

    private static AdminDayCloseViewModel MapDayClosure(DayClosureRecord day)
    {
        return new AdminDayCloseViewModel
        {
            DayLabel = day.Day.ToString("dd/MM/yyyy"),
            ClosedAtLabel = day.ClosedAtUtc.ToLocalTime().ToString("dd/MM HH:mm"),
            ClosedBy = day.ClosedBy,
            Revenue = day.Revenue,
            OrderCount = day.OrderCount
        };
    }

    private static ReviewEntryViewModel MapReview(TableReviewRecord review)
    {
        return new ReviewEntryViewModel
        {
            TableCode = review.TableCode,
            FoodRating = review.FoodRating,
            ServiceRating = review.ServiceRating,
            Comment = review.Comment,
            CreatedAtLabel = review.CreatedAtUtc.ToLocalTime().ToString("HH:mm dd/MM")
        };
    }

    private static string BuildCustomizationNote(CustomerAddToCartInputModel request)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(request.SugarLevel)) parts.Add($"Duong: {request.SugarLevel.Trim()}");
        if (!string.IsNullOrWhiteSpace(request.IceLevel)) parts.Add($"Da: {request.IceLevel.Trim()}");
        if (!string.IsNullOrWhiteSpace(request.ToppingChoice)) parts.Add($"Topping: {request.ToppingChoice.Trim()}");
        if (!string.IsNullOrWhiteSpace(request.Note)) parts.Add(request.Note.Trim());
        return string.Join(" | ", parts);
    }

    private static decimal CalculateOrderSubtotal(TableOrderRecord order) => order.Items.Sum(x => x.UnitPrice * x.Quantity);

    private static decimal CalculateServiceFee(decimal subtotal) => subtotal <= 0 ? 0 : Math.Round(subtotal * 0.05m, 0);

    private static string GenerateNextOrderCode(OrderStoreRecord store, string tableCode, string? excludingOrderId = null)
    {
        var nextNumber = store.Orders.Count(x =>
            string.Equals(x.TableCode, tableCode, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(x.Id, excludingOrderId, StringComparison.OrdinalIgnoreCase) &&
            x.Status is not OrderStatus.Draft and not OrderStatus.Cancelled and not OrderStatus.Refunded and not OrderStatus.Paid) + 1;

        return $"{tableCode}-{nextNumber:000}";
    }

    private static OrderLineRef? FindOrderLineByKey(OrderStoreRecord store, string lineKey)
    {
        var parts = lineKey.Split(':', 2);
        if (parts.Length != 2 || !int.TryParse(parts[1], out var itemIndex) || itemIndex < 0)
        {
            return null;
        }

        var order = store.Orders.FirstOrDefault(x => string.Equals(x.Id, parts[0], StringComparison.OrdinalIgnoreCase));
        if (order is null || itemIndex >= order.Items.Count)
        {
            return null;
        }

        return new OrderLineRef(order, order.Items[itemIndex], itemIndex);
    }

    private static TableOrderRecord? FindTargetOrderForSplit(OrderStoreRecord store, string tableCode, string status)
    {
        return store.Orders
            .Where(x => string.Equals(x.TableCode, tableCode, StringComparison.OrdinalIgnoreCase))
            .Where(x => x.Status == status)
            .Where(x => x.Status is not OrderStatus.Cancelled and not OrderStatus.Refunded and not OrderStatus.Paid)
            .OrderByDescending(x => x.UpdatedAtUtc)
            .FirstOrDefault();
    }

    private static TableOrderRecord CreateSplitTargetOrder(OrderStoreRecord store, string tableCode, TableOrderRecord sourceOrder, DateTime updatedAtUtc)
    {
        return new TableOrderRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            TableCode = tableCode,
            Status = sourceOrder.Status,
            OrderCode = sourceOrder.Status == OrderStatus.Draft ? null : GenerateNextOrderCode(store, tableCode),
            CreatedAtUtc = updatedAtUtc,
            UpdatedAtUtc = updatedAtUtc,
            SubmittedAtUtc = sourceOrder.SubmittedAtUtc.HasValue ? updatedAtUtc : null,
            AcceptedAtUtc = sourceOrder.AcceptedAtUtc.HasValue ? updatedAtUtc : null,
            ReadyAtUtc = sourceOrder.ReadyAtUtc.HasValue ? updatedAtUtc : null,
            ServedAtUtc = sourceOrder.ServedAtUtc.HasValue ? updatedAtUtc : null
        };
    }

    private static TableOrderItemRecord CloneOrderItem(TableOrderItemRecord item, int quantity)
    {
        return new TableOrderItemRecord
        {
            ItemId = item.ItemId,
            ItemCode = item.ItemCode,
            Name = item.Name,
            UnitPrice = item.UnitPrice,
            Quantity = quantity,
            Note = item.Note
        };
    }

    private static void AddOrMergeOrderItem(TableOrderRecord order, TableOrderItemRecord item)
    {
        var existingItem = order.Items.FirstOrDefault(x =>
            x.ItemId == item.ItemId &&
            string.Equals(x.ItemCode, item.ItemCode, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(x.Name, item.Name, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(x.Note, item.Note, StringComparison.OrdinalIgnoreCase) &&
            x.UnitPrice == item.UnitPrice);

        if (existingItem is null)
        {
            order.Items.Add(item);
            return;
        }

        existingItem.Quantity += item.Quantity;
    }

    private static void MoveTableChatMessages(OrderStoreRecord store, string fromTableCode, string toTableCode)
    {
        foreach (var message in store.ChatMessages.Where(x => string.Equals(x.TableCode, fromTableCode, StringComparison.OrdinalIgnoreCase)))
        {
            message.TableCode = toTableCode;
        }
    }

    private static bool HasOpenItems(OrderStoreRecord store, string tableCode)
    {
        return store.Orders.Any(x =>
            string.Equals(x.TableCode, tableCode, StringComparison.OrdinalIgnoreCase) &&
            x.Status is not OrderStatus.Cancelled and not OrderStatus.Refunded and not OrderStatus.Paid &&
            x.Items.Count > 0);
    }

    private static void UpdatePendingPaymentAmountsForTable(OrderStoreRecord store, string tableCode)
    {
        var subtotal = store.Orders
            .Where(x => string.Equals(x.TableCode, tableCode, StringComparison.OrdinalIgnoreCase))
            .Where(x => x.Status is not OrderStatus.Draft and not OrderStatus.Cancelled and not OrderStatus.Refunded and not OrderStatus.Paid)
            .Sum(CalculateOrderSubtotal);
        var total = subtotal + CalculateServiceFee(subtotal);

        foreach (var payment in store.PaymentRequests.Where(x =>
                     string.Equals(x.TableCode, tableCode, StringComparison.OrdinalIgnoreCase) &&
                     x.Status == PaymentStatuses.Pending))
        {
            payment.Amount = total;
            payment.UpdatedAtUtc = DateTime.UtcNow;
        }
    }

    private static void SetTableState(OrderStoreRecord store, string tableCode, string state, DateTime updatedAtUtc, string updatedBy)
    {
        var tableState = store.TableStates.FirstOrDefault(x =>
            string.Equals(x.TableCode, tableCode, StringComparison.OrdinalIgnoreCase));
        if (tableState is null)
        {
            tableState = new TableStateRecord { TableCode = tableCode };
            store.TableStates.Add(tableState);
        }

        tableState.State = state;
        tableState.UpdatedAtUtc = updatedAtUtc;
        tableState.UpdatedBy = updatedBy;
    }

    private static void ResetTableSession(OrderStoreRecord store, string tableCode, DateTime updatedAtUtc, string updatedBy)
    {
        store.Orders.RemoveAll(x =>
            string.Equals(x.TableCode, tableCode, StringComparison.OrdinalIgnoreCase) &&
            x.Status == OrderStatus.Draft);

        SetTableState(store, tableCode, "Bàn trống", updatedAtUtc, updatedBy);
    }

    private static void ClearTableChatSession(OrderStoreRecord store, string tableCode)
    {
        store.ChatMessages.RemoveAll(x => string.Equals(x.TableCode, tableCode, StringComparison.OrdinalIgnoreCase));
    }

    private static string ResolveTableStateLabel(OrderStoreRecord store, string tableCode)
    {
        var manualState = store.TableStates
            .Where(x => string.Equals(x.TableCode, tableCode, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(x => x.UpdatedAtUtc)
            .Select(x => x.State)
            .FirstOrDefault();

        if (!string.IsNullOrWhiteSpace(manualState))
        {
            return manualState;
        }

        var latestOrder = store.Orders
            .Where(x => string.Equals(x.TableCode, tableCode, StringComparison.OrdinalIgnoreCase) && x.Items.Count > 0)
            .OrderByDescending(x => x.UpdatedAtUtc)
            .FirstOrDefault();

        return latestOrder switch
        {
            null => "Bàn trống",
            { Status: OrderStatus.Draft } => "Đang ngồi và chọn món",
            { Status: OrderStatus.Ready } => "Món sẵn sàng trả",
            { Status: OrderStatus.Served } => "Đang dùng bữa",
            { Status: OrderStatus.Paid } => "Đã thanh toán",
            _ => "Đang phục vụ"
        };
    }

    private static List<OrderTimelineEntryViewModel> BuildTimeline(
        TableOrderRecord? draft,
        List<TableOrderRecord> submittedOrders,
        List<TablePaymentRequestRecord> paymentRequests,
        List<TableChatMessageRecord> chatMessages,
        List<TableReviewRecord> reviews)
    {
        var timeline = new List<(DateTime SortAtUtc, OrderTimelineEntryViewModel Entry)>();

        if (draft is not null && draft.Items.Count > 0)
        {
            timeline.Add((draft.UpdatedAtUtc, new OrderTimelineEntryViewModel
            {
                TimeLabel = draft.UpdatedAtUtc.ToLocalTime().ToString("HH:mm"),
                Title = $"Giỏ hàng nhóm đang có {draft.Items.Sum(x => x.Quantity)} món.",
                Detail = "Khách tại bàn vẫn có thể tiếp tục thêm món vào giỏ chung.",
                State = "warning"
            }));
        }

        foreach (var order in submittedOrders.Take(4))
        {
            var at = order.SubmittedAtUtc ?? order.UpdatedAtUtc;
            timeline.Add((at, new OrderTimelineEntryViewModel
            {
                TimeLabel = at.ToLocalTime().ToString("HH:mm"),
                Title = $"Order #{order.OrderCode ?? order.Id[..6].ToUpperInvariant()} - {MapOrderStatusLabel(order.Status)}",
                Detail = $"{order.Items.Sum(x => x.Quantity)} món | {CalculateOrderSubtotal(order):N0}đ",
                State = order.Status is OrderStatus.Cancelled or OrderStatus.Refunded ? "danger" : "success"
            }));
        }

        if (paymentRequests.FirstOrDefault() is { } payment)
        {
            timeline.Add((payment.UpdatedAtUtc, new OrderTimelineEntryViewModel
            {
                TimeLabel = payment.UpdatedAtUtc.ToLocalTime().ToString("HH:mm"),
                Title = $"Yêu cầu thanh toán - {payment.StatusLabelOrDefault()}",
                Detail = MapPaymentMethodLabel(payment.Method),
                State = "neutral"
            }));
        }

        if (chatMessages.LastOrDefault() is { } lastChat)
        {
            timeline.Add((lastChat.CreatedAtUtc, new OrderTimelineEntryViewModel
            {
                TimeLabel = lastChat.CreatedAtUtc.ToLocalTime().ToString("HH:mm"),
                Title = $"Chat {lastChat.SenderName}",
                Detail = lastChat.Message,
                State = "neutral"
            }));
        }

        if (reviews.FirstOrDefault() is { } latestReview)
        {
            timeline.Add((latestReview.CreatedAtUtc, new OrderTimelineEntryViewModel
            {
                TimeLabel = latestReview.CreatedAtUtc.ToLocalTime().ToString("HH:mm"),
                Title = "Danh gia sau bua an",
                Detail = $"Món: {latestReview.FoodRating}/5 | Dịch vụ: {latestReview.ServiceRating}/5",
                State = "success"
            }));
        }

        if (timeline.Count == 0)
        {
            timeline.Add((DateTime.UtcNow, new OrderTimelineEntryViewModel
            {
                TimeLabel = DateTime.Now.ToString("HH:mm"),
                Title = "Bàn chưa có thao tác nào.",
                Detail = "Quét QR, chọn món, chat hoặc yêu cầu thanh toán để bắt đầu.",
                State = "neutral"
            }));
        }

        return timeline
            .OrderByDescending(x => x.SortAtUtc)
            .Take(6)
            .Select(x => x.Entry)
            .ToList();
    }

    private static StaffOrderTicketViewModel MapStaffOrder(TableOrderRecord order, TablePaymentRequestRecord? payment)
    {
        var notes = order.Items
            .Where(x => !string.IsNullOrWhiteSpace(x.Note))
            .Select(x => $"{x.Name}: {x.Note}")
            .ToList();

        return new StaffOrderTicketViewModel
        {
            OrderId = order.Id,
            TableCode = order.TableCode,
            OrderCode = order.OrderCode ?? order.Id[..6].ToUpperInvariant(),
            ItemsSummary = string.Join(", ", order.Items.Select(x => $"{x.Name} x{x.Quantity}")),
            NotesSummary = notes.Count > 0 ? string.Join(" | ", notes) : "Không có ghi chú thêm.",
            StatusKey = order.Status,
            StatusLabel = MapOrderStatusLabel(order.Status),
            SubmittedTimeLabel = (order.SubmittedAtUtc ?? order.UpdatedAtUtc).ToLocalTime().ToString("HH:mm"),
            TotalAmount = CalculateOrderSubtotal(order),
            PaymentLabel = payment is null
                ? "Chưa yêu cầu thanh toán"
                : $"{payment.StatusLabelOrDefault()} - {MapPaymentMethodLabel(payment.Method)}"
        };
    }

    private static List<StaffKitchenItemViewModel> BuildKitchenItems(List<TableOrderRecord> orders)
    {
        return orders
            .Where(x => x.Status is OrderStatus.Submitted or OrderStatus.Accepted or OrderStatus.Preparing or OrderStatus.Ready)
            .SelectMany(order => order.Items.Select(item => new StaffKitchenItemViewModel
            {
                OrderId = order.Id,
                OrderCode = order.OrderCode ?? order.Id[..6].ToUpperInvariant(),
                TableCode = order.TableCode,
                ItemName = item.Name,
                Quantity = item.Quantity,
                Note = string.IsNullOrWhiteSpace(item.Note) ? "Không có ghi chú" : item.Note,
                StatusKey = order.Status,
                StatusLabel = MapOrderStatusLabel(order.Status),
                SubmittedTimeLabel = (order.SubmittedAtUtc ?? order.UpdatedAtUtc).ToLocalTime().ToString("HH:mm dd/MM")
            }))
            .OrderBy(x => x.TableCode)
            .ThenByDescending(x => x.SubmittedTimeLabel)
            .ToList();
    }

    private static List<TableStatusViewModel> BuildTableStatuses(OrderStoreRecord store)
    {
        return TableCodes.Select(code =>
        {
            var orders = store.Orders
                .Where(x => string.Equals(x.TableCode, code, StringComparison.OrdinalIgnoreCase))
                .ToList();
            var activeOrders = orders
                .Where(x => x.Status is OrderStatus.Submitted or OrderStatus.Accepted or OrderStatus.Preparing or OrderStatus.Ready or OrderStatus.Served)
                .ToList();
            var latestOrder = orders.OrderByDescending(x => x.UpdatedAtUtc).FirstOrDefault();
            var latestChat = store.ChatMessages
                .Where(x => string.Equals(x.TableCode, code, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(x => x.CreatedAtUtc)
                .FirstOrDefault();
            var latestActivity = new[]
                {
                    latestOrder?.UpdatedAtUtc,
                    latestChat?.CreatedAtUtc,
                    store.TableStates.FirstOrDefault(x => string.Equals(x.TableCode, code, StringComparison.OrdinalIgnoreCase))?.UpdatedAtUtc
                }
                .Where(x => x.HasValue)
                .Select(x => x!.Value)
                .DefaultIfEmpty(DateTime.UtcNow)
                .Max();

            var outstanding = orders
                .Where(x => x.Status is not OrderStatus.Draft and not OrderStatus.Cancelled and not OrderStatus.Refunded and not OrderStatus.Paid)
                .Sum(CalculateOrderSubtotal);
            var orderedItems = activeOrders
                .SelectMany(x => x.Items)
                .GroupBy(x => x.Name)
                .Select(x => $"{x.Key} x{x.Sum(i => i.Quantity)}")
                .OrderBy(x => x)
                .ToList();
            var detailedItems = activeOrders
                .SelectMany(order => order.Items.Select((item, index) => new
                {
                    Order = order,
                    Item = item,
                    ItemIndex = index
                }))
                .Select(x => new TableOrderLineViewModel
                {
                    LineKey = $"{x.Order.Id}:{x.ItemIndex}",
                    Name = x.Item.Name,
                    UnitPrice = x.Item.UnitPrice,
                    Quantity = x.Item.Quantity,
                    MaxQuantity = x.Item.Quantity,
                    LineTotal = x.Item.UnitPrice * x.Item.Quantity,
                    Note = x.Item.Note
                })
                .OrderBy(x => x.Name)
                .ThenBy(x => x.Note)
                .ToList();

            var stateLabel = ResolveTableStateLabel(store, code);
            return new TableStatusViewModel
            {
                TableCode = code,
                State = stateLabel,
                BlocksCustomerAccess = BlocksCustomerAccess(stateLabel),
                OutstandingTotal = outstanding + CalculateServiceFee(outstanding),
                LastActivityLabel = latestActivity.ToLocalTime().ToString("HH:mm dd/MM"),
                HasActiveOrder = activeOrders.Count > 0,
                ActiveOrderCount = activeOrders.Count,
                OrderedItems = orderedItems,
                OrderedItemsSummary = orderedItems.Count > 0
                    ? string.Join(", ", orderedItems)
                    : "Chưa có món nào được gửi",
                DetailedItems = detailedItems
            };
        }).ToList();
    }

    private static List<StaffChatThreadViewModel> BuildChatThreads(OrderStoreRecord store)
    {
        return store.ChatMessages
            .Where(x => x.SenderRole != ChatRoles.System)
            .GroupBy(x => x.TableCode)
            .Where(group => group.Any())
            .OrderByDescending(group => group.Max(x => x.CreatedAtUtc))
            .Select(group =>
            {
                var messages = group.OrderByDescending(x => x.CreatedAtUtc).Take(80).OrderBy(x => x.CreatedAtUtc).ToList();
                var last = messages.LastOrDefault();
                return new StaffChatThreadViewModel
                {
                    TableCode = group.Key,
                    LastMessageLabel = last is null ? "Chưa có hội thoại" : $"{last.CreatedAtUtc.ToLocalTime():HH:mm} - {ResolveChatSenderName(last)}: {last.Message}",
                    PendingCount = group.Count(x => x.SenderRole == ChatRoles.Customer && !x.IsReadByStaff),
                    Messages = messages.Select(MapChatMessage).ToList()
                };
            })
            .OrderByDescending(x => x.PendingCount)
            .Take(20)
            .ToList();
    }

    private static string ResolveAdminPaymentLabel(TableOrderRecord order, List<TablePaymentRequestRecord> payments)
    {
        if (order.Status == OrderStatus.Paid || order.PaidAtUtc.HasValue)
        {
            var completedPayment = payments
                .Where(x => string.Equals(x.OrderId, order.Id, StringComparison.OrdinalIgnoreCase))
                .Where(x => x.Status == PaymentStatuses.Completed)
                .OrderByDescending(x => x.UpdatedAtUtc)
                .FirstOrDefault();

            return completedPayment is null
                ? "Đã thu tiền"
                : $"{completedPayment.StatusLabelOrDefault()} | {MapPaymentMethodLabel(completedPayment.Method)}";
        }

        var pendingPayment = payments
            .Where(x => string.Equals(x.OrderId, order.Id, StringComparison.OrdinalIgnoreCase)
                || string.Equals(x.TableCode, order.TableCode, StringComparison.OrdinalIgnoreCase))
            .Where(x => x.Status == PaymentStatuses.Pending)
            .OrderByDescending(x => x.UpdatedAtUtc)
            .FirstOrDefault();

        return pendingPayment is null
            ? "Chưa thu tiền"
            : $"{pendingPayment.StatusLabelOrDefault()} | {MapPaymentMethodLabel(pendingPayment.Method)}";
    }

    private static List<StaffChatThreadViewModel> BuildAdminChatThreads(OrderStoreRecord store, DateTime? latestDayClosureUtc)
    {
        return store.ChatMessages
            .Where(x => x.SenderRole != ChatRoles.System)
            .Where(x => !latestDayClosureUtc.HasValue || x.CreatedAtUtc > latestDayClosureUtc.Value)
            .GroupBy(x => x.TableCode, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var messages = group.OrderBy(x => x.CreatedAtUtc).ToList();
                var last = messages.LastOrDefault();
                return new
                {
                    LastAtUtc = last?.CreatedAtUtc ?? DateTime.MinValue,
                    Thread = new StaffChatThreadViewModel
                    {
                    TableCode = group.Key,
                    LastMessageLabel = last is null ? "Chưa có hội thoại" : $"{last.CreatedAtUtc.ToLocalTime():dd/MM HH:mm} - {ResolveChatSenderName(last)}: {last.Message}",
                    PendingCount = group.Count(x => x.SenderRole == ChatRoles.Customer && !x.IsReadByStaff),
                    Messages = messages.Select(MapChatMessage).ToList()
                    }
                };
            })
            .OrderByDescending(x => x.LastAtUtc)
            .ThenBy(x => x.Thread.TableCode)
            .Select(x => x.Thread)
            .ToList();
    }

    private static string InferCustomerMessageType(string message)
    {
        var normalized = message.ToLowerInvariant();
        if (normalized.Contains("thanh toan")) return "payment";
        if (normalized.Contains("khan") || normalized.Contains("da") || normalized.Contains("bat") || normalized.Contains("dua")) return "service";
        return "chat";
    }

    private static TableChatMessageRecord CreateSystemMessage(string tableCode, string message, string messageType)
    {
        return new TableChatMessageRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            TableCode = tableCode,
            SenderName = "Hệ thống",
            SenderRole = ChatRoles.System,
            Message = message,
            MessageType = messageType,
            CreatedAtUtc = DateTime.UtcNow,
            IsReadByCustomer = false,
            IsReadByStaff = true
        };
    }

    private static string? NormalizePaymentMethod(string? value)
    {
        var normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            "cash" => "cash",
            "bankqr" => "bankqr",
            "ewallet" => "ewallet",
            _ => null
        };
    }

    private static string? NormalizeOrderStatus(string? value)
    {
        var normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            "submitteđ" => OrderStatus.Submitted,
            "accepted" => OrderStatus.Accepted,
            "preparing" => OrderStatus.Preparing,
            "ready" => OrderStatus.Ready,
            "served" => OrderStatus.Served,
            "paid" => OrderStatus.Paid,
            "cancelled" => OrderStatus.Cancelled,
            "refunded" => OrderStatus.Refunded,
            _ => null
        };
    }

    private static string? NormalizeTableState(string? value)
    {
        var normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            "bàn trống" or "ban trong" => "Bàn trống",
            "đang ngồi" or "dang ngoi" => "Đang ngồi",
            "đang phục vụ" or "dang phuc vu" => "Đang phục vụ",
            "chờ dọn dẹp" or "cho don dep" => "Chờ dọn dẹp",
            "đã thanh toán" or "da thanh toan" => "Đã thanh toán",
            "bàn lỗi" or "ban loi" => "Bàn lỗi",
            "ngưng phục vụ" or "ngung phuc vu" => "Ngưng phục vụ",
            "đang sửa" or "dang sua" => "Đang sửa",
            _ => null
        };
    }

    private static bool BlocksCustomerAccess(string? state)
    {
        var normalized = (state ?? string.Empty).Trim().ToLowerInvariant();
        return normalized is "bàn lỗi" or "ban loi" or "ngưng phục vụ" or "ngung phuc vu" or "đang sửa" or "dang sua";
    }

    private static string MapPaymentMethodLabel(string method)
    {
        return method switch
        {
            "cash" => "Tiền mặt",
            "bankqr" => "Chuyển khoản QR",
            "ewallet" => "Ví điện tử",
            _ => "Khac"
        };
    }

    private static int ResolveCustomerProgressStep(string status)
    {
        return status switch
        {
            OrderStatus.Submitted => 1,
            OrderStatus.Accepted => 2,
            OrderStatus.Preparing => 3,
            OrderStatus.Ready => 4,
            OrderStatus.Served or OrderStatus.Paid => 5,
            OrderStatus.Cancelled or OrderStatus.Refunded => 0,
            _ => 1
        };
    }

    private static string MapOrderStatusLabel(string status)
    {
        return status switch
        {
            OrderStatus.Draft => "đang chọn món",
            OrderStatus.Submitted => "Mới gửi",
            OrderStatus.Accepted => "Đã nhận don",
            OrderStatus.Preparing => "Đang chế biến",
            OrderStatus.Ready => "Sẵn sàng tra món",
            OrderStatus.Served => "Đã phục vụ",
            OrderStatus.Paid => "Đã thanh toán",
            OrderStatus.Cancelled => "Đã hủy",
            OrderStatus.Refunded => "Hoàn trả",
            _ => status
        };
    }

    private static DateTime ToLocalDate(DateTime utcOrLocal) => utcOrLocal.ToLocalTime().Date;

    private static (DateTime Start, DateTime End, string Label) ResolveShiftRange(DateTime nowLocal)
    {
        if (nowLocal.Hour < 14)
        {
            return (nowLocal.Date.AddHours(6), nowLocal.Date.AddHours(14), "Ca sang");
        }

        if (nowLocal.Hour < 22)
        {
            return (nowLocal.Date.AddHours(14), nowLocal.Date.AddHours(22), "Ca chieu");
        }

        return (nowLocal.Date.AddHours(22), nowLocal.Date.AddDays(1).AddHours(6), "Ca toi");
    }

    private static class OrderStatus
    {
        public const string Draft = "Draft";
        public const string Submitted = "Submitteđ";
        public const string Accepted = "Accepteđ";
        public const string Preparing = "Preparing";
        public const string Ready = "Ready";
        public const string Served = "Serveđ";
        public const string Paid = "Paiđ";
        public const string Cancelled = "Cancelleđ";
        public const string Refunded = "Refundeđ";
    }

    private static class PaymentStatuses
    {
        public const string Pending = "Pending";
        public const string Completed = "Completed";
    }

    private static class ChatRoles
    {
        public const string Customer = "Customer";
        public const string Staff = "Staff";
        public const string System = "System";
    }

    private sealed class OrderStoreRecord
    {
        public List<TableOrderRecord> Orders { get; set; } = [];
        public List<TableChatMessageRecord> ChatMessages { get; set; } = [];
        public List<TablePaymentRequestRecord> PaymentRequests { get; set; } = [];
        public List<TableReviewRecord> Reviews { get; set; } = [];
        public List<TableStateRecord> TableStates { get; set; } = [];
        public List<ShiftClosureRecord> ShiftClosures { get; set; } = [];
        public List<DayClosureRecord> DayClosures { get; set; } = [];
    }

    private sealed class ShiftClosureRecord
    {
        public string Id { get; set; } = string.Empty;
        public string ShiftLabel { get; set; } = string.Empty;
        public DateTime ShiftStartUtc { get; set; }
        public DateTime ShiftEndUtc { get; set; }
        public DateTime ClosedAtUtc { get; set; }
        public string ClosedBy { get; set; } = string.Empty;
        public decimal Revenue { get; set; }
        public int OrderCount { get; set; }
    }

    private sealed class DayClosureRecord
    {
        public string Id { get; set; } = string.Empty;
        public DateTime Day { get; set; }
        public DateTime ClosedAtUtc { get; set; }
        public string ClosedBy { get; set; } = string.Empty;
        public decimal Revenue { get; set; }
        public int OrderCount { get; set; }
    }

    private sealed class TableOrderRecord
    {
        public string Id { get; set; } = string.Empty;
        public string TableCode { get; set; } = string.Empty;
        public string Status { get; set; } = OrderStatus.Draft;
        public string? OrderCode { get; set; }
        public DateTime CreatedAtUtc { get; set; }
        public DateTime UpdatedAtUtc { get; set; }
        public DateTime? SubmittedAtUtc { get; set; }
        public DateTime? AcceptedAtUtc { get; set; }
        public DateTime? ReadyAtUtc { get; set; }
        public DateTime? ServedAtUtc { get; set; }
        public DateTime? PaidAtUtc { get; set; }
        public List<TableOrderItemRecord> Items { get; set; } = [];
    }

    private sealed class TableOrderItemRecord
    {
        public int ItemId { get; set; }
        public string ItemCode { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public decimal UnitPrice { get; set; }
        public int Quantity { get; set; }
        public string Note { get; set; } = string.Empty;
    }

    private sealed class TableChatMessageRecord
    {
        public string Id { get; set; } = string.Empty;
        public string TableCode { get; set; } = string.Empty;
        public string SenderName { get; set; } = string.Empty;
        public string SenderRole { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public string MessageType { get; set; } = string.Empty;
        public DateTime CreatedAtUtc { get; set; }
        public bool IsReadByStaff { get; set; }
        public bool IsReadByCustomer { get; set; }
    }

    private sealed class TablePaymentRequestRecord
    {
        public string Id { get; set; } = string.Empty;
        public string TableCode { get; set; } = string.Empty;
        public string OrderId { get; set; } = string.Empty;
        public string Method { get; set; } = string.Empty;
        public string Note { get; set; } = string.Empty;
        public string Status { get; set; } = PaymentStatuses.Pending;
        public decimal Amount { get; set; }
        public DateTime RequestedAtUtc { get; set; }
        public DateTime UpdatedAtUtc { get; set; }
    }

    private sealed class TableReviewRecord
    {
        public string Id { get; set; } = string.Empty;
        public string TableCode { get; set; } = string.Empty;
        public int FoodRating { get; set; }
        public int ServiceRating { get; set; }
        public string Comment { get; set; } = string.Empty;
        public DateTime CreatedAtUtc { get; set; }
    }

    private sealed class TableStateRecord
    {
        public string TableCode { get; set; } = string.Empty;
        public string State { get; set; } = "Bàn trống";
        public DateTime UpdatedAtUtc { get; set; }
        public string UpdatedBy { get; set; } = string.Empty;
    }

    private sealed record OrderLineRef(TableOrderRecord Order, TableOrderItemRecord Item, int ItemIndex);
}

file static class TablePaymentRequestRecordExtensions
{
    public static string StatusLabelOrDefault(this object paymentRecord)
    {
        var statusProperty = paymentRecord.GetType().GetProperty("Status");
        var status = statusProperty?.GetValue(paymentRecord)?.ToString() ?? string.Empty;
        return status switch
        {
            "Pending" => "Đang chờ xử lý",
            "Completed" => "Đã thu tiền",
            _ => "Khac"
        };
    }
}
