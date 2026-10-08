using Microsoft.AspNetCore.Identity;
using Who.Infrastructure.Identity;

namespace Who.Infrastructure.Authentication;

public sealed class PasswordLengthValidator : IPasswordValidator<ApplicationUser>
{
    public Task<IdentityResult> ValidateAsync(UserManager<ApplicationUser> manager, ApplicationUser user, string? password) =>
        Task.FromResult(password is { Length: >= 10 and <= 128 } ? IdentityResult.Success :
            IdentityResult.Failed(new IdentityError { Code = "PasswordInvalid", Description = "Password does not meet policy." }));
}
