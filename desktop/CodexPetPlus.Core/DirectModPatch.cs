using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace CodexPetPlus.Core;

public sealed class PreparedModPatch : IDisposable
{
    public string Executable { get; }
    public string Archive { get; }
    public string StagedExecutable { get; }
    public string StagedArchive { get; }
    public string BackupFile { get; }
    internal PreparedModPatch(string executable, string archive, string stagedExecutable, string stagedArchive, string backup)
    { Executable = executable; Archive = archive; StagedExecutable = stagedExecutable; StagedArchive = stagedArchive; BackupFile = backup; }

    public void Commit()
    {
        using var backupGuard = new FileStream(BackupFile, FileMode.Open, FileAccess.Read, FileShare.Read);
        DirectModPatch.VerifyPrepared(this);
        try
        {
            File.Replace(StagedArchive, Archive, null);
            File.Replace(StagedExecutable, Executable, null);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            try { DirectModPatch.Restore(Executable, BackupFile); }
            catch (Exception recoveryError) when (recoveryError is IOException or UnauthorizedAccessException or InvalidDataException)
            { throw new IOException("패치를 완료하지 못했습니다. 복원 데이터를 보존했습니다. 앱을 종료하고 ‘원본 복원’을 실행해 주세요.", new AggregateException(error, recoveryError)); }
            throw new IOException("패치를 완료하지 못해 원본 파일로 복원했습니다.", error);
        }
    }

    public void Dispose() { DirectModPatch.DeleteTemporary(StagedArchive); DirectModPatch.DeleteTemporary(StagedExecutable); }
}

public static class DirectModPatch
{
    private sealed record ExeSlice(long Offset, byte[] OriginalBytes);
    private sealed record BackupMetadata(int Format, string OriginalArchiveHash, string InstalledArchiveHash,
        string OriginalExecutableHash, string InstalledExecutableHash, string MainEntry, long OriginalArchiveLength, ExeSlice[] ExecutableChanges);
    private sealed record Backup(BackupMetadata Metadata, AsarRecoveryData Archive);

