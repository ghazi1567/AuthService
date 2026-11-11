using System.Text;
using AuthService.Api.Features.Cleanup;
using AuthService.Api.Observability;
using AuthService.Infrastructure.Persistence;
using AuthService.Infrastructure.Persistence.Entities;
using AuthService.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace AuthService.IntegrationTests.Cln01;

/// <summary>
/// AUTH-270 (FIX-05) The cleanup removes finished rows once their retention has passed, and keeps everything recent or still usable:
/// refresh, verification and reset tokens, sent and failed emails, delivered and failed webhook events, expired product secrets. Never the
/// audit log. Each test plants rows with known times and runs the cleanup itself at a chosen "now".
/// </summary>
public sealed class DataCleanupTests(PostgresContainerFixture postgres) : AuthApiTestBase(postgres)
{
    private static readonly DateTimeOffset Now = new(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task AUTH270_RefreshTokensGoAWeekAfterExpiringAndARevokedOneStaysUntilThenForReuseDetection()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = (await SignUpVerifiedAsync("Acme Ltd", "owner@acme.com", ct)).UserId;
        var ids = new Dictionary<string, Guid>();
        await WithDbAsync(async db =>
        {
            ids["expired 8 days"] = AddRefresh(db, user, expires: Now.AddDays(-8));
            ids["expired 6 days"] = AddRefresh(db, user, expires: Now.AddDays(-6));
            ids["revoked, not expired"] = AddRefresh(db, user, expires: Now.AddDays(20), revoked: Now.AddDays(-10));
            ids["usable"] = AddRefresh(db, user, expires: Now.AddDays(25));
            await db.SaveChangesAsync(ct);
        });

        var removed = await RunAsync(ct);

        removed[CleanupKinds.RefreshTokens].ShouldBe(1);
        (await RemainingAsync(db => db.RefreshTokens.Select(token => token.Id), ids.Values, ct))
            .ShouldBe([ids["expired 6 days"], ids["revoked, not expired"], ids["usable"]], ignoreOrder: true);
    }

    [Fact]
    public async Task AUTH270_VerificationAndResetLinksGoAWeekAfterBeingUsedOrExpiring()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = (await SignUpVerifiedAsync("Acme Ltd", "owner@acme.com", ct)).UserId;
        var verification = new Dictionary<string, Guid>();
        var reset = new Dictionary<string, Guid>();
        await WithDbAsync(async db =>
        {
            foreach (var (name, expires, used) in Cases())
            {
                var v = new EmailVerificationToken { Id = Guid.CreateVersion7(), UserId = user, TokenHash = Hash(), CreatedAt = Now.AddDays(-30), ExpiresAt = expires, UsedAt = used };
                var r = new PasswordResetToken { Id = Guid.CreateVersion7(), UserId = user, TokenHash = Hash(), CreatedAt = Now.AddDays(-30), ExpiresAt = expires, UsedAt = used };
                db.EmailVerificationTokens.Add(v);
                db.PasswordResetTokens.Add(r);
                verification[name] = v.Id;
                reset[name] = r.Id;
            }

            await db.SaveChangesAsync(ct);
        });

        var removed = await RunAsync(ct);

        removed[CleanupKinds.VerificationTokens].ShouldBe(2);
        removed[CleanupKinds.ResetTokens].ShouldBe(2);
        (await RemainingAsync(db => db.EmailVerificationTokens.Select(token => token.Id), verification.Values, ct))
            .ShouldBe([verification["used 2 days"], verification["unused, valid"]], ignoreOrder: true);
        (await RemainingAsync(db => db.PasswordResetTokens.Select(token => token.Id), reset.Values, ct))
            .ShouldBe([reset["used 2 days"], reset["unused, valid"]], ignoreOrder: true);

