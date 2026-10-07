using Microsoft.EntityFrameworkCore;

namespace Who.Infrastructure.Persistence;

// No business/auth entities or empty migration in the foundation milestone.
public sealed class WhoDbContext(DbContextOptions<WhoDbContext> options) : DbContext(options);
