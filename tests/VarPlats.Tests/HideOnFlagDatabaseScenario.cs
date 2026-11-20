using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using VarPlats.Core;
using VarPlats.Web;
using VarPlats.Web.Data;
using VarPlats.Web.Photos;
using Xunit;

namespace VarPlats.Tests;

/// <summary>
/// Direct publishing and hide-on-flag against the isolated PostGIS database, run by
/// <see cref="ReportWorkflowDatabaseTests"/> once every migration is applied. Uses its own synthetic accounts and
/// reports far from the other scenarios and removes them (and their photo files) at the end.
/// </summary>
internal static class HideOnFlagDatabaseScenario
{
    private const double Latitude = 63.1;
    private const double Longitude = 15.1;

    public static async Task RunAsync(
        IDbContextFactory<ReportDbContext> contexts, UserManager<IdentityUser<Guid>> userManager,
        PhotoWorkflowDatabaseScenario.MemoryStorage storage)
    {
        var author = await CreateUserAsync(userManager, "synthetic-hide-author");
        var flagger = await CreateUserAsync(userManager, "synthetic-hide-flagger");
        var other = await CreateUserAsync(userManager, "synthetic-hide-other");
        var limited = await CreateUserAsync(userManager, "synthetic-hide-limited");
        var authorAccount = Account(author, "Member");
        var flaggerAccount = Account(flagger, "Member");
        var otherAccount = Account(other, "Member");
        var limitedAccount = Account(limited, "Member");
        var admin = Account(Guid.NewGuid(), "Admin");
        var anonymous = new ClaimsPrincipal();
        var access = SyntheticReportAccountAccess.Instance;

        var pointsOptions = new PointsOptions();
        var photoOptions = new PhotoOptions
        {
            Enabled = true,
            MaxFiles = 100,
            MaxStorageBytes = 64 * 1024 * 1024,
            MinimumFreeBytes = 64 * 1024 * 1024,
            MaxConcurrentUploads = 1
        };
        var mode = new ReportingMode(false, false, true, true, true, true, true, true,
            DirectPublishEnabled: true, HideOnFlagEnabled: true, HideOnFlagMinimumFlags: 1);
        var reviewMode = mode with { DirectPublishEnabled = false };
        var hideOff = mode with { HideOnFlagEnabled = false };
        var twoFlags = mode with { HideOnFlagMinimumFlags = 2 };
        var store = new DatabaseReportStore(contexts, mode, pointsOptions);
        var writer = new DatabasePhotoWriter(contexts, storage, photoOptions, mode,
            NullLogger<DatabasePhotoWriter>.Instance);
        var reader = new DatabasePhotoReader(contexts, storage, photoOptions, mode,
            NullLogger<DatabasePhotoReader>.Instance, access);
        using var processor = new PhotoProcessor(photoOptions, new PhotoWorkflowDatabaseScenario.CleanScanner());
        var uploads = new PhotoUploadCommands(mode, photoOptions, processor, writer, access);
        var reports = new ReportCommands(store, mode,
            new DatabaseReportWorkflowStore(contexts, mode, photoOptions, storage), writer, access);
        var actions = new ReportActionCommands(mode, new DatabaseReportActionStore(contexts, mode, pointsOptions),
            writer, access);
        var flags = new ContentFlagCommands(mode, new DatabaseContentFlagStore(contexts, mode), access);
        var points = new PointsCommands(mode, pointsOptions, new DatabasePointsStore(contexts, mode, pointsOptions),
            access);
        var image = PhotoWorkflowTests.SyntheticPngWithText("synthetic-direct-publish");
        Assert.True(MapArea.TryCreate(Latitude - 0.01, Longitude - 0.01, Latitude + 0.01, Longitude + 0.01,
            out var around, out _));
        var filesBefore = storage.List().Count;

        // Direct publishing: public at once, through the same submission and approval rows as a review.
        var text = await reports.AddAsync(authorAccount, Draft("Syntetisk direktpublicerad rapport"));
        Assert.Contains(await store.ListAsync(), item => item.Id == text.Id);
        await using (var db = contexts.CreateDbContext())
        {
            var submission = await db.ReportSubmissions.AsNoTracking().SingleAsync(item => item.Id == text.Id);
            Assert.Equal(ReportReviewStatus.Approved, submission.ReviewStatus);
            Assert.Equal(text.Id, submission.PublishedReportId);
            Assert.Equal(author, submission.AuthorId);
        }
        Assert.DoesNotContain(await reports.ListPendingAsync(admin), item => item.Report.Id == text.Id);

        // A photo still goes through the full pipeline and is checked against its hash before publication.
        var withPhoto = await uploads.SubmitReportAsync(authorAccount, Draft("Syntetisk direktpublicerad bild"),
            new MemoryStream(image));
        Assert.Contains(await store.ListAsync(), item => item.Id == withPhoto.Id);
        Assert.NotNull(await reader.ReadReportAsync(anonymous, withPhoto.Id));
        storage.FailRead = true;
        await Assert.ThrowsAsync<PhotoReviewException>(() => uploads.SubmitReportAsync(authorAccount,
            Draft("Syntetisk bild som inte kan kontrolleras"), new MemoryStream(image)));
        storage.FailRead = false;
        await using (var db = contexts.CreateDbContext())
            Assert.False(await db.ReportSubmissions.AnyAsync(item =>
                item.Title == "Syntetisk bild som inte kan kontrolleras"));
        Assert.Equal(filesBefore + 1, storage.List().Count);

        // With the switch off a member's report still waits for review.
        var reviewCommands = new ReportCommands(new DatabaseReportStore(contexts, reviewMode), reviewMode,
            new DatabaseReportWorkflowStore(contexts, reviewMode), accountAccess: access);
        var queued = await reviewCommands.AddAsync(authorAccount, Draft("Syntetisk väntande rapport i direkttest"));
        Assert.DoesNotContain(await store.ListAsync(), item => item.Id == queued.Id);
        Assert.Contains(await reports.ListPendingAsync(admin), item => item.Report.Id == queued.Id);
        Assert.True(await reports.DecideAsync(admin, queued.Id, ReportReviewStatus.Rejected));

        // The author's own flag never hides the report.
        Assert.Equal(FlagResult.Flagged, await flags.FlagAsync(authorAccount, text.Id, null, Flag()));
        Assert.Contains(await store.ListAsync(), item => item.Id == text.Id);
        Assert.False((await flags.GetVisibilityAsync(anonymous, text.Id)).ReportHidden);

        // One flag from another account hides both reports at once from every list, but not from the page read.
        Assert.Equal(FlagResult.Flagged, await flags.FlagAsync(flaggerAccount, text.Id, null, Flag()));
        Assert.Equal(FlagResult.Flagged, await flags.FlagAsync(flaggerAccount, withPhoto.Id, null, Flag()));
        var hideOffStore = new DatabaseReportStore(contexts, hideOff, pointsOptions);
        foreach (var id in new[] { text.Id, withPhoto.Id })
        {
            Assert.DoesNotContain(await store.ListAsync(), item => item.Id == id);
            Assert.DoesNotContain((await store.ListInAreaAsync(around)).Reports, item => item.Id == id);
            Assert.DoesNotContain(await store.ListNearAsync(Latitude, Longitude), item => item.Report.Id == id);
            Assert.NotNull(await store.GetAsync(id));
            Assert.Contains(await hideOffStore.ListAsync(), item => item.Id == id);
        }
        Assert.True((await flags.GetVisibilityAsync(anonymous, text.Id)).ReportWithheld);
        Assert.True((await flags.GetVisibilityAsync(otherAccount, text.Id)).ReportWithheld);
        Assert.True((await flags.GetVisibilityAsync(flaggerAccount, text.Id)).ReportWithheld);
        var authorView = await flags.GetVisibilityAsync(authorAccount, text.Id);
        Assert.True(authorView.ReportHidden);
        Assert.False(authorView.ReportWithheld);
        Assert.False(authorView.IsModerator);
        var adminView = await flags.GetVisibilityAsync(admin, text.Id);
        Assert.False(adminView.ReportWithheld);
        Assert.True(adminView.IsModerator);
        Assert.Null(await reader.ReadReportAsync(anonymous, withPhoto.Id));
        Assert.Null(await reader.ReadReportAsync(otherAccount, withPhoto.Id));
        Assert.Empty(await reader.VisibleReportIdsAsync(otherAccount, [withPhoto.Id]));
        Assert.NotNull(await reader.ReadReportAsync(authorAccount, withPhoto.Id));
        Assert.NotNull(await reader.ReadReportAsync(admin, withPhoto.Id));
        var queueItem = Assert.Single(await flags.ListOpenAsync(admin), item => item.ReportId == text.Id);
        Assert.True(queueItem.Hidden);
        Assert.Equal(2, queueItem.FlagCount);
        Assert.False(await actions.AddAsync(otherAccount, text.Id,
            new ReportActionDraft { Description = "Syntetisk åtgärd på en dold rapport." }));

        // Restoring dismisses the flags and shows the report again; the same account cannot hide it again.
        Assert.Equal(2, await flags.DismissAsync(admin, text.Id, null));
        Assert.Contains(await store.ListAsync(), item => item.Id == text.Id);
        Assert.True(Assert.Single(await flags.ListOpenAsync(admin), item => item.ReportId == withPhoto.Id).Hidden);
        Assert.Equal(FlagResult.AlreadyFlagged, await flags.FlagAsync(flaggerAccount, text.Id, null, Flag()));
        Assert.Equal(FlagResult.AlreadyFlagged, await flags.FlagAsync(authorAccount, text.Id, null, Flag()));
        Assert.Contains(await store.ListAsync(), item => item.Id == text.Id);

        // A higher threshold needs that many flags from accounts other than the author.
        var twoFlagStore = new DatabaseReportStore(contexts, twoFlags, pointsOptions);
        Assert.Contains(await twoFlagStore.ListAsync(), item => item.Id == withPhoto.Id);
        Assert.Equal(FlagResult.Flagged, await flags.FlagAsync(authorAccount, withPhoto.Id, null, Flag()));
        Assert.Contains(await twoFlagStore.ListAsync(), item => item.Id == withPhoto.Id);
        Assert.Equal(FlagResult.Flagged, await flags.FlagAsync(otherAccount, withPhoto.Id, null, Flag()));
        Assert.DoesNotContain(await twoFlagStore.ListAsync(), item => item.Id == withPhoto.Id);

        // The per-account flag limit applies before anything is saved, so it also caps how much one account hides.
        var limiter = new WriteRateLimiter(new RateLimitOptions
        {
            Enabled = true,
            Flags = new RateLimitWindow { PermitLimit = 1, WindowSeconds = 3600 }
        }, TimeProvider.System, NullLogger<WriteRateLimiter>.Instance);
        var limitedFlags = new ContentFlagCommands(mode, new DatabaseContentFlagStore(contexts, mode), access, limiter);
        Assert.Equal(FlagResult.Flagged, await limitedFlags.FlagAsync(limitedAccount, withPhoto.Id, null, Flag()));
        await Assert.ThrowsAsync<WriteRateLimitException>(() =>
            limitedFlags.FlagAsync(limitedAccount, text.Id, null, Flag()));
        Assert.Contains(await store.ListAsync(), item => item.Id == text.Id);

        // Action texts: hidden content keeps its confirmations and points but takes no new ones.
        Assert.True(await actions.AddAsync(otherAccount, text.Id,
            new ReportActionDraft { Description = "Syntetisk åtgärd som sedan anmäls." }));
        var action = Assert.Single(await actions.ListAsync(otherAccount, text.Id));
        Assert.Equal(ActionConfirmationResult.Confirmed, await actions.ConfirmAsync(authorAccount, text.Id, action.Id));
        Assert.Equal(HeartResult.Added, await points.ToggleHeartAsync(flaggerAccount, text.Id, action.Id));
        Assert.Equal(pointsOptions.ConfirmedActionPoints, (await points.GetOwnAsync(otherAccount)).Balance);
        Assert.True(await uploads.SubmitActionAsync(otherAccount, text.Id,
            new ReportActionDraft { Description = "Syntetisk åtgärd med bild som anmäls." }, new MemoryStream(image)));
        var photoAction = Assert.Single(await actions.ListAsync(otherAccount, text.Id),
            item => item.Id != action.Id).Id;
        Assert.Equal(FlagResult.Flagged, await flags.FlagAsync(otherAccount, text.Id, action.Id, Flag()));
        Assert.Empty((await flags.GetVisibilityAsync(anonymous, text.Id)).HiddenActionIds);
        Assert.Equal(FlagResult.Flagged, await flags.FlagAsync(flaggerAccount, text.Id, action.Id, Flag()));
        Assert.Equal(FlagResult.Flagged, await flags.FlagAsync(flaggerAccount, text.Id, photoAction, Flag()));
        var publicView = await flags.GetVisibilityAsync(anonymous, text.Id);
        Assert.False(publicView.ReportHidden);
        Assert.Equal(new HashSet<Guid> { action.Id, photoAction }, publicView.HiddenActionIds.ToHashSet());
        Assert.True(publicView.ActionWithheld(Assert.Single(await actions.ListAsync(anonymous, text.Id),
            item => item.Id == action.Id)));
        var ownView = await flags.GetVisibilityAsync(otherAccount, text.Id);
        Assert.False(ownView.ActionWithheld(Assert.Single(await actions.ListAsync(otherAccount, text.Id),
            item => item.Id == action.Id)));
        Assert.Contains(await store.ListAsync(), item => item.Id == text.Id);
        Assert.Null(await reader.ReadActionAsync(anonymous, photoAction));
        Assert.Null(await reader.ReadActionAsync(authorAccount, photoAction));
        Assert.NotNull(await reader.ReadActionAsync(otherAccount, photoAction));
        Assert.NotNull(await reader.ReadActionAsync(admin, photoAction));
        Assert.Equal(ActionConfirmationResult.Hidden, await actions.ConfirmAsync(flaggerAccount, text.Id, action.Id));
        Assert.Equal(HeartResult.Hidden, await points.ToggleHeartAsync(authorAccount, text.Id, action.Id));
        Assert.Equal(GiftResult.Hidden, await points.GiftAsync(authorAccount, text.Id, action.Id,
            new PointsGiftDraft { Amount = 1 }));
        Assert.Equal(pointsOptions.ConfirmedActionPoints, (await points.GetOwnAsync(otherAccount)).Balance);
        Assert.Equal(HeartResult.Removed, await points.ToggleHeartAsync(flaggerAccount, text.Id, action.Id));
        Assert.True((await flags.ListOpenAsync(admin)).Single(item => item.ActionId == action.Id).Hidden);

        // Removing hidden content reverses its points through the existing hook.
        Assert.True(await actions.RemoveAsync(admin, text.Id, action.Id));
        var afterRemoval = await points.GetOwnAsync(otherAccount);
        Assert.Equal(0, afterRemoval.Balance);
        Assert.Equal(PointsReversalCause.ActionRemoved,
            afterRemoval.History.Single(item => item.Reason == PointsReason.Reversal).Cause);

        // Clean up: reports, their photo files and the synthetic accounts.
        Assert.True(await reports.RemoveAsync(admin, text.Id));
        Assert.True(await reports.RemoveAsync(admin, withPhoto.Id));
        Assert.Equal(filesBefore, storage.List().Count);
        await using (var db = contexts.CreateDbContext())
            await db.ReportSubmissions.Where(item => item.Id == queued.Id).ExecuteDeleteAsync();
        foreach (var id in new[] { author, flagger, other, limited })
            Assert.True((await userManager.DeleteAsync((await userManager.FindByIdAsync(id.ToString()))!)).Succeeded);
    }

    private static ReportDraft Draft(string title)
    {
        var draft = ReportTests.ValidDraft();
        draft.Title = title;
        draft.Latitude = Latitude;
        draft.Longitude = Longitude;
        return draft;
    }

    private static ContentFlagDraft Flag() => new() { Reason = FlagReason.Spam };

    private static async Task<Guid> CreateUserAsync(UserManager<IdentityUser<Guid>> userManager, string name)
    {
        var user = new IdentityUser<Guid> { Id = Guid.NewGuid(), UserName = name };
        Assert.True((await userManager.CreateAsync(user)).Succeeded);
        return user.Id;
    }

    private static ClaimsPrincipal Account(Guid id, string role) => new(new ClaimsIdentity(
        [new Claim(ClaimTypes.NameIdentifier, id.ToString()), new Claim(ClaimTypes.Role, role)], "test"));
}