        static IEnumerable<(string Name, DateTimeOffset Expires, DateTimeOffset? Used)> Cases() =>
        [
            ("used 8 days", Now.AddDays(1), Now.AddDays(-8)),
            ("expired 8 days", Now.AddDays(-8), null),
            ("used 2 days", Now.AddDays(1), Now.AddDays(-2)),
            ("unused, valid", Now.AddHours(10), null),
        ];
    }

    [Fact]
    public async Task AUTH270_SentAndFailedEmailsGoAfterTwoWeeksAndAPendingOneNever()
    {
        var ct = TestContext.Current.CancellationToken;
        var ids = new Dictionary<string, Guid>();
        await WithDbAsync(async db =>
        {
            ids["sent 15 days"] = AddEmail(db, EmailOutboxStatus.Sent, created: Now.AddDays(-16), sent: Now.AddDays(-15));
            ids["sent 13 days"] = AddEmail(db, EmailOutboxStatus.Sent, created: Now.AddDays(-14), sent: Now.AddDays(-13));
            ids["failed 15 days"] = AddEmail(db, EmailOutboxStatus.Failed, created: Now.AddDays(-15), sent: null);
            ids["pending 30 days"] = AddEmail(db, EmailOutboxStatus.Pending, created: Now.AddDays(-30), sent: null);
            await db.SaveChangesAsync(ct);
        });

        var removed = await RunAsync(ct);

        removed[CleanupKinds.EmailOutbox].ShouldBe(2);
        (await RemainingAsync(db => db.EmailOutbox.Select(message => message.Id), ids.Values, ct))
            .ShouldBe([ids["sent 13 days"], ids["pending 30 days"]], ignoreOrder: true);
    }

    [Fact]
    public async Task AUTH270_DeliveredWebhooksGoAfterTwoWeeksFailedOnesAfterAMonthAndPendingOnesNever()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, adminToken) = await CreatePlatformAdminAsync("ops@platform.test", ct);
        var product = await CreateActiveProductAsync(adminToken, "marketing", ct);
        var ids = new Dictionary<string, Guid>();
        await WithDbAsync(async db =>
        {
            ids["delivered 15 days"] = AddWebhook(db, product.Id, WebhookStatus.Delivered, deadline: Now.AddDays(-14), delivered: Now.AddDays(-15));
            ids["delivered 13 days"] = AddWebhook(db, product.Id, WebhookStatus.Delivered, deadline: Now.AddDays(-12), delivered: Now.AddDays(-13));
            ids["failed 31 days"] = AddWebhook(db, product.Id, WebhookStatus.Failed, deadline: Now.AddDays(-31), delivered: null);
            ids["failed 29 days"] = AddWebhook(db, product.Id, WebhookStatus.Failed, deadline: Now.AddDays(-29), delivered: null);
            ids["pending, old"] = AddWebhook(db, product.Id, WebhookStatus.Pending, deadline: Now.AddDays(-60), delivered: null);
            await db.SaveChangesAsync(ct);
        });

        var removed = await RunAsync(ct);

        removed[CleanupKinds.DeliveredWebhooks].ShouldBe(1);
        removed[CleanupKinds.FailedWebhooks].ShouldBe(1);
        (await RemainingAsync(db => db.WebhookOutbox.Select(message => message.Id), ids.Values, ct))
            .ShouldBe([ids["delivered 13 days"], ids["failed 29 days"], ids["pending, old"]], ignoreOrder: true);
    }

    [Fact]
    public async Task AUTH270_APreviousProductSecretGoesAWeekAfterItStoppedWorkingAndTheCurrentOneNever()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, adminToken) = await CreatePlatformAdminAsync("ops@platform.test", ct);
        var product = await CreateActiveProductAsync(adminToken, "marketing", ct);
        var ids = new Dictionary<string, Guid>();
        await WithDbAsync(async db =>
        {
            ids["expired 8 days"] = AddSecret(db, product.Id, Now.AddDays(-8));
            ids["expired 6 days"] = AddSecret(db, product.Id, Now.AddDays(-6));
            ids["still in its grace period"] = AddSecret(db, product.Id, Now.AddHours(5));
            await db.SaveChangesAsync(ct);
        });
        var current = 0;
        await WithDbAsync(async db => current = await db.ApplicationSecrets.CountAsync(secret => secret.ApplicationId == product.Id && secret.ExpiresAt == null, ct));

        var removed = await RunAsync(ct);

        removed[CleanupKinds.ExpiredSecrets].ShouldBe(1);
        (await RemainingAsync(db => db.ApplicationSecrets.Select(secret => secret.Id), ids.Values, ct))
            .ShouldBe([ids["expired 6 days"], ids["still in its grace period"]], ignoreOrder: true);
        await WithDbAsync(async db => (await db.ApplicationSecrets.CountAsync(secret => secret.ApplicationId == product.Id && secret.ExpiresAt == null, ct)).ShouldBe(current));
        current.ShouldBe(1, "the product's current secret is still there");
    }

    [Fact]
    public async Task AUTH270_ABacklogIsRemovedInSmallBatchesAndWhatOneRunLeavesTheNextOneTakes()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = (await SignUpVerifiedAsync("Acme Ltd", "owner@acme.com", ct)).UserId;
        await WithDbAsync(async db =>
        {
            for (var i = 0; i < 5; i++)
            {
                AddRefresh(db, user, expires: Now.AddDays(-30 - i));
            }

            await db.SaveChangesAsync(ct);
        });

        var limited = await RunAsync(ct, new CleanupOptions { BatchSize = 2, MaxBatchesPerKind = 1 });
        var rest = await RunAsync(ct, new CleanupOptions { BatchSize = 2, MaxBatchesPerKind = 10 });

        limited[CleanupKinds.RefreshTokens].ShouldBe(2, "one batch of two in this run");
        rest[CleanupKinds.RefreshTokens].ShouldBe(3, "the next run takes the rest, two at a time");
    }

    [Fact]
    public async Task AUTH270_WhileAnotherInstanceIsCleaningThisOneRemovesNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = (await SignUpVerifiedAsync("Acme Ltd", "owner@acme.com", ct)).UserId;
        await WithDbAsync(async db =>
        {
            AddRefresh(db, user, expires: Now.AddDays(-30));
            await db.SaveChangesAsync(ct);
        });

        await using var scope = Harness.CreateScope();
        var other = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        await using var transaction = await other.Database.BeginTransactionAsync(ct);
        await other.Database.SqlQuery<bool>($"SELECT pg_try_advisory_xact_lock({0x41_55_54_48_43_4C_4EL}) AS \"Value\"").SingleAsync(ct);

        var removed = await RunAsync(ct);

        removed.ShouldBeEmpty();
        await transaction.RollbackAsync(ct);
        (await RunAsync(ct))[CleanupKinds.RefreshTokens].ShouldBe(1, "once the other instance is done");
    }

    [Fact]
    public async Task AUTH270_TheAuditLogIsNeverTouchedAndTheRemovedRowsAreCounted()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = (await SignUpVerifiedAsync("Acme Ltd", "owner@acme.com", ct)).UserId;
        var auditBefore = 0;
        await WithDbAsync(async db =>
        {
            AddRefresh(db, user, expires: Now.AddDays(-30));
            AddRefresh(db, user, expires: Now.AddDays(-31));
            db.AuditEntries.Add(new AuditEntry { Id = Guid.CreateVersion7(), OccurredAt = Now.AddDays(-200), Action = "test.old", ActorType = AuditActorType.System });
            await db.SaveChangesAsync(ct);
            auditBefore = await db.AuditEntries.CountAsync(ct);
        });

        await RunAsync(ct, useServiceMetrics: true);

        await WithDbAsync(async db => (await db.AuditEntries.CountAsync(ct)).ShouldBe(auditBefore));
        var text = new StringBuilder();
        await using (var metricsScope = Harness.CreateScope())
        {
            metricsScope.ServiceProvider.GetRequiredService<AuthMetrics>().WriteTo(text);
        }

        text.ToString().ShouldContain("auth_cleanup_rows_removed_total{kind=\"refresh_tokens\"} 2");
    }

    [Fact]
    public async Task AUTH270_TheWorkerRunsOnItsOwnWhenEnabled()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var harness = await AuthApiHarness.CreateAsync(Postgres, ct, new HarnessOptions
        {
            Settings = { ["Auth:Cleanup:Enabled"] = "true", ["Auth:Cleanup:IntervalSeconds"] = "1" },
        });
        Guid oldId;
        await using (var scope = harness.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
            var email = new EmailOutboxMessage
            {
                Id = Guid.CreateVersion7(), ToAddress = "someone@example.com", Template = EmailTemplate.Verification, Status = EmailOutboxStatus.Sent,
                CreatedAt = DateTimeOffset.UtcNow.AddDays(-40), NextAttemptAt = DateTimeOffset.UtcNow.AddDays(-40), SentAt = DateTimeOffset.UtcNow.AddDays(-40),
            };
            db.EmailOutbox.Add(email);
            await db.SaveChangesAsync(ct);
            oldId = email.Id;
        }

        await Eventually.UntilAsync(async () =>
        {
            await using var scope = harness.CreateScope();
            return !await scope.ServiceProvider.GetRequiredService<AuthDbContext>().EmailOutbox.AnyAsync(message => message.Id == oldId, ct);
        }, "the worker removes the old sent email");
    }

    private async Task<IReadOnlyDictionary<string, int>> RunAsync(CancellationToken ct, CleanupOptions? settings = null, bool useServiceMetrics = false)
    {
        await using var scope = Harness.CreateScope();
        var metrics = useServiceMetrics ? scope.ServiceProvider.GetRequiredService<AuthMetrics>() : new AuthMetrics();
        var cleanup = new DataCleanup(scope.ServiceProvider.GetRequiredService<AuthDbContext>(), Microsoft.Extensions.Options.Options.Create(settings ?? new CleanupOptions()), metrics, NullLogger<DataCleanup>.Instance);
        return await cleanup.RunAsync(Now, ct);
    }

    private async Task<List<Guid>> RemainingAsync(Func<AuthDbContext, IQueryable<Guid>> ids, IEnumerable<Guid> planted, CancellationToken ct)
    {
        var plantedIds = planted.ToList();
        var remaining = new List<Guid>();
        await WithDbAsync(async db => remaining = await ids(db).Where(id => plantedIds.Contains(id)).ToListAsync(ct));
        return remaining;
    }

    private static Guid AddRefresh(AuthDbContext db, Guid userId, DateTimeOffset expires, DateTimeOffset? revoked = null)
    {
        var token = new RefreshToken
        {
            Id = Guid.CreateVersion7(), UserId = userId, FamilyId = Guid.CreateVersion7(), TokenHash = Hash(),
            CreatedAt = expires.AddDays(-30), ExpiresAt = expires, RevokedAt = revoked,
        };
        db.RefreshTokens.Add(token);
        return token.Id;
    }

    private static Guid AddEmail(AuthDbContext db, EmailOutboxStatus status, DateTimeOffset created, DateTimeOffset? sent)
    {
        var message = new EmailOutboxMessage
        {
            Id = Guid.CreateVersion7(), ToAddress = "someone@example.com", Template = EmailTemplate.Verification, Status = status,
            CreatedAt = created, NextAttemptAt = created, SentAt = sent,
        };
        db.EmailOutbox.Add(message);
        return message.Id;
    }

    private static Guid AddWebhook(AuthDbContext db, Guid applicationId, WebhookStatus status, DateTimeOffset deadline, DateTimeOffset? delivered)
    {
        var message = new WebhookOutboxMessage
        {
            Id = Guid.CreateVersion7(), ApplicationId = applicationId, EventType = "ApplicationSubscribed", PayloadJson = "{}", Status = status,
            CreatedAt = deadline.AddDays(-1), NextAttemptAt = deadline, DeadlineAt = deadline, DeliveredAt = delivered,
        };
        db.WebhookOutbox.Add(message);
        return message.Id;
    }

    private static Guid AddSecret(AuthDbContext db, Guid applicationId, DateTimeOffset expires)
    {
        var secret = new ApplicationSecret { Id = Guid.CreateVersion7(), ApplicationId = applicationId, SecretHash = Hash(), CreatedAt = expires.AddDays(-60), ExpiresAt = expires };
        db.ApplicationSecrets.Add(secret);
        return secret.Id;
    }

    private static string Hash() => Convert.ToHexString(Guid.NewGuid().ToByteArray()) + Convert.ToHexString(Guid.NewGuid().ToByteArray());
}
