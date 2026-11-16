using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Sentinel.Core.Model;
using Sentinel.Core.Runs;
using Sentinel.Core.Sysmon;

namespace Sentinel.Core.Store;

public static class BackupService
{
    private const string Format = "sentinel.backup/v1";

    public static BackupReport Create(
        string databasePath,
        string outputPath,
        DateTimeOffset? now = null,
        string? signingPrivateKeyPem = null)
    {
        var sourcePath = Path.GetFullPath(databasePath);
        var backupPath = Path.GetFullPath(outputPath);
        var manifestPath = backupPath + ".manifest.json";
        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException($"source database not found: {sourcePath}");
        }

        if (string.Equals(sourcePath, backupPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Backup output must not overwrite the source database.");
        }

        if (File.Exists(backupPath) || File.Exists(manifestPath))
        {
            throw new IOException($"backup already exists: {backupPath}");
        }

        var directory = Path.GetDirectoryName(backupPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var partialPath = backupPath + ".partial";
        var partialManifest = manifestPath + ".partial";
        try
        {
            CopyOnline(sourcePath, partialPath);
            var manifest = ReadManifestValues(partialPath, now ?? DateTimeOffset.UtcNow);
            File.Move(partialPath, backupPath);
            var finalManifest = manifest with
            {
                DatabaseSha256 = HashFile(backupPath),
                DatabaseBytes = new FileInfo(backupPath).Length
            };
            if (!string.IsNullOrWhiteSpace(signingPrivateKeyPem))
            {
                using var key = ECDsa.Create();
                key.ImportFromPem(signingPrivateKeyPem);
                var publicKey = key.ExportSubjectPublicKeyInfoPem();
                finalManifest = finalManifest with { SigningPublicKeyPem = publicKey };
                var payload = Encoding.UTF8.GetBytes(CanonicalJson.Serialize(finalManifest));
                var signature = key.SignData(
                    payload,
                    HashAlgorithmName.SHA256,
                    DSASignatureFormat.Rfc3279DerSequence);
                finalManifest = finalManifest with { Signature = Convert.ToBase64String(signature) };
            }

            var json = JsonSerializer.Serialize(finalManifest, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(partialManifest, json);
            File.Move(partialManifest, manifestPath);
            return new BackupReport(backupPath, manifestPath, finalManifest);
        }
        catch
        {
            DeleteIfExists(partialPath);
            DeleteIfExists(partialManifest);
            DeleteIfExists(backupPath);
            DeleteIfExists(manifestPath);
            throw;
        }
    }

    private static void CopyOnline(string sourcePath, string destinationPath)
    {
        Exception? lastError = null;
        for (var attempt = 1; attempt <= 40; attempt++)
        {
            DeleteIfExists(destinationPath);
            try
            {
                using var source = new SqliteConnection(new SqliteConnectionStringBuilder
                {
                    DataSource = sourcePath,
                    Mode = SqliteOpenMode.ReadOnly,
                    Pooling = false,
                    DefaultTimeout = 30
                }.ToString());
                using var destination = new SqliteConnection(new SqliteConnectionStringBuilder
                {
                    DataSource = destinationPath,
                    Mode = SqliteOpenMode.ReadWriteCreate,
                    Pooling = false,
                    DefaultTimeout = 30
                }.ToString());
                source.Open();
                destination.Open();
                using (var busy = source.CreateCommand())
                {
                    busy.CommandText = "PRAGMA busy_timeout = 30000;";
                    busy.ExecuteNonQuery();
                }

                using (var busy = destination.CreateCommand())
                {
                    busy.CommandText = "PRAGMA busy_timeout = 30000;";
                    busy.ExecuteNonQuery();
                }

                source.BackupDatabase(destination);
                return;
            }
            catch (SqliteException error) when (error.SqliteErrorCode is 5 or 6)
            {
                lastError = error;
                System.Threading.Thread.Sleep(250);
            }
        }

        throw new IOException(
            $"online backup did not acquire a stable snapshot after 40 attempts: {lastError?.Message}",
            lastError);
    }

    public static string GenerateSigningKey(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        File.WriteAllText(fullPath, key.ExportPkcs8PrivateKeyPem(), new UTF8Encoding(false));
        return key.ExportSubjectPublicKeyInfoPem();
    }

    /// <summary>
    /// Repairs the target/previous pair left by a process kill between the two
    /// renames in <see cref="Replace"/>.
    /// </summary>
    public static void RecoverInterruptedRestore(string databasePath)
    {
        var target = Path.GetFullPath(databasePath);
        var previous = target + ".pre-restore";
        if (!File.Exists(previous))
        {
            return;
        }

        if (File.Exists(target))
        {
            File.Delete(previous);
        }
        else
        {
            File.Move(previous, target);
        }
    }

    public static RestoreReport Restore(string backupPath, string databasePath)
    {
        var sourceBackup = Path.GetFullPath(backupPath);
        var target = Path.GetFullPath(databasePath);
        RecoverInterruptedRestore(target);
        var manifestPath = sourceBackup + ".manifest.json";
        if (!File.Exists(sourceBackup))
        {
            throw new FileNotFoundException($"backup not found: {sourceBackup}");
        }

        if (!File.Exists(manifestPath))
        {
            throw new FileNotFoundException($"backup manifest not found: {manifestPath}");
        }

        if (string.Equals(sourceBackup, target, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Restore target must differ from the backup.");
        }

        var manifest = JsonSerializer.Deserialize<BackupManifest>(File.ReadAllText(manifestPath))
            ?? throw new InvalidDataException("backup manifest is empty.");
        if (!string.Equals(manifest.Format, Format, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"unsupported backup format: {manifest.Format}");
        }

        ValidateSignature(manifest);
        if (!string.Equals(HashFile(sourceBackup), manifest.DatabaseSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("backup database hash does not match the manifest.");
        }

        ValidateDatabase(sourceBackup, manifest);
        var targetDirectory = Path.GetDirectoryName(target);
        if (!string.IsNullOrEmpty(targetDirectory))
        {
            Directory.CreateDirectory(targetDirectory);
        }

        var temporary = target + ".restore-partial";
        try
        {
            File.Copy(sourceBackup, temporary, overwrite: true);
            if (!string.Equals(HashFile(temporary), manifest.DatabaseSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("restored copy hash does not match the manifest.");
            }

            Replace(temporary, target);
            return new RestoreReport(sourceBackup, target, manifest);
        }
        finally
        {
            DeleteIfExists(temporary);
        }
    }

    private static BackupManifest ReadManifestValues(string path, DateTimeOffset now)
    {
        using var store = new SqliteEventStore(path, readOnly: true);
        var (counts, bookmark) = store.GetStatus(SysmonXmlReader.SysmonChannel);
        var contract = store.GetMeta("contract_version")
            ?? throw new InvalidDataException("backup has no contract_version.");
        var ocsf = store.GetMeta("ocsf_version")
            ?? throw new InvalidDataException("backup has no ocsf_version.");
        var engine = store.GetMeta("engine_version") ?? string.Empty;
        return new BackupManifest(
            Format,
            string.Empty,
            0,
            contract,
            ocsf,
            engine,
            counts,
            bookmark?.SourceId,
            bookmark?.IncarnationId,
            bookmark?.EventRecordId,
            bookmark?.CoverageStartRecordId,
            bookmark?.SensorSequence,
            now);
    }

    private static void ValidateSignature(BackupManifest manifest)
    {
        if (manifest.SigningPublicKeyPem is null && manifest.Signature is null)
        {
            return;
        }

        if (manifest.SigningPublicKeyPem is null || manifest.Signature is null)
        {
            throw new InvalidDataException("backup manifest has an incomplete signature.");
        }

        byte[] signature;
        try
        {
            signature = Convert.FromBase64String(manifest.Signature);
        }
        catch (FormatException ex)
        {
            throw new InvalidDataException("backup manifest signature is not valid base64.", ex);
        }

        using var key = ECDsa.Create();
        key.ImportFromPem(manifest.SigningPublicKeyPem);
        var unsigned = manifest with { Signature = null };
        var payload = Encoding.UTF8.GetBytes(CanonicalJson.Serialize(unsigned));
        if (!key.VerifyData(
                payload,
                signature,
                HashAlgorithmName.SHA256,
                DSASignatureFormat.Rfc3279DerSequence))
        {
            throw new InvalidDataException("backup manifest signature does not match.");
        }
    }

    private static void ValidateDatabase(string path, BackupManifest manifest)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        connection.Open();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA integrity_check;";
            using var reader = command.ExecuteReader();
            var rows = 0;
            while (reader.Read())
            {
                rows++;
                if (!string.Equals(reader.GetString(0), "ok", StringComparison.Ordinal))
                {
                    throw new InvalidDataException($"backup integrity check failed: {reader.GetString(0)}");
                }
            }

            if (rows != 1)
            {
                throw new InvalidDataException("backup integrity check returned an unexpected result.");
            }
        }

        using var store = new SqliteEventStore(path, readOnly: true);
        var (counts, _) = store.GetStatus(SysmonXmlReader.SysmonChannel);
        if (counts != manifest.Counts)
        {
            throw new InvalidDataException("backup row counts do not match the manifest.");
        }

        if (!string.Equals(store.GetMeta("contract_version"), manifest.ContractVersion, StringComparison.Ordinal) ||
            !string.Equals(store.GetMeta("ocsf_version"), manifest.OcsfVersion, StringComparison.Ordinal) ||
            !string.Equals(store.GetMeta("engine_version"), manifest.EngineVersion, StringComparison.Ordinal))
        {
            throw new InvalidDataException("backup schema metadata does not match the manifest.");
        }

        if (!string.Equals(manifest.ContractVersion, SentinelVersions.ContractVersion, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"backup contract '{manifest.ContractVersion}' is incompatible with '{SentinelVersions.ContractVersion}'.");
        }
    }

    private static void Replace(string source, string target)
    {
        if (!File.Exists(target))
        {
            File.Move(source, target);
            return;
        }

        var previous = target + ".pre-restore";
        DeleteIfExists(previous);
        File.Move(target, previous);
        try
        {
            File.Move(source, target);
            File.Delete(previous);
        }
        catch
        {
            if (File.Exists(target))
            {
                File.Delete(target);
            }

            File.Move(previous, target);
            throw;
        }
    }

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
