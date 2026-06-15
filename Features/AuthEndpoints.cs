using System.Security.Claims;
using Accelerator.Api.Common;
using Accelerator.Api.Data;
using Accelerator.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace Accelerator.Api.Features;

public static class AuthEndpoints
{
    public record RegisterRequest(string Email, string Password, string DisplayName);
    public record LoginRequest(string Email, string Password);
    public record RefreshRequest(string RefreshToken);

    public static void MapAuthEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("/auth");

        g.MapPost("/register", async (RegisterRequest req, AppDbContext db, TokenService tokens) =>
        {
            var email = req.Email.Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(email) || !email.Contains('@'))
                return Results.BadRequest(new { error = "A valid email is required." });
            if (req.Password.Length < 8)
                return Results.BadRequest(new { error = "Password must be at least 8 characters." });
            if (await db.Users.AnyAsync(u => u.Email == email))
                return Results.Conflict(new { error = "An account with this email already exists." });

            var user = new User
            {
                Email = email,
                DisplayName = string.IsNullOrWhiteSpace(req.DisplayName) ? email.Split('@')[0] : req.DisplayName.Trim(),
                PasswordHash = BCrypt.Net.BCrypt.HashPassword(req.Password)
            };
            db.Users.Add(user);
            var refresh = tokens.CreateRefreshToken(user.Id);
            db.RefreshTokens.Add(refresh);
            await db.SaveChangesAsync();

            return Results.Ok(Session(tokens, user, refresh));
        });

        g.MapPost("/login", async (LoginRequest req, AppDbContext db, TokenService tokens) =>
        {
            var email = req.Email.Trim().ToLowerInvariant();
            var user = await db.Users.FirstOrDefaultAsync(u => u.Email == email);
            if (user is null || !BCrypt.Net.BCrypt.Verify(req.Password, user.PasswordHash))
                return Results.Json(new { error = "Invalid email or password." }, statusCode: 401);

            var refresh = tokens.CreateRefreshToken(user.Id);
            db.RefreshTokens.Add(refresh);
            await db.SaveChangesAsync();

            return Results.Ok(Session(tokens, user, refresh));
        });

        g.MapPost("/refresh", async (RefreshRequest req, AppDbContext db, TokenService tokens) =>
        {
            var stored = await db.RefreshTokens.FirstOrDefaultAsync(t => t.Token == req.RefreshToken);
            if (stored is null || !stored.IsActive)
                return Results.Json(new { error = "Refresh token is invalid or expired." }, statusCode: 401);

            var user = await db.Users.FindAsync(stored.UserId);
            if (user is null) return Results.Json(new { error = "User not found." }, statusCode: 401);

            // Rotate: revoke old, issue new
            var next = tokens.CreateRefreshToken(user.Id);
            stored.RevokedAt = DateTime.UtcNow;
            stored.ReplacedByToken = next.Token;
            db.RefreshTokens.Add(next);
            await db.SaveChangesAsync();

            return Results.Ok(Session(tokens, user, next));
        });

        g.MapPost("/logout", async (RefreshRequest req, AppDbContext db) =>
        {
            var stored = await db.RefreshTokens.FirstOrDefaultAsync(t => t.Token == req.RefreshToken);
            if (stored is not null && stored.IsActive)
            {
                stored.RevokedAt = DateTime.UtcNow;
                await db.SaveChangesAsync();
            }
            return Results.Ok();
        });

        g.MapGet("/me", async (ClaimsPrincipal principal, AppDbContext db) =>
        {
            var user = await db.Users.FindAsync(CurrentUser.Id(principal));
            return user is null
                ? Results.NotFound()
                : Results.Ok(new { user.Id, user.Email, user.DisplayName, user.Role, user.Xp, user.Headline });
        }).RequireAuthorization();
    }

    private static object Session(TokenService tokens, User user, RefreshToken refresh) => new
    {
        accessToken = tokens.CreateAccessToken(user),
        refreshToken = refresh.Token,
        user = new { user.Id, user.Email, user.DisplayName, user.Role }
    };
}
