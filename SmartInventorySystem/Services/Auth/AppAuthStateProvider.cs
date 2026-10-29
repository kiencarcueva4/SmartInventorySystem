using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Components.Authorization;

namespace SmartInventorySystem.Services.Auth
{
    /// <summary>The signed-in user, in the form Blazor's [Authorize] system understands.</summary>
    public sealed class AppAuthStateProvider : AuthenticationStateProvider
    {
        private static readonly ClaimsPrincipal Anonymous = new(new ClaimsIdentity());
        private ClaimsPrincipal _user = Anonymous;

        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(_user));

        public void SignIn(CurrentUser user)
        {
            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, user.UserId.ToString()),
                new(ClaimTypes.Name, user.FullName),
                new("username", user.Username),
                new(ClaimTypes.Role, user.RoleName),
            };
            claims.AddRange(Permissions.ForRole(user.RoleName).Select(p => new Claim(Permissions.ClaimType, p)));

            _user = new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "SmartInventory"));
            NotifyAuthenticationStateChanged(GetAuthenticationStateAsync());
        }

        public void SignOut()
        {
            _user = Anonymous;
            NotifyAuthenticationStateChanged(GetAuthenticationStateAsync());
        }
    }
}
