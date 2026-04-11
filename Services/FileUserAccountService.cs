using System.Security.Cryptography;
using System.Text.Json;
using TableOrderWeb.Models;

namespace TableOrderWeb.Services;

public sealed class FileUserAccountService : IUserAccountService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _filePath;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public FileUserAccountService(IWebHostEnvironment environment)
    {
        var directory = Path.Combine(environment.ContentRootPath, "data");
        Directory.CreateDirectory(directory);
        _filePath = Path.Combine(directory, "users.json");
    }

    public async Task<(bool Succeeded, string? ErrorMessage)> RegisterAsync(RegisterViewModel model, CancellationToken cancellationToken = default)
    {
        var role = NormalizeRole(model.Role);
        if (role is null)
        {
            return (false, "Vai tro khong hop le.");
        }

        var normalizedUserName = NormalizeUserName(model.UserName);
        if (string.IsNullOrWhiteSpace(normalizedUserName))
        {
            return (false, "Ten dang nhap khong hop le.");
        }

        await _lock.WaitAsync(cancellationToken);
        try
        {
            var users = await ReadUsersAsync(cancellationToken);
            if (users.Any(x => string.Equals(x.UserName, normalizedUserName, StringComparison.OrdinalIgnoreCase)))
            {
                return (false, "Ten dang nhap da ton tai.");
            }

            if (role == "Admin" && users.Any(x => string.Equals(x.Role, "Admin", StringComparison.OrdinalIgnoreCase)))
            {
                return (false, "He thong chi cho phep duy nhat 1 tai khoan Quan tri.");
            }

            CreatePasswordHash(model.Password, out var hash, out var salt);
            users.Add(new AppUser
            {
                UserName = normalizedUserName,
                DisplayName = model.DisplayName.Trim(),
                Role = role,
                PasswordHash = hash,
                PasswordSalt = salt,
                CreatedAtUtc = DateTime.UtcNow
            });

            await WriteUsersAsync(users, cancellationToken);
            return (true, null);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<AppUser?> ValidateLoginAsync(string userName, string password, CancellationToken cancellationToken = default)
    {
        var normalizedUserName = NormalizeUserName(userName);
        await _lock.WaitAsync(cancellationToken);
        try
        {
            var users = await ReadUsersAsync(cancellationToken);
            var user = users.FirstOrDefault(x => string.Equals(x.UserName, normalizedUserName, StringComparison.OrdinalIgnoreCase));
            if (user is null)
            {
                return null;
            }

            return VerifyPassword(password, user.PasswordHash, user.PasswordSalt) ? user : null;
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<List<AppUser>> ReadUsersAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_filePath))
        {
            return new List<AppUser>();
        }

        await using var stream = File.OpenRead(_filePath);
        return await JsonSerializer.DeserializeAsync<List<AppUser>>(stream, JsonOptions, cancellationToken) ?? new List<AppUser>();
    }

    private async Task WriteUsersAsync(List<AppUser> users, CancellationToken cancellationToken)
    {
        await using var stream = File.Create(_filePath);
        await JsonSerializer.SerializeAsync(stream, users, JsonOptions, cancellationToken);
    }

    private static string NormalizeUserName(string value) => value.Trim();

    private static string? NormalizeRole(string value)
    {
        if (value.Equals("Admin", StringComparison.OrdinalIgnoreCase)) return "Admin";
        if (value.Equals("Staff", StringComparison.OrdinalIgnoreCase)) return "Staff";
        return null;
    }

    private static void CreatePasswordHash(string password, out string hash, out string salt)
    {
        var saltBytes = RandomNumberGenerator.GetBytes(16);
        var hashBytes = Rfc2898DeriveBytes.Pbkdf2(password, saltBytes, 100_000, HashAlgorithmName.SHA256, 32);
        salt = Convert.ToBase64String(saltBytes);
        hash = Convert.ToBase64String(hashBytes);
    }

    private static bool VerifyPassword(string password, string storedHash, string storedSalt)
    {
        var saltBytes = Convert.FromBase64String(storedSalt);
        var computedHash = Rfc2898DeriveBytes.Pbkdf2(password, saltBytes, 100_000, HashAlgorithmName.SHA256, 32);
        return CryptographicOperations.FixedTimeEquals(computedHash, Convert.FromBase64String(storedHash));
    }
}
