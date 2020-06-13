using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Dangl.AspNetCore.FileHandling.Azure;
using Dangl.Data.Shared;
using Newtonsoft.Json;
using Nuke.Common;
using Nuke.Common.CI.Jenkins;
using Nuke.Common.Execution;
using Nuke.Common.IO;
using static Nuke.Common.IO.FileSystemTasks;
using System.IO.Compression;
using static Nuke.Common.IO.PathConstruction;

[UnsetVisualStudioEnvironmentVariables]
class Build : NukeBuild
{
    public static int Main () => Execute<Build>(x => x.ListBackups);

    [Parameter("Configuration to build - Default is 'Debug' (local) or 'Release' (server)")]
    readonly Configuration Configuration = IsLocalBuild ? Configuration.Debug : Configuration.Release;

    AbsolutePath OutputDirectory = RootDirectory / "output";

    const string METADATA_FILE_NAME = "JenkinsBackupMetadata.json";

    [Parameter] string ContainerName = "jenkins-backup";
    [Parameter] string BlobStorageConnectionString;
    [Parameter] string JenkinsHome = Jenkins.Instance?.JenkinsHome;
    [Parameter] string BackupId;

    Target Clean => _ => _
        .Executes(() =>
        {
            EnsureCleanDirectory(OutputDirectory);
        });

    Target ListBackups => _ => _
        .Requires(() => BlobStorageConnectionString)
        .Requires(() => ContainerName)
        .Executes(async () =>
        {
            var backupDetails = await GetBackupMetadataAsync();
            var backupTotalSize = ConvertLongToReadableFileSize(backupDetails.Sum(d => d.FileSizeInBytes));
            Logger.Normal($"Found {backupDetails.Count} backups, total size {backupTotalSize}.");
            foreach (var detail in backupDetails.OrderBy(d => d.DateUtc))
            {
                var fileSize = ConvertLongToReadableFileSize(detail.FileSizeInBytes);
                Logger.Normal($"Id: {detail.BackupId}, {fileSize} @ {detail.DateUtc:dd.MM.yyyy HH:mm}");
            }
        });

    Target DownloadBackup => _ => _
        .DependsOn(Clean)
        .Requires(() => BackupId)
        .Requires(() => BlobStorageConnectionString)
        .Requires(() => ContainerName)
        .Executes(async () =>
        {
            var backupData = await GetCurrentBackupMetadata();

            var filePath = backupData.ToBlobStorageFilename();
            var fileDownloadResult = await DownloadFileAsync(filePath);
            if (!fileDownloadResult.IsSuccess)
            {
                ControlFlow.Fail(fileDownloadResult.ErrorMessage);
            }

            try
            {
                var outputPath = OutputDirectory / "Backup.zip";
                Logger.Normal($"Saving backup to {outputPath}");
                using (var fs = File.Create(outputPath))
                {
                    await fileDownloadResult.Value.CopyToAsync(fs);
                }
            }
            finally
            {
                fileDownloadResult.Value.Dispose();
            }
        });

    Target DeleteBackup => _ => _
        .Requires(() => BackupId)
        .Requires(() => BlobStorageConnectionString)
        .Requires(() => ContainerName)
        .Executes(async () =>
        {
            var backupDetails = await GetBackupMetadataAsync();
            if (!Guid.TryParse(BackupId, out var id))
            {
                ControlFlow.Fail($"The {BackupId} must be a Guid.");
            }

            var backupDetail = backupDetails.SingleOrDefault(d => d.BackupId == id);
            var fileManager = await GetFileManagerAsync();
            var deletionResult = await fileManager.DeleteFileAsync(ContainerName, backupDetail.ToBlobStorageFilename());
            if (!deletionResult.IsSuccess)
            {
                ControlFlow.Fail(deletionResult.ErrorMessage);
            }

            var updatedBackupDetails = backupDetails
                .Where(d => d.BackupId != id)
                .ToList();
            var metadataUpdateResult = await SaveBackupMetadataAsync(updatedBackupDetails);
            if (!metadataUpdateResult.IsSuccess)
            {
                ControlFlow.Fail(metadataUpdateResult.ErrorMessage);
            }
        });

