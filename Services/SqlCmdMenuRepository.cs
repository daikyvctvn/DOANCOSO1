using System.Diagnostics;
using System.Text;
using TableOrderWeb.Models;

namespace TableOrderWeb.Services;

public sealed class SqlCmdMenuRepository : IMenuRepository
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<SqlCmdMenuRepository> _logger;

    public SqlCmdMenuRepository(IConfiguration configuration, ILogger<SqlCmdMenuRepository> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<CustomerPageViewModel> GetCustomerMenuAsync(CancellationToken cancellationToken = default)
    {
        var model = new CustomerPageViewModel();
        var sqlCmdPath = _configuration["SqlMenu:SqlCmdPath"];
        var server = _configuration["SqlMenu:Server"] ?? "localhost";
        var database = _configuration["SqlMenu:Database"] ?? "TableOrderDb";

        if (string.IsNullOrWhiteSpace(sqlCmdPath) || !File.Exists(sqlCmdPath))
        {
            model.ErrorMessage = "Khong tim thay sqlcmd de doc menu SQL.";
            return model;
        }

        const string query = @"SET NOCOUNT ON;
SELECT
    c.CategoryId,
    c.CategoryCode,
    c.CategoryName,
    c.DisplayOrder,
    i.ItemId,
    i.ItemCode,
    REPLACE(i.ItemName, '|', '/') AS ItemName,
    REPLACE(ISNULL(i.Description, N''), '|', '/') AS Description,
    REPLACE(ISNULL(i.ImageUrl, N''), '|', '/') AS ImageUrl,
    i.Price,
    i.PreparationTimeMinutes,
    CASE WHEN i.IsBestSeller = 1 THEN 1 ELSE 0 END AS IsBestSeller,
    i.PopularityScore
FROM dbo.MenuCategory AS c
INNER JOIN dbo.MenuItem AS i ON i.CategoryId = c.CategoryId
WHERE c.IsActive = 1 AND i.IsAvailable = 1
ORDER BY c.DisplayOrder, i.PopularityScore DESC, i.ItemName;";

        var tempQueryFile = Path.Combine(Path.GetTempPath(), $"menu-query-{Guid.NewGuid():N}.sql");
        var tempOutputFile = Path.Combine(Path.GetTempPath(), $"menu-output-{Guid.NewGuid():N}.txt");

        try
        {
            await File.WriteAllTextAsync(tempQueryFile, query, Encoding.UTF8, cancellationToken);

            var psi = new ProcessStartInfo
            {
                FileName = sqlCmdPath,
                Arguments = $"-S {server} -d {database} -E -u -h -1 -s \"|\" -w 65535 -y 8000 -Y 8000 -i \"{tempQueryFile}\" -o \"{tempOutputFile}\"",
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
                _logger.LogError("sqlcmd tra ve ma loi {ExitCode}: {Error}", process.ExitCode, error);
                model.ErrorMessage = "Khong doc duoc menu tu SQL Server.";
                return model;
            }

            var rows = ParseRows(output);
            if (rows.Count == 0)
            {
                model.ErrorMessage = "Khong co mon nao duoc doc tu SQL.";
                return model;
            }

            model.Dishes = rows.Select(MapDish).ToList();
            model.Categories = rows
                .GroupBy(x => new { x.CategoryId, x.CategoryCode, x.CategoryName, x.DisplayOrder })
                .Select(x => new MenuCategoryViewModel
                {
                    CategoryId = x.Key.CategoryId,
                    CategoryCode = x.Key.CategoryCode,
                    CategoryName = x.Key.CategoryName,
                    DisplayOrder = x.Key.DisplayOrder
                })
                .OrderBy(x => x.DisplayOrder)
                .ToList();

            return model;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Khong the doc menu tu SQL Server qua sqlcmd.");
            model.ErrorMessage = $"Khong ket noi duoc menu SQL. Chi tiet: {ex.Message}";
            return model;
        }
        finally
        {
            TryDelete(tempQueryFile);
            TryDelete(tempOutputFile);
        }
    }

    private static List<MenuDishSqlRow> ParseRows(string output)
    {
        var rows = new List<MenuDishSqlRow>();
        var lines = output.Replace("\r", string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries);

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim().TrimStart('\uFEFF');
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var parts = line.Split('|').Select(static x => x.Trim()).ToArray();
            if (parts.Length < 13)
            {
                continue;
            }

            rows.Add(new MenuDishSqlRow
            {
                CategoryId = int.Parse(parts[0]),
                CategoryCode = parts[1],
                CategoryName = parts[2],
                DisplayOrder = int.Parse(parts[3]),
                ItemId = int.Parse(parts[4]),
                ItemCode = parts[5],
                Name = parts[6],
                Description = parts[7],
                ImageUrl = parts[8],
                Price = decimal.Parse(parts[9]),
                PreparationTimeMinutes = int.Parse(parts[10]),
                IsBestSeller = parts[11] == "1",
                PopularityScore = int.Parse(parts[12])
            });
        }

        return rows;
    }

    private static MenuDishViewModel MapDish(MenuDishSqlRow x)
    {
        return new MenuDishViewModel
        {
            CategoryId = x.CategoryId,
            CategoryCode = x.CategoryCode,
            CategoryName = x.CategoryName,
            ItemId = x.ItemId,
            ItemCode = x.ItemCode,
            Name = x.Name,
            Description = x.Description,
            ImageUrl = x.ImageUrl?.Trim() ?? string.Empty,
            IngredientSummary = ResolveIngredientSummary(x.ItemCode, x.CategoryCode),
            ServingSuggestion = ResolveServingSuggestion(x.ItemCode, x.CategoryCode),
            CustomizationSummary = ResolveCustomizationSummary(x.ItemCode, x.CategoryCode),
            Price = x.Price,
            PreparationTimeMinutes = x.PreparationTimeMinutes,
            IsBestSeller = x.IsBestSeller,
            PopularityScore = x.PopularityScore,
            Accent = MapAccent(x.CategoryCode)
        };
    }



    private static string ResolveIngredientSummary(string itemCode, string categoryCode)
    {
        return itemCode switch
        {
            "MC001" => "Tôm tươi, nấm kim châm, nấm đùi gà, rau thập cẩm và nước lẩu Thái chua cay.",
            "MC002" => "Bò Mỹ cắt lát dày, tiêu đen, bơ lạt, rau củ nướng và sốt demi tiêu.",
            "DU001" => "Trà nhài ủ lạnh, đào ngâm, lát cam vàng và sả cây đập dập.",
            "TM001" => "Trứng gà, sữa tươi, vani Madagascar và caramel nấu tay.",
            _ when categoryCode == "DOUONG" => "Nguyên liệu pha chế theo công thức chuẩn quầy bar, phục vụ lạnh ngay sau khi làm.",
            _ when categoryCode == "TRANGMIENG" => "Mẻ tráng miệng làm mới trong ngày, ưu tiên vị nhẹ và kết cấu mềm mịn.",
            _ => "Nguyên liệu được chuẩn bị trong ngày, cân bằng giữa hương vị chính và phần ăn chia nhóm."
        };
    }

    private static string ResolveServingSuggestion(string itemCode, string categoryCode)
    {
        return itemCode switch
        {
            "MC001" => "Phù hợp 3-4 người, ngon hơn khi gọi thêm mì tươi hoặc bò viên phô mai.",
            "MC002" => "Dùng nóng tại bàn, hợp vị khi ăn cùng khoai lắc hoặc salad trộn chua nhẹ.",
            "DU001" => "Phục vụ lạnh, hợp đi kèm món nướng hoặc món cay để cân vị.",
            "TM001" => "Nên dùng sau món chính khoảng 5-10 phút để cảm nhận rõ caramel và độ béo.",
            _ when categoryCode == "KHAIVI" => "Nên gọi mở bàn cho 2-4 người để chia phần dễ hơn.",
            _ when categoryCode == "MONCHINH" => "Ưu tiên phục vụ ngay khi hoàn thành để giữ nhiệt và kết cấu món.",
            _ => "Phù hợp gọi kèm trong combo hoặc thêm vào cuối bữa để cân trải nghiệm vị giác."
        };
    }

    private static string ResolveCustomizationSummary(string itemCode, string categoryCode)
    {
        return itemCode switch
        {
            "MC001" => "Tùy chọn mức cay, thêm nấm, thêm mì và ghi chú bếp cho khẩu vị từng bàn.",
            "MC002" => "Có thể ghi chú không hành, ít tiêu, nướng chín vừa hoặc chín kỹ.",
            "DU001" => "Chọn mức đường, mức đá và topping như thạch đào, hạt chia hoặc milk foam.",
            _ when categoryCode == "DOUONG" => "Cho phép điều chỉnh đường, đá và thêm ghi chú riêng cho quầy bar.",
            _ => "Hỗ trợ ghi chú đặc biệt như ít cay, không hành, tách sốt hoặc đổi kiểu phục vụ."
        };
    }

    private static string MapAccent(string categoryCode)
    {
        return categoryCode switch
        {
            "MONCHINH" => "red",
            "DOUONG" => "gold",
            "TRANGMIENG" => "green",
            "KHAIVI" => "warm",
            _ => "warm"
        };
    }

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

    private sealed class MenuDishSqlRow
    {
        public int CategoryId { get; set; }
        public string CategoryCode { get; set; } = string.Empty;
        public string CategoryName { get; set; } = string.Empty;
        public int DisplayOrder { get; set; }
        public int ItemId { get; set; }
        public string ItemCode { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string ImageUrl { get; set; } = string.Empty;
        public decimal Price { get; set; }
        public int PreparationTimeMinutes { get; set; }
        public bool IsBestSeller { get; set; }
        public int PopularityScore { get; set; }
    }
}



