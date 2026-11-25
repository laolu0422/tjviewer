using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;

namespace TjViewer.Server;

public static class ClaimsHelper
{
    public static string? UserId(ClaimsPrincipal cp)
        => cp.FindFirstValue(JwtRegisteredClaimNames.Sub) ?? cp.FindFirstValue(ClaimTypes.NameIdentifier);
}
