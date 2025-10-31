using Microsoft.EntityFrameworkCore;

namespace StorageBenchmarks.Ef;

public sealed class BenchDbContext : DbContext
{
    public BenchDbContext(DbContextOptions<BenchDbContext> options) : base(options)
    {
    }

    public DbSet<PersonEntity> Persons => Set<PersonEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var e = modelBuilder.Entity<PersonEntity>();
        e.ToTable("Persons", schema: "dbo");
        e.HasKey(x => x.Id);
        e.Property(x => x.Name).HasMaxLength(200).IsRequired();
        e.Property(x => x.Age).IsRequired();
        e.Property(x => x.Role).HasMaxLength(100).IsRequired();
        e.Property(x => x.Surname).HasMaxLength(100);
    }
}

public static class Ef
{
    public static DbContextOptions<BenchDbContext> CreateOptions(string connectionString)
    {
        var builder = new DbContextOptionsBuilder<BenchDbContext>();
        builder.UseSqlServer(connectionString, sql =>
        {
            sql.EnableRetryOnFailure();
            sql.CommandTimeout(300);
        });
        return builder.Options;
    }
}

public sealed class PersonEntity
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int Age { get; set; }
    public string Role { get; set; } = string.Empty;
    public string? Surname { get; set; }
}