    Target BackupInstance => _ => _
        .DependsOn(Clean)
        .Requires(() => JenkinsHome)
        .Requires(() => ContainerName)
        .Requires(() => BlobStorageConnectionString)
        .Executes(async () =>
        {
            Logger.Normal("Starting backup of Jenkins. This will not backup the master key, please ensure it's backed up separately.");
            EnsureExistingDirectory(JenkinsHome);

            var filesToBackup = new[]
            {
                "config.xml",
                "credentials.xml",
                "jenkins.xml"
            };

            Func<ZipArchiveEntry, string, Task> addFileToZipArchive = async (entry, absolutePath) =>
            {
                using (var entryStream = entry.Open())
                {
                    using (var entryFs = File.OpenRead(absolutePath))
                    {
                        await entryFs.CopyToAsync(entryStream);
                    }
                }
            };

            var outputArchive = OutputDirectory / "Backup.zip";
            long backupSizeInBytes = 0;
            using (var fs = File.Create(outputArchive))
            {
                using (var zipFile = new ZipArchive(fs, ZipArchiveMode.Create))
                {
                    Logger.Normal("Zipping config files");
                    foreach (var fileToBackup in filesToBackup)
                    {
                        var absolutePath = Path.Combine(JenkinsHome, fileToBackup);
                        var entry = zipFile.CreateEntry(fileToBackup);
                        await addFileToZipArchive(entry, absolutePath);
                    }

                    Logger.Normal("Zipping users");
                    var jenkinsUsersDirectory = Path.Combine(JenkinsHome, "users");
                    var userFiles = GlobFiles(jenkinsUsersDirectory, "**/*");
                    foreach (var userFile in userFiles)
                    {
                        var entryPath = $"users/{userFile.Substring(jenkinsUsersDirectory.Length + 1)}";
                        var entry = zipFile.CreateEntry(entryPath);
                        await addFileToZipArchive(entry, userFile);
                    }

                    var jenkinsJobsDirectory = Path.Combine(JenkinsHome, "jobs");
                    var jobs = GlobDirectories(jenkinsJobsDirectory, "*");
                    foreach (var job in jobs)
                    {
                        var jobName = Path.GetFileName(job);
                        if (jobName == Jenkins.Instance?.JobBaseName)
                        {
                            Logger.Normal($"Skipping current job {jobName} to circumvent file lock of current running job");
                            continue;
                        }

                        Logger.Normal($"Zipping job {jobName}");
                        var jobFiles = GlobFiles(job, "**/*");
                        foreach (var jobFile in jobFiles)
                        {
                            var entryPath = $"jobs/{jobName}/{jobFile.Substring(job.Length + 1)}";
                            var entry = zipFile.CreateEntry(entryPath);
                            await addFileToZipArchive(entry, jobFile);
                        }
                    }

                    backupSizeInBytes = fs.Length;
                }
            }

            var metadata = new BackupMetadata
            {
                FileSizeInBytes = backupSizeInBytes
            };

            Logger.Normal($"Uploading backup, total size: {ConvertLongToReadableFileSize(backupSizeInBytes)}");
            using (var fs = File.OpenRead(outputArchive))
            {
                await UploadFileAsync(fs, metadata.ToBlobStorageFilename());
            }

            var currentMetadata = (await MetadataFileExists())
                ? await GetBackupMetadataAsync()
                : new List<BackupMetadata>();
            currentMetadata.Add(metadata);
            var saveMetadataResult = await SaveBackupMetadataAsync(currentMetadata);
            if (!saveMetadataResult.IsSuccess)
            {
                ControlFlow.Fail(saveMetadataResult.ErrorMessage);
            }
        });

    async Task<RepositoryResult<Stream>> DownloadFileAsync(string fileName)
    {
        var fileManager = await GetFileManagerAsync();
        return await fileManager.GetFileAsync(ContainerName, fileName);
    }

    async Task<RepositoryResult> UploadFileAsync(Stream stream, string fileName)
    {
        var fileManager = await GetFileManagerAsync();
        return await fileManager.SaveFileAsync(ContainerName, fileName, stream);
    }

    async Task<AzureBlobFileManager> GetFileManagerAsync()
    {
        var fileManager = new AzureBlobFileManager(BlobStorageConnectionString);
        await fileManager.EnsureContainerCreated(ContainerName);
        return fileManager;
    }

    async Task<BackupMetadata> GetCurrentBackupMetadata()
    {
        if (!Guid.TryParse(BackupId, out var id))
        {
            ControlFlow.Fail($"The {BackupId} must be a Guid.");
        }

        var details = await GetBackupMetadataAsync();
        var backupData = details.SingleOrDefault(d => d.BackupId == id);
        if (backupData == null)
        {
            ControlFlow.Fail($"Could not find a backup with the id {BackupId}");
        }

        return backupData;
    }

    async Task<List<BackupMetadata>> GetBackupMetadataAsync()
    {
        var metadataResult = await DownloadFileAsync(METADATA_FILE_NAME);
        if (!metadataResult.IsSuccess)
        {
            ControlFlow.Fail(metadataResult.ErrorMessage);
        }

        try
        {
            using (var sr = new StreamReader(metadataResult.Value))
            {
                var jsonRaw = await sr.ReadToEndAsync();
                var details = JsonConvert.DeserializeObject<List<BackupMetadata>>(jsonRaw);
                return details;
            }
        }
        finally
        {
            metadataResult.Value.Dispose();
        }
    }

    async Task<bool> MetadataFileExists()
    {
        var fileManager = await GetFileManagerAsync();
        var metadataResult = await DownloadFileAsync(METADATA_FILE_NAME);
        if (metadataResult.IsSuccess)
        {
            return true;
        }

        return false;
    }

    async Task<RepositoryResult> SaveBackupMetadataAsync(List<BackupMetadata> backupDetails)
    {
        var json = JsonConvert.SerializeObject(backupDetails, Formatting.Indented);
        var memStream = new MemoryStream();
        using (var sw = new StreamWriter(memStream, leaveOpen: true))
        {
            await sw.WriteAsync(json);
        }
        memStream.Position = 0;

        var fileManager = await GetFileManagerAsync();
        var result = await fileManager.SaveFileAsync(ContainerName, METADATA_FILE_NAME, memStream);
        return result;
    }

    public string ConvertLongToReadableFileSize(long fileSize)
    {
        if (fileSize < 1024L)
        {
            return $"{fileSize:0} B";
        }

        if (fileSize < 1024L * 1024L)
        {
            return $"{fileSize / 1024d:0.000} kB";
        }
        if (fileSize < 1024L * 1024L * 1024L)
        {
            return $"{fileSize / (1024d * 1024d):0.000} MB";
        }
        if (fileSize < 1024L * 1024L * 1024L * 1024L)
        {
            return $"{fileSize / (1024d * 1024d * 1024d):0.000} GB";
        }

        return "Huge!";
    }
}
