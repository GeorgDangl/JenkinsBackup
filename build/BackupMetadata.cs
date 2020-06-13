using System;

public class BackupMetadata
{
    public Guid BackupId { get; set; } = Guid.NewGuid();
    public DateTimeOffset DateUtc { get; set; } = DateTimeOffset.UtcNow;
    public long FileSizeInBytes { get; set; }

    public string ToBlobStorageFilename()
    {
        return $"backups/{BackupId}.zip".ToLowerInvariant();
    }

}
