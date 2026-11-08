using System.Security.Claims;
using Microsoft.AspNetCore.Components.Forms;
using VarPlats.Core;

namespace VarPlats.Web.Photos;

public sealed class PhotoUploadException(string message) : Exception(message);

public sealed record PhotoSelection(IBrowserFile? File, bool Invalid);

public interface IPhotoMalwareScanner
{
    Task<bool> IsCleanAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken);
}

public interface IPhotoWriter
{
    Task<Report> SubmitReportAsync(
        Guid authorId, Report report, SanitizedPhoto photo, CancellationToken cancellationToken);
    Task<bool> SubmitActionAsync(
        Guid authorId, Guid reportId, string description, SanitizedPhoto photo, CancellationToken cancellationToken);
}

public sealed class PhotoProcessor(PhotoOptions options, IPhotoMalwareScanner scanner) : IDisposable
{
    private readonly SemaphoreSlim slots = new(options.MaxConcurrentUploads, options.MaxConcurrentUploads);

    public async Task<T> ProcessAsync<T>(
        Stream input, Func<SanitizedPhoto, CancellationToken, Task<T>> save,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(save);
        if (!await slots.WaitAsync(0, cancellationToken))
            throw new PhotoUploadException("Bildhanteringen är upptagen. Försök igen senare.");

        try
        {
            var bytes = await ReadWithTimeoutAsync(input, cancellationToken);
            await RequireCleanAsync(bytes, cancellationToken);
            var photo = PhotoSanitizer.Sanitize(bytes);
            await RequireCleanAsync(photo.Bytes, cancellationToken);
            return await save(photo, cancellationToken);
        }
        finally { slots.Release(); }
    }

    private async Task<byte[]> ReadWithTimeoutAsync(Stream input, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.UploadTimeoutSeconds));
        try
        {
            return await ReadBoundedAsync(input, timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            throw new PhotoUploadException("Bilduppladdningen tog för lång tid. Ingen text eller bild sparades.");
        }
    }

    private async Task RequireCleanAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    {
        try
        {
            if (!await scanner.IsCleanAsync(bytes, cancellationToken))
                throw new PhotoUploadException("Bilden kunde inte godkännas av den lokala säkerhetskontrollen.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (PhotoUploadException) { throw; }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            throw new PhotoUploadException("Den lokala säkerhetskontrollen är inte tillgänglig. Bilden sparades inte.");
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream input, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[64 * 1024];
        while (true)
        {
            var remaining = PhotoSanitizer.MaximumInputBytes + 1 - (int)buffer.Length;
            var read = await input.ReadAsync(chunk.AsMemory(0, Math.Min(chunk.Length, remaining)), cancellationToken);
            if (read == 0) break;
            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
            if (buffer.Length > PhotoSanitizer.MaximumInputBytes)
                throw new PhotoUploadException("Bilden är större än 5 MiB. Ingen text eller bild sparades.");
        }
        return buffer.ToArray();
    }

    public void Dispose() => slots.Dispose();
}

public sealed class PhotoUploadCommands(
    ReportingMode mode, PhotoOptions options, PhotoProcessor? processor = null, IPhotoWriter? writer = null,
    IReportAccountAccess? accountAccess = null)
{
    public async Task<Report> SubmitReportAsync(
        ClaimsPrincipal user, ReportDraft draft, Stream input, CancellationToken cancellationToken = default)
    {
        var authorId = await RequireWriteAsync(user, cancellationToken);
        ArgumentNullException.ThrowIfNull(draft);
        var report = draft.ToReport();
        return await Processor().ProcessAsync(input, async (photo, token) =>
        {
            await RequireWriteAsync(user, token);
            return await Writer().SubmitReportAsync(authorId, report, photo, token);
        }, cancellationToken);
    }

    public async Task<bool> SubmitActionAsync(
        ClaimsPrincipal user, Guid reportId, ReportActionDraft draft, Stream input,
        CancellationToken cancellationToken = default)
    {
        var authorId = await RequireWriteAsync(user, cancellationToken);
        ArgumentNullException.ThrowIfNull(draft);
        var description = draft.ValidatedDescription();
        return await Processor().ProcessAsync(input, async (photo, token) =>
        {
            await RequireWriteAsync(user, token);
            return await Writer().SubmitActionAsync(authorId, reportId, description, photo, token);
        }, cancellationToken);
    }

    private async Task<Guid> RequireWriteAsync(ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        if (!options.Enabled || !mode.PublicWorkflow)
            throw new InvalidOperationException("Bilduppladdning är inte aktiverad för skrivning.");
        var rights = accountAccess is null
            ? default : await accountAccess.VerifyAsync(user, cancellationToken);
        if (!ReportCommands.CanReport(user) || !rights.CanReport || rights.AccountId != ReportCommands.UserId(user))
            throw new UnauthorizedAccessException("Ett behörigt konto krävs för bilduppladdning.");
        return rights.AccountId!.Value;
    }

    private PhotoProcessor Processor() => processor
        ?? throw new InvalidOperationException("Bildhantering är inte tillgänglig.");
    private IPhotoWriter Writer() => writer
        ?? throw new InvalidOperationException("Bildlagring är inte tillgänglig.");
}
