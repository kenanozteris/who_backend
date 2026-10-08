using Microsoft.AspNetCore.Identity;

namespace Who.Infrastructure.Identity;

// Auth identity only. WHO social identity and private birth date belong to Domain.
public sealed class ApplicationUser : IdentityUser<Guid> { }