    public static PreparedModPatch Prepare(string executable, string backupFile, string runtime)
    {
        executable = Path.GetFullPath(executable);
        var archive = ArchiveOf(executable);
        var stagedExecutable = TemporaryFor(executable); var stagedArchive = TemporaryFor(archive);
        var backupCreated = false;
        try
        {
            var originalArchiveHash = HashFile(archive); var originalExecutableHash = HashFile(executable);
            var recovery = AsarModPatcher.CaptureRecovery(archive);
            var result = AsarModPatcher.Patch(archive, stagedArchive, runtime);
            File.Copy(executable, stagedExecutable);
            AsarModPatcher.UpdateExecutableIntegrity(stagedExecutable, result.OriginalHeaderHash, result.PatchedHeaderHash);
            using (var candidate = new FileStream(stagedArchive, FileMode.Open, FileAccess.ReadWrite)) candidate.Flush(true);
            using (var candidate = new FileStream(stagedExecutable, FileMode.Open, FileAccess.ReadWrite)) candidate.Flush(true);
            var changes = CaptureExecutableChanges(executable, stagedExecutable);
            RequireHash(archive, originalArchiveHash); RequireHash(executable, originalExecutableHash);
            var metadata = new BackupMetadata(1, originalArchiveHash, HashFile(stagedArchive), originalExecutableHash,
                HashFile(stagedExecutable), recovery.MainEntry, recovery.OriginalLength, changes);
            using (var file = new FileStream(backupFile, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
                backupCreated = true;
                using (var zip = new ZipArchive(file, ZipArchiveMode.Create, true))
                {
                    Write(zip, "metadata.json", JsonSerializer.SerializeToUtf8Bytes(metadata));
                    Write(zip, "asar-header.bin", recovery.Header); Write(zip, "asar-main.bin", recovery.Main);
                }
                file.Flush(true);
            }
            return new(executable, archive, stagedExecutable, stagedArchive, backupFile);
        }
        catch
        {
            DeleteTemporary(stagedArchive); DeleteTemporary(stagedExecutable);
            if (backupCreated) DeleteTemporary(backupFile);
            throw;
        }
    }

    internal static void VerifyPrepared(PreparedModPatch plan)
    {
        var metadata = Read(plan.BackupFile).Metadata;
        RequireHash(plan.Executable, metadata.OriginalExecutableHash); RequireHash(plan.Archive, metadata.OriginalArchiveHash);
        RequireHash(plan.StagedExecutable, metadata.InstalledExecutableHash); RequireHash(plan.StagedArchive, metadata.InstalledArchiveHash);
    }

    public static bool IsOriginal(string executable, string backupFile)
    {
        var metadata = Read(backupFile).Metadata;
        return HashFile(executable) == metadata.OriginalExecutableHash && HashFile(ArchiveOf(executable)) == metadata.OriginalArchiveHash;
    }

    public static void Restore(string executable, string backupFile)
    {
        using var backupGuard = new FileStream(backupFile, FileMode.Open, FileAccess.Read, FileShare.Read);
        var backup = Read(backupFile); var metadata = backup.Metadata;
        var archive = ArchiveOf(executable);
        var archiveHash = HashFile(archive); var exeHash = HashFile(executable);
        if ((archiveHash != metadata.OriginalArchiveHash && archiveHash != metadata.InstalledArchiveHash)
            || (exeHash != metadata.OriginalExecutableHash && exeHash != metadata.InstalledExecutableHash))
            throw new IOException("앱이 업데이트됐거나 다른 수정이 있어 복원을 중단했습니다. 현재 앱 파일을 덮어쓰지 않았습니다.");
        var stagedArchive = TemporaryFor(archive); var stagedExecutable = TemporaryFor(executable);
        try
        {
            if (archiveHash != metadata.OriginalArchiveHash)
            {
                AsarModPatcher.RestoreArchive(archive, stagedArchive, backup.Archive);
                RequireHash(stagedArchive, metadata.OriginalArchiveHash);
            }
            if (exeHash != metadata.OriginalExecutableHash)
            {
                File.Copy(executable, stagedExecutable);
                using (var file = new FileStream(stagedExecutable, FileMode.Open, FileAccess.Write))
                {
                    foreach (var change in metadata.ExecutableChanges) { file.Position = change.Offset; file.Write(change.OriginalBytes); }
                    file.Flush(true);
                }
                RequireHash(stagedExecutable, metadata.OriginalExecutableHash);
            }
            // Verify both files before either replacement; updates and other mods must survive.
            RequireHash(archive, archiveHash); RequireHash(executable, exeHash);
            if (File.Exists(stagedArchive)) File.Replace(stagedArchive, archive, null);
            if (File.Exists(stagedExecutable)) File.Replace(stagedExecutable, executable, null);
        }
        finally { DeleteTemporary(stagedArchive); DeleteTemporary(stagedExecutable); }
    }

    private static ExeSlice[] CaptureExecutableChanges(string original, string installed)
    {
        using var before = File.OpenRead(original); using var after = File.OpenRead(installed);
        if (before.Length != after.Length) throw new IOException("실행 파일 크기가 바뀌어 부분 백업을 만들 수 없습니다.");
        var oldBytes = new byte[64 * 1024]; var newBytes = new byte[oldBytes.Length];
        var changes = new List<ExeSlice>(); long offset = 0; var savedBytes = 0;
        while (offset < before.Length)
        {
            var count = (int)Math.Min(oldBytes.Length, before.Length - offset);
            before.ReadExactly(oldBytes.AsSpan(0, count)); after.ReadExactly(newBytes.AsSpan(0, count));
            for (var index = 0; index < count;)
            {
                if (oldBytes[index] == newBytes[index]) { index++; continue; }
                var start = index; while (index < count && oldBytes[index] != newBytes[index]) index++;
                savedBytes += index - start;
                if (savedBytes > 8192 || changes.Count >= 8192) throw new IOException("실행 파일 변경 범위가 부분 백업 제한을 초과했습니다.");
                changes.Add(new(offset + start, oldBytes[start..index]));
            }
            offset += count;
        }
        return changes.ToArray();
    }

    private static Backup Read(string file)
    {
        try { return ReadBackup(file); }
        catch (Exception error) when (error is InvalidDataException or JsonException or InvalidOperationException or ArgumentException or OverflowException)
        { throw new IOException("복원 데이터가 손상됐거나 지원하지 않는 형식입니다.", error); }
    }

    private static Backup ReadBackup(string file)
    {
        using var zip = ZipFile.OpenRead(file);
        if (zip.Entries.Count != 3) throw new IOException("복원 파일 형식이 올바르지 않습니다.");
        var metadata = JsonSerializer.Deserialize<BackupMetadata>(ReadEntry(zip, "metadata.json", 512 * 1024)) ?? throw new IOException("복원 정보가 없습니다.");
        var hashes = new[] { metadata.OriginalArchiveHash, metadata.InstalledArchiveHash, metadata.OriginalExecutableHash, metadata.InstalledExecutableHash };
        if (metadata.Format != 1 || hashes.Any(value => value is null || value.Length != 64 || value.Any(character => !Uri.IsHexDigit(character)))
            || metadata.OriginalArchiveLength <= 0 || metadata.ExecutableChanges is null || metadata.ExecutableChanges.Length > 8192)
            throw new IOException("복원 정보가 올바르지 않습니다.");
        var saved = 0; long end = 0;
        foreach (var change in metadata.ExecutableChanges)
        {
            if (change is null || change.OriginalBytes is null || change.OriginalBytes.Length == 0 || change.Offset < end || change.Offset > 512 * 1024 * 1024)
                throw new IOException("실행 파일 복원 범위가 올바르지 않습니다.");
            saved = checked(saved + change.OriginalBytes.Length); end = checked(change.Offset + change.OriginalBytes.Length);
            if (saved > 8192) throw new IOException("실행 파일 복원 범위가 너무 큽니다.");
        }
        return new(metadata, new(metadata.MainEntry, metadata.OriginalArchiveLength,
            ReadEntry(zip, "asar-header.bin", 32 * 1024 * 1024 + 16), ReadEntry(zip, "asar-main.bin", 32 * 1024 * 1024)));
    }

    private static byte[] ReadEntry(ZipArchive archive, string name, int maximum)
    {
        var entry = archive.GetEntry(name) ?? throw new IOException("복원 파일이 빠져 있습니다.");
        if (entry.Length > maximum) throw new IOException("복원 데이터 크기가 너무 큽니다.");
        using var stream = entry.Open(); var bytes = new byte[(int)entry.Length]; stream.ReadExactly(bytes); return bytes;
    }
    private static void Write(ZipArchive archive, string name, byte[] data)
    { using var stream = archive.CreateEntry(name, CompressionLevel.SmallestSize).Open(); stream.Write(data); }
    private static string ArchiveOf(string executable) => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(executable))!, "resources", "app.asar");
    private static string TemporaryFor(string target) => Path.Combine(Path.GetDirectoryName(target)!, "." + Path.GetFileName(target) + ".pet-plus-" + Guid.NewGuid().ToString("N") + ".tmp");
    private static string HashFile(string file) { using var input = File.OpenRead(file); return Convert.ToHexStringLower(SHA256.HashData(input)); }
    private static void RequireHash(string file, string expected)
    { if (HashFile(file) != expected) throw new IOException("앱 파일이 변경돼 패치를 중단했습니다."); }
    internal static void DeleteTemporary(string file)
    {
        try
        {
            if (!File.Exists(file)) return;
            File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);
            File.Delete(file);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }
}
