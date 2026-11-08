namespace VarPlats.Web.Photos;

public sealed class PhotoOptions
{
    public bool Enabled { get; set; }
    public string StoragePath { get; set; } = "";
    public string ScannerSocketPath { get; set; } = "";
    public int MaxFiles { get; set; } = 1000;
    public long MaxStorageBytes { get; set; } = 512L * 1024 * 1024;
    public long MinimumFreeBytes { get; set; } = 512L * 1024 * 1024;
    public int MaxConcurrentUploads { get; set; } = 1;
    public int UploadTimeoutSeconds { get; set; } = 30;
    public int ScannerTimeoutSeconds { get; set; } = 10;

    public void Validate(ReportingMode mode, bool authenticationEnabled, string webRootPath)
    {
        if (!Enabled) return;
        if (!authenticationEnabled || !mode.WorkflowEnabled || mode.Demo || mode.LocalWrites)
            throw new InvalidOperationException("Bilder kräver autentisering och rapportgranskning i databasläge.");
        if (!Path.IsPathFullyQualified(StoragePath) || !Path.IsPathFullyQualified(ScannerSocketPath))
            throw new InvalidOperationException("Bilder kräver absoluta sökvägar till privat lagring och lokal skanner.");
        if (System.Text.Encoding.UTF8.GetByteCount(ScannerSocketPath) > 107)
            throw new InvalidOperationException("Skannerns Unix-socket har för lång sökväg.");
        if (MaxFiles is < 1 or > 100_000
            || MaxStorageBytes is < PhotoSanitizer.MaximumOutputBytes or > 10L * 1024 * 1024 * 1024
            || MinimumFreeBytes is < 64L * 1024 * 1024 or > 100L * 1024 * 1024 * 1024
            || MaxConcurrentUploads is < 1 or > 2
            || UploadTimeoutSeconds is < 1 or > 120
            || ScannerTimeoutSeconds is < 1 or > 30)
            throw new InvalidOperationException("Bildernas lagrings-, samtidighets- eller tidsgräns är ogiltig.");

        StoragePath = Path.GetFullPath(StoragePath);
        ScannerSocketPath = Path.GetFullPath(ScannerSocketPath);
        var webRoot = Path.GetFullPath(webRootPath);
        if (SameOrInside(StoragePath, webRoot) || SameOrInside(webRoot, StoragePath)
            || SameOrInside(ScannerSocketPath, webRoot)
            || SameOrInside(ScannerSocketPath, StoragePath))
            throw new InvalidOperationException("Bildlagring och skanner får inte ligga i webroot eller dela lagringskatalog.");
    }

    private static bool SameOrInside(string path, string directory)
    {
        var relative = Path.GetRelativePath(directory, path);
        return relative == "." || (relative != ".."
            && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            && !Path.IsPathFullyQualified(relative));
    }
}
