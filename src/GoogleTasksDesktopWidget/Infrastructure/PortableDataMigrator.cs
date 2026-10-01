namespace GoogleTasksDesktopWidget.Infrastructure;

public sealed record PortableDataMigrationResult(
    IReadOnlyList<string> CopiedFiles,
    IReadOnlyList<string> ConflictingFiles,
    bool HasLegacyData);

/// <summary>Copies prior per-user data into the portable folder without replacing existing files.</summary>
public sealed class PortableDataMigrator(AppDataPaths paths)
{
    private const string MigrationMarkerPrefix = "GoogleTasksDesktopWidget legacy migration v1";

    private static readonly string[] DataFiles =
    [
        "settings.json",
        "token.dat",
        "client-secret.dat",
        "cache.dat"
    ];

    public PortableDataMigrationResult Migrate()
    {
        ArgumentNullException.ThrowIfNull(paths);
        EnsureDataDirectoryWritable();

        if (File.Exists(paths.MigrationMarkerFilePath) && ReadMigrationMarker(paths.MigrationMarkerFilePath))
        {
            return new PortableDataMigrationResult([], [], true);
        }

        var copied = new List<string>();
        var conflicts = new List<string>();
        var hasLegacyData = false;

        foreach (var relativePath in DataFiles)
        {
            var sourcePath = paths.GetLegacyFilePath(relativePath);
            if (!File.Exists(sourcePath)) continue;
            hasLegacyData = true;
            MigrateFile(sourcePath, Path.Combine(paths.DataDirectory, relativePath), relativePath, copied, conflicts);
        }

        var legacyLogsDirectory = paths.GetLegacyLogsDirectory();
        if (Directory.Exists(legacyLogsDirectory))
        {
            foreach (var sourcePath in Directory.EnumerateFiles(legacyLogsDirectory, "*", SearchOption.TopDirectoryOnly))
            {
                hasLegacyData = true;
                var relativePath = Path.Combine("logs", Path.GetFileName(sourcePath));
                MigrateFile(sourcePath, Path.Combine(paths.LogsDirectory, Path.GetFileName(sourcePath)), relativePath, copied, conflicts);
            }
        }

        WriteMigrationMarker(hasLegacyData);
        return new PortableDataMigrationResult(copied, conflicts, hasLegacyData);
    }

    private void EnsureDataDirectoryWritable()
    {
        Directory.CreateDirectory(paths.DataDirectory);
        var probePath = Path.Combine(paths.DataDirectory, $".write-check-{Guid.NewGuid():N}.tmp");
        using (var probe = new FileStream(probePath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            probe.WriteByte(0);
            probe.Flush(flushToDisk: true);
        }
        File.Delete(probePath);
    }

    private static void MigrateFile(
        string sourcePath,
        string destinationPath,
        string relativePath,
        ICollection<string> copied,
        ICollection<string> conflicts)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);

        if (File.Exists(destinationPath))
        {
            if (!FilesMatch(sourcePath, destinationPath)) conflicts.Add(relativePath);
            return;
        }

        var temporaryPath = destinationPath + $".migration-{Guid.NewGuid():N}.tmp";
        try
        {
            using (var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var temporary = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.WriteThrough))
            {
                source.CopyTo(temporary);
                temporary.Flush(flushToDisk: true);
            }

            var moved = false;
            try
            {
                File.Move(temporaryPath, destinationPath, overwrite: false);
                moved = true;
            }
            catch (IOException) when (File.Exists(destinationPath))
            {
                // Another writer created the destination. It is never replaced.
            }

            if (!FilesMatch(sourcePath, destinationPath))
            {
                conflicts.Add(relativePath);
                return;
            }

            if (moved) copied.Add(relativePath);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private static bool FilesMatch(string firstPath, string secondPath)
    {
        using var first = new FileStream(firstPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var second = new FileStream(secondPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (first.Length != second.Length) return false;

        var firstBuffer = new byte[81920];
        var secondBuffer = new byte[firstBuffer.Length];
        while (true)
        {
            var firstRead = first.Read(firstBuffer, 0, firstBuffer.Length);
            var secondRead = second.Read(secondBuffer, 0, secondBuffer.Length);
            if (firstRead != secondRead) return false;
            if (firstRead == 0) return true;
            if (!firstBuffer.AsSpan(0, firstRead).SequenceEqual(secondBuffer.AsSpan(0, secondRead))) return false;
        }
    }

    private bool ReadMigrationMarker(string markerPath)
    {
        var contents = File.ReadAllText(markerPath);
        if (string.Equals(contents, MigrationMarkerPrefix + Environment.NewLine + "legacy-data=1", StringComparison.Ordinal)) return true;
        if (string.Equals(contents, MigrationMarkerPrefix + Environment.NewLine + "legacy-data=0", StringComparison.Ordinal)) return false;
        throw new InvalidDataException($"The portable migration marker is invalid: '{markerPath}'.");
    }

    private void WriteMigrationMarker(bool hasLegacyData)
    {
        var markerPath = paths.MigrationMarkerFilePath;
        var temporaryPath = markerPath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            var contents = MigrationMarkerPrefix + Environment.NewLine + (hasLegacyData ? "legacy-data=1" : "legacy-data=0");
            using (var marker = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            using (var writer = new StreamWriter(marker))
            {
                writer.Write(contents);
                writer.Flush();
                marker.Flush(flushToDisk: true);
            }

            try
            {
                File.Move(temporaryPath, markerPath, overwrite: true);
            }
            catch (IOException) when (File.Exists(markerPath))
            {
                // A completed marker is immutable and never replaced.
            }
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }
}
