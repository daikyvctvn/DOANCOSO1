using System.Diagnostics;
using System.Globalization;
using System.Text;
using TableOrderWeb.Models;

namespace TableOrderWeb.Services;

public sealed class SqlCmdMenuAdminService : IMenuAdminService
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<SqlCmdMenuAdminService> _logger;

    public SqlCmdMenuAdminService(IConfiguration configuration, ILogger<SqlCmdMenuAdminService> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<AdminMenuPageViewModel> GetAdminMenuPageAsync(int? editItemId = null, CancellationToken cancellationToken = default)
    {
        var model = new AdminMenuPageViewModel();

        try
        {
            var categories = await ReadCategoriesAsync(cancellationToken);
            var menuItems = await ReadMenuItemsAsync(cancellationToken);

            model.Categories = categories;
            model.MenuItems = menuItems;
            model.Form = BuildForm(menuItems.FirstOrDefault(x => x.ItemId == editItemId));

            if (editItemId.HasValue && model.Form.ItemId is null)
            {
                model.ErrorMessage = "Khong tim thay mon can sua.";
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Khong the tai trang quan ly menu cho admin.");
            model.ErrorMessage = $"Khong doc duoc du lieu menu SQL. Chi tiet: {ex.Message}";
        }

        return model;
    }

    public async Task<MenuAdminOperationResult> SaveMenuItemAsync(AdminMenuFormViewModel form, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!await CategoryExistsAsync(form.CategoryId, cancellationToken))
            {
                return new MenuAdminOperationResult { ErrorMessage = "Danh muc mon an khong ton tai." };
            }

            var itemCode = form.ItemCode.Trim().ToUpperInvariant();
            if (await ItemCodeExistsAsync(itemCode, form.ItemId, cancellationToken))
            {
                return new MenuAdminOperationResult { ErrorMessage = "Ma mon da ton tai." };
            }

            var sql = form.ItemId.HasValue
                ? BuildUpdateSql(form, itemCode)
                : BuildInsertSql(form, itemCode);

            var output = await ExecuteSqlAsync(sql, cancellationToken);
            var itemId = ParseScalarInt(output);

            if (itemId <= 0)
            {
                return new MenuAdminOperationResult { ErrorMessage = "Khong luu duoc mon an vao SQL Server." };
            }

            return new MenuAdminOperationResult
            {
                Succeeded = true,
                ItemId = itemId
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Khong the luu mon an tu form admin.");
            return new MenuAdminOperationResult { ErrorMessage = ex.Message };
        }
    }

    public async Task<MenuAdminOperationResult> DeleteMenuItemAsync(int itemId, CancellationToken cancellationToken = default)
    {
        try
        {
            var sql = $@"SET NOCOUNT ON;
DELETE FROM dbo.MenuOptionChoice
WHERE OptionGroupId IN (SELECT OptionGroupId FROM dbo.MenuOptionGroup WHERE ItemId = {itemId});
DELETE FROM dbo.MenuOptionGroup WHERE ItemId = {itemId};
DELETE FROM dbo.MenuItem WHERE ItemId = {itemId};
SELECT @@ROWCOUNT;";

            var output = await ExecuteSqlAsync(sql, cancellationToken);
            var deleted = ParseScalarInt(output);

            return deleted > 0
                ? new MenuAdminOperationResult { Succeeded = true, ItemId = itemId }
                : new MenuAdminOperationResult { ErrorMessage = "Khong tim thay mon an de xoa." };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Khong the xoa mon an ItemId={ItemId}", itemId);
            return new MenuAdminOperationResult { ErrorMessage = ex.Message };
        }
    }

    private async Task<List<MenuCategoryViewModel>> ReadCategoriesAsync(CancellationToken cancellationToken)
    {
        const string sql = @"SET NOCOUNT ON;
SELECT CategoryId, CategoryCode, REPLACE(CategoryName, '|', '/'), DisplayOrder
FROM dbo.MenuCategory
WHERE IsActive = 1
ORDER BY DisplayOrder, CategoryName;";

        var output = await ExecuteSqlAsync(sql, cancellationToken);
        var rows = ParseRows(output, 4);

        return rows.Select(parts => new MenuCategoryViewModel
        {
            CategoryId = int.Parse(parts[0], CultureInfo.InvariantCulture),
            CategoryCode = parts[1],
            CategoryName = parts[2],
            DisplayOrder = int.Parse(parts[3], CultureInfo.InvariantCulture)
        }).ToList();
    }

    private async Task<List<AdminMenuItemViewModel>> ReadMenuItemsAsync(CancellationToken cancellationToken)
    {
        const string sql = @"SET NOCOUNT ON;
SELECT
    i.ItemId,
    i.CategoryId,
    REPLACE(c.CategoryName, '|', '/') AS CategoryName,
    i.ItemCode,
    REPLACE(i.ItemName, '|', '/') AS ItemName,
    REPLACE(ISNULL(i.Description, N''), '|', '/') AS Description,
    i.Price,
    REPLACE(ISNULL(i.ImageUrl, N''), '|', '/') AS ImageUrl,
    i.PreparationTimeMinutes,
    CASE WHEN i.IsBestSeller = 1 THEN 1 ELSE 0 END AS IsBestSeller,
    CASE WHEN i.IsAvailable = 1 THEN 1 ELSE 0 END AS IsAvailable,
    i.PopularityScore
FROM dbo.MenuItem AS i
INNER JOIN dbo.MenuCategory AS c ON c.CategoryId = i.CategoryId
ORDER BY c.DisplayOrder, i.PopularityScore DESC, i.ItemName;";

        var output = await ExecuteSqlAsync(sql, cancellationToken);
        var rows = ParseRows(output, 12);

        return rows.Select(parts => new AdminMenuItemViewModel
        {
            ItemId = int.Parse(parts[0], CultureInfo.InvariantCulture),
            CategoryId = int.Parse(parts[1], CultureInfo.InvariantCulture),
            CategoryName = parts[2],
            ItemCode = parts[3],
            Name = parts[4],
            Description = parts[5],
            Price = decimal.Parse(parts[6], CultureInfo.InvariantCulture),
            ImageUrl = parts[7],
            PreparationTimeMinutes = int.Parse(parts[8], CultureInfo.InvariantCulture),
            IsBestSeller = parts[9] == "1",
            IsAvailable = parts[10] == "1",
            PopularityScore = int.Parse(parts[11], CultureInfo.InvariantCulture)
        }).ToList();
    }

    private async Task<bool> CategoryExistsAsync(int categoryId, CancellationToken cancellationToken)
    {
        var output = await ExecuteSqlAsync($"SET NOCOUNT ON; SELECT COUNT(1) FROM dbo.MenuCategory WHERE CategoryId = {categoryId} AND IsActive = 1;", cancellationToken);
        return ParseScalarInt(output) > 0;
    }

    private async Task<bool> ItemCodeExistsAsync(string itemCode, int? itemId, CancellationToken cancellationToken)
    {
        var whereId = itemId.HasValue ? $" AND ItemId <> {itemId.Value}" : string.Empty;
        var output = await ExecuteSqlAsync($"SET NOCOUNT ON; SELECT COUNT(1) FROM dbo.MenuItem WHERE ItemCode = {SqlVarchar(itemCode)}{whereId};", cancellationToken);
        return ParseScalarInt(output) > 0;
    }

    private string BuildInsertSql(AdminMenuFormViewModel form, string itemCode)
    {
        return $@"SET NOCOUNT ON;
INSERT INTO dbo.MenuItem
(
    CategoryId,
    ItemCode,
    ItemName,
    Description,
    Price,
    ImageUrl,
    PreparationTimeMinutes,
    IsBestSeller,
    IsAvailable,
    PopularityScore
)
VALUES
(
    {form.CategoryId},
    {SqlVarchar(itemCode)},
    {SqlUnicode(form.Name)},
    {SqlNullableUnicode(form.Description)},
    {form.Price.ToString(CultureInfo.InvariantCulture)},
    {SqlNullableUnicode(form.ImageUrl)},
    {form.PreparationTimeMinutes},
    {(form.IsBestSeller ? 1 : 0)},
    {(form.IsAvailable ? 1 : 0)},
    {form.PopularityScore}
);
SELECT CAST(SCOPE_IDENTITY() AS INT);";
    }

    private string BuildUpdateSql(AdminMenuFormViewModel form, string itemCode)
    {
        return $@"SET NOCOUNT ON;
UPDATE dbo.MenuItem
SET
    CategoryId = {form.CategoryId},
    ItemCode = {SqlVarchar(itemCode)},
    ItemName = {SqlUnicode(form.Name)},
    Description = {SqlNullableUnicode(form.Description)},
    Price = {form.Price.ToString(CultureInfo.InvariantCulture)},
    ImageUrl = {SqlNullableUnicode(form.ImageUrl)},
    PreparationTimeMinutes = {form.PreparationTimeMinutes},
    IsBestSeller = {(form.IsBestSeller ? 1 : 0)},
    IsAvailable = {(form.IsAvailable ? 1 : 0)},
    PopularityScore = {form.PopularityScore}
WHERE ItemId = {form.ItemId!.Value};
SELECT {form.ItemId!.Value};";
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

        var tempQueryFile = Path.Combine(Path.GetTempPath(), $"menu-admin-query-{Guid.NewGuid():N}.sql");
        var tempOutputFile = Path.Combine(Path.GetTempPath(), $"menu-admin-output-{Guid.NewGuid():N}.txt");

        try
        {
            await File.WriteAllTextAsync(tempQueryFile, sql, Encoding.UTF8, cancellationToken);

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

    private static List<string[]> ParseRows(string output, int expectedColumns)
    {
        var rows = new List<string[]>();
        var lines = output.Replace("\r", string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries);

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim().TrimStart('\uFEFF');
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var parts = line.Split('|').Select(static x => x.Trim()).ToArray();
            if (parts.Length < expectedColumns)
            {
                continue;
            }

            rows.Add(parts);
        }

        return rows;
    }

    private static int ParseScalarInt(string output)
    {
        var line = output.Replace("\r", string.Empty)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(static x => x.Trim().TrimStart('\uFEFF'))
            .FirstOrDefault(static x => int.TryParse(x, NumberStyles.Integer, CultureInfo.InvariantCulture, out _));

        return int.TryParse(line, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0;
    }

    private static AdminMenuFormViewModel BuildForm(AdminMenuItemViewModel? item)
    {
        if (item is null)
        {
            return new AdminMenuFormViewModel();
        }

        return new AdminMenuFormViewModel
        {
            ItemId = item.ItemId,
            CategoryId = item.CategoryId,
            ItemCode = item.ItemCode,
            Name = item.Name,
            Description = item.Description,
            Price = item.Price,
            ImageUrl = item.ImageUrl,
            PreparationTimeMinutes = item.PreparationTimeMinutes,
            PopularityScore = item.PopularityScore,
            IsBestSeller = item.IsBestSeller,
            IsAvailable = item.IsAvailable
        };
    }

    private static string SqlVarchar(string value) => $"'{EscapeSql(value)}'";

    private static string SqlUnicode(string value) => $"N'{EscapeSql(value)}'";

    private static string SqlNullableUnicode(string? value)
        => string.IsNullOrWhiteSpace(value) ? "NULL" : $"N'{EscapeSql(value.Trim())}'";

    private static string EscapeSql(string value) => value.Trim().Replace("'", "''");

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
}
