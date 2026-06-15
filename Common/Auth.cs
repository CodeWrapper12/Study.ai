using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Accelerator.Api.Domain;
using Microsoft.IdentityModel.Tokens;

namespace Accelerator.Api.Common;

public class TokenService(IConfiguration config)
{
    public const int AccessMinutes = 30;
    public const int RefreshDays = 30;

    public string CreateAccessToken(User user)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(
            config["Jwt:Key"] ?? "dev-only-key-change-me-please-0123456789"));
        var token = new JwtSecurityToken(
            claims:
            [
                new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
                new Claim(ClaimTypes.Email, user.Email),
                new Claim(ClaimTypes.Name, user.DisplayName),
                new Claim(ClaimTypes.Role, user.Role)
            ],
            expires: DateTime.UtcNow.AddMinutes(AccessMinutes),
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public RefreshToken CreateRefreshToken(Guid userId) => new()
    {
        UserId = userId,
        Token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48)),
        ExpiresAt = DateTime.UtcNow.AddDays(RefreshDays)
    };
}

public static class CurrentUser
{
    public static Guid Id(ClaimsPrincipal p) =>
        Guid.Parse(p.FindFirstValue(ClaimTypes.NameIdentifier)!);

    public static Guid? IdOrNull(ClaimsPrincipal p) =>
        p.Identity?.IsAuthenticated == true
            ? Guid.Parse(p.FindFirstValue(ClaimTypes.NameIdentifier)!)
            : null;

    public static bool IsStaff(ClaimsPrincipal p) =>
        p.IsInRole("Admin") || p.IsInRole("Instructor");
}
