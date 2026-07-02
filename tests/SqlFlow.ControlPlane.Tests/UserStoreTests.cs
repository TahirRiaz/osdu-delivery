using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// The user/role store against the real catalog database: local creation with the uniqueness check, JIT
/// provisioning of external identities (including UPN renames and rename conflicts), the last-active-admin
/// lockout guards, and the local-only password reset. Gated on a reachable catalog database like the other
/// DB-backed suites; each test uses its own unique usernames and removes them.
/// </summary>
[Trait("Category", "Integration")]
public sealed class UserStoreTests
{
    [SkippableFact]
    public async Task CreateLocal_Roundtrips_AndRefusesADuplicateUsername()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var prefix = NewPrefix();
        var username = $"{prefix}-alice";

        try
        {
            await using var db = CatalogDatabase.Create(cs);
            await SeedRolesAsync(db);

            var (status, id) = await UserStore.CreateLocalAsync(
                db, username, "hash-value", RoleNames.Operator, "alice@example.test", "Alice", DateTime.UtcNow);
            Assert.Equal(UserCreateStatus.Created, status);

            var found = await UserStore.FindByUsernameAsync(db, username);
            Assert.NotNull(found);
            Assert.Equal(id, found.Id);
            Assert.Equal(UserProviders.Local, found.Provider);
            Assert.Equal(RoleNames.Operator, found.Role);
            Assert.Equal("alice@example.test", found.Email);
            Assert.True(found.Active);

            var (duplicate, _) = await UserStore.CreateLocalAsync(
                db, username.ToUpperInvariant(), "other-hash", RoleNames.Viewer, null, null, DateTime.UtcNow);
            Assert.Equal(UserCreateStatus.UsernameTaken, duplicate);

            var (unknownRole, _) = await UserStore.CreateLocalAsync(
                db, $"{prefix}-bob", "hash", "no-such-role", null, null, DateTime.UtcNow);
            Assert.Equal(UserCreateStatus.UnknownRole, unknownRole);
        }
        finally
        {
            await CleanupAsync(cs, prefix);
        }
    }

    [SkippableFact]
    public async Task EnsureExternal_ProvisionsOnFirstSight_ThenUpdatesInPlace()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var prefix = NewPrefix();
        var objectId = Guid.NewGuid().ToString("N");

        try
        {
            await using var db = CatalogDatabase.Create(cs);
            await SeedRolesAsync(db);

            var first = await UserStore.EnsureExternalAsync(
                db, new ExternalUserProfile(objectId, $"{prefix}@example.test", "Ali Akbar", $"{prefix}@example.test"),
                RoleNames.Viewer, DateTime.UtcNow);
            Assert.Equal(ExternalSignInStatus.Provisioned, first.Status);
            Assert.NotNull(first.User);
            Assert.Equal(UserProviders.Entra, first.User.Provider);
            Assert.Equal(RoleNames.Viewer, first.User.Role);
            Assert.Null(first.User.PasswordHash);

            // Second sign-in: same identity, refreshed profile, no duplicate row.
            var renamed = await UserStore.EnsureExternalAsync(
                db, new ExternalUserProfile(objectId, $"{prefix}-renamed@example.test", "Ali A.", null),
                RoleNames.Viewer, DateTime.UtcNow);
            Assert.Equal(ExternalSignInStatus.SignedIn, renamed.Status);
            Assert.Equal(first.User.Id, renamed.User!.Id);
            Assert.Equal($"{prefix}-renamed@example.test", renamed.User.Username);
            Assert.Equal("Ali A.", renamed.User.DisplayName);
            // The email was absent from the renamed token; the stored one is kept, not nulled.
            Assert.Equal($"{prefix}@example.test", renamed.User.Email);

            // A rename onto a sign-in name someone else owns keeps the old name and still signs in.
            var (taken, _) = await UserStore.CreateLocalAsync(
                db, $"{prefix}-local@example.test", "hash", RoleNames.Viewer, null, null, DateTime.UtcNow);
            Assert.Equal(UserCreateStatus.Created, taken);
            var conflicted = await UserStore.EnsureExternalAsync(
                db, new ExternalUserProfile(objectId, $"{prefix}-local@example.test", null, null),
                RoleNames.Viewer, DateTime.UtcNow);
            Assert.Equal(ExternalSignInStatus.SignedIn, conflicted.Status);
            Assert.Equal($"{prefix}-renamed@example.test", conflicted.User!.Username);

            // A brand-new external identity whose sign-in name is already owned is refused, never merged.
            var stranger = await UserStore.EnsureExternalAsync(
                db, new ExternalUserProfile(Guid.NewGuid().ToString("N"), $"{prefix}-local@example.test", null, null),
                RoleNames.Viewer, DateTime.UtcNow);
            Assert.Equal(ExternalSignInStatus.UsernameConflict, stranger.Status);

            // A deactivated external user cannot sign in.
            Assert.Equal(UserMutation.Applied, await UserStore.SetActiveAsync(db, first.User.Id, active: false, DateTime.UtcNow));
            var refused = await UserStore.EnsureExternalAsync(
                db, new ExternalUserProfile(objectId, $"{prefix}-renamed@example.test", null, null),
                RoleNames.Viewer, DateTime.UtcNow);
            Assert.Equal(ExternalSignInStatus.Inactive, refused.Status);
        }
        finally
        {
            await CleanupAsync(cs, prefix);
        }
    }

    [SkippableFact]
    public async Task LastActiveAdmin_CanNeitherBeDemotedNorDeactivated()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var prefix = NewPrefix();

        try
        {
            await using var db = CatalogDatabase.Create(cs);
            await SeedRolesAsync(db);

            // The shared test catalog may already hold admins from other suites; deactivate-guard semantics are
            // "last ACTIVE admin", so this test builds its own admin population and deactivates the rest first.
            var others = await db.Users.AsNoTracking()
                .Where(u => u.Active && u.Role == RoleNames.Admin).Select(u => u.Id).ToListAsync();

            var (s1, admin1) = await UserStore.CreateLocalAsync(db, $"{prefix}-admin1", "h", RoleNames.Admin, null, null, DateTime.UtcNow);
            var (s2, admin2) = await UserStore.CreateLocalAsync(db, $"{prefix}-admin2", "h", RoleNames.Admin, null, null, DateTime.UtcNow);
            Assert.Equal(UserCreateStatus.Created, s1);
            Assert.Equal(UserCreateStatus.Created, s2);
            foreach (var other in others)
            {
                Assert.Equal(UserMutation.Applied, await UserStore.SetActiveAsync(db, other, active: false, DateTime.UtcNow));
            }

            try
            {
                // With two active admins, demoting one is allowed.
                Assert.Equal(UserMutation.Applied, await UserStore.SetRoleAsync(db, admin2, RoleNames.Viewer, DateTime.UtcNow));

                // admin1 is now the last active admin: demotion and deactivation are both refused.
                Assert.Equal(UserMutation.LastAdmin, await UserStore.SetRoleAsync(db, admin1, RoleNames.Viewer, DateTime.UtcNow));
                Assert.Equal(UserMutation.LastAdmin, await UserStore.SetActiveAsync(db, admin1, active: false, DateTime.UtcNow));

                // Restoring a second admin unblocks both.
                Assert.Equal(UserMutation.Applied, await UserStore.SetRoleAsync(db, admin2, RoleNames.Admin, DateTime.UtcNow));
                Assert.Equal(UserMutation.Applied, await UserStore.SetActiveAsync(db, admin1, active: false, DateTime.UtcNow));
            }
            finally
            {
                foreach (var other in others)
                {
                    await UserStore.SetActiveAsync(db, other, active: true, DateTime.UtcNow);
                }
            }
        }
        finally
        {
            await CleanupAsync(cs, prefix);
        }
    }

    [SkippableFact]
    public async Task SetPasswordHash_IsRefusedForAnSsoUser()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var prefix = NewPrefix();

        try
        {
            await using var db = CatalogDatabase.Create(cs);
            await SeedRolesAsync(db);

            var (_, user) = await UserStore.EnsureExternalAsync(
                db, new ExternalUserProfile(Guid.NewGuid().ToString("N"), $"{prefix}-sso@example.test", null, null),
                RoleNames.Viewer, DateTime.UtcNow);
            Assert.NotNull(user);

            Assert.Equal(UserMutation.NotLocal, await UserStore.SetPasswordHashAsync(db, user.Id, "new-hash", DateTime.UtcNow));
            Assert.Equal(UserMutation.NotFound, await UserStore.SetPasswordHashAsync(db, Guid.NewGuid(), "new-hash", DateTime.UtcNow));
        }
        finally
        {
            await CleanupAsync(cs, prefix);
        }
    }

    [SkippableFact]
    public async Task EnsureRole_SeedsOnce_AndNeverOverwritesAnEditedRole()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var roleName = $"testrole-{Guid.NewGuid():N}";

        try
        {
            await using var db = CatalogDatabase.Create(cs);

            await UserStore.EnsureRoleAsync(db, roleName, "read", "A test role.", DateTime.UtcNow);
            var seeded = await UserStore.FindRoleAsync(db, roleName);
            Assert.NotNull(seeded);
            Assert.Equal("read", seeded.Scopes);

            // An operator edit survives re-seeding (seeding ensures existence, it does not manage drift).
            await db.Roles.Where(r => r.Name == roleName)
                .ExecuteUpdateAsync(r => r.SetProperty(x => x.Scopes, "read operate"));
            await UserStore.EnsureRoleAsync(db, roleName, "read", "A test role.", DateTime.UtcNow);
            var kept = await UserStore.FindRoleAsync(db, roleName);
            Assert.Equal("read operate", kept!.Scopes);
        }
        finally
        {
            await using var db = CatalogDatabase.Create(cs);
            await db.Roles.Where(r => r.Name == roleName).ExecuteDeleteAsync();
        }
    }

    private static async Task SeedRolesAsync(CatalogDbContext db)
    {
        var now = DateTime.UtcNow;
        await UserStore.EnsureRoleAsync(db, RoleNames.Admin, "read operate admin", "Full control.", now);
        await UserStore.EnsureRoleAsync(db, RoleNames.Operator, "read operate", "Operations.", now);
        await UserStore.EnsureRoleAsync(db, RoleNames.Viewer, "read", "Read-only.", now);
    }

    private static string NewPrefix() => $"user-test-{Guid.NewGuid():N}";

    private static async Task CleanupAsync(string cs, string prefix)
    {
        await using var db = CatalogDatabase.Create(cs);
        await db.Users.Where(u => u.Username.StartsWith(prefix)).ExecuteDeleteAsync();
    }
}
