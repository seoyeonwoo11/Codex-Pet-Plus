using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace CodexPetPlus.Core;

public sealed record AsarPatchResult(string OriginalHeaderHash, string PatchedHeaderHash, string MainEntry);

public static class AsarModPatcher
{
    private const string RuntimeName = "codex-pet-plus-runtime.cjs";
    private const string Marker = "/* CODEX_PET_PLUS_MOD_0_4 */";
    private const int BlockSize = 4 * 1024 * 1024;
    private sealed record Entry(string Name, JsonObject Node, long Size, long Offset, bool Packed);

    public static AsarPatchResult Patch(string sourceFile, string destinationFile, string runtime)
    {
        if (Path.GetFullPath(sourceFile).Equals(Path.GetFullPath(destinationFile), StringComparison.OrdinalIgnoreCase))
            throw new IOException("앱 원본과 다른 위치에 복사본을 만들어야 합니다.");
        using var source = File.OpenRead(sourceFile);
        var prefix = new byte[16]; source.ReadExactly(prefix);
        var headerSize = BinaryPrimitives.ReadUInt32LittleEndian(prefix.AsSpan(4));
        var jsonSize = BinaryPrimitives.ReadUInt32LittleEndian(prefix.AsSpan(12));
        if (BinaryPrimitives.ReadUInt32LittleEndian(prefix) != 4 || headerSize is < 8 or > 32 * 1024 * 1024
            || jsonSize > headerSize - 8 || BinaryPrimitives.ReadUInt32LittleEndian(prefix.AsSpan(8)) != headerSize - 4)
            throw new IOException("지원하는 Electron 앱 패키지 형식이 아닙니다.");
        var dataOffset = 8L + headerSize;
        if (dataOffset > source.Length) throw new IOException("앱 패키지가 잘렸습니다.");
        var headerBytes = new byte[jsonSize]; source.ReadExactly(headerBytes);
        var root = JsonNode.Parse(headerBytes)?.AsObject() ?? throw new IOException("앱 패키지 헤더를 읽을 수 없습니다.");
        var files = root["files"]?.AsObject() ?? throw new IOException("앱 패키지 파일 목록이 없습니다.");
        var entries = new List<Entry>();
        Walk(files, "", entries, 0);
        if (entries.Any(entry => entry.Name == RuntimeName)) throw new IOException("이미 모드가 적용된 복사본입니다. 공식 앱 원본을 선택해 주세요.");
        var package = entries.SingleOrDefault(entry => entry.Name == "package.json") ?? throw new IOException("앱 메타데이터가 없습니다.");
        var metadata = JsonNode.Parse(ReadEntry(source, package, dataOffset, 1024 * 1024))?.AsObject() ?? throw new IOException("앱 메타데이터를 읽을 수 없습니다.");
        var mainName = Normalize(metadata["main"]?.GetValue<string>() ?? throw new IOException("앱 시작 파일이 없습니다."));
        var main = entries.SingleOrDefault(entry => entry.Name == mainName) ?? throw new IOException("앱 시작 파일을 찾지 못했습니다.");
        if (!entries.Any(entry => entry.Name.Contains("avatar-overlay", StringComparison.OrdinalIgnoreCase)
            || entry.Name.Contains("pet-overlay", StringComparison.OrdinalIgnoreCase) || entry.Name.Contains("mascot-overlay", StringComparison.OrdinalIgnoreCase)))
            throw new IOException("이 앱에서 지원하는 펫 화면을 확인하지 못했습니다. 앱 파일을 수정하지 않았습니다.");
        var text = Encoding.UTF8.GetString(ReadEntry(source, main, dataOffset, 32 * 1024 * 1024));
        if (text.Contains(Marker, StringComparison.Ordinal)) throw new IOException("이미 모드가 적용된 복사본입니다.");
        var mainDirectory = Path.GetDirectoryName(mainName);
        var modulePath = Path.GetRelativePath(string.IsNullOrEmpty(mainDirectory) ? "." : mainDirectory, RuntimeName).Replace('\\', '/');
        if (!modulePath.StartsWith('.')) modulePath = "./" + modulePath;
        var quotedModule = System.Text.Json.JsonSerializer.Serialize(modulePath);
        var esm = mainName.EndsWith(".mjs", StringComparison.OrdinalIgnoreCase)
            || (!mainName.EndsWith(".cjs", StringComparison.OrdinalIgnoreCase) && metadata["type"]?.GetValue<string>() == "module");
        var loader = esm ? "import " + quotedModule + ";" : "try { require(" + quotedModule + "); } catch {}";
        var mainBytes = Encoding.UTF8.GetBytes(text + "\n;" + Marker + "\n" + loader + "\n");
        var runtimeBytes = Encoding.UTF8.GetBytes(runtime);
        if (runtimeBytes.Length is 0 or > 1024 * 1024) throw new IOException("모드 파일의 크기가 올바르지 않습니다.");
        var runtimeNode = new JsonObject(); files[RuntimeName] = runtimeNode;
        entries.Add(new(RuntimeName, runtimeNode, 0, 0, true));
        long outputOffset = 0;
        foreach (var entry in entries.Where(entry => entry.Packed))
        {
            var replacement = entry.Name == mainName ? mainBytes : entry.Name == RuntimeName ? runtimeBytes : null;
            Validate(entry, source.Length, dataOffset);
            entry.Node["offset"] = outputOffset.ToString(System.Globalization.CultureInfo.InvariantCulture);
            entry.Node["size"] = replacement?.Length ?? entry.Size;
            if (replacement is not null) entry.Node["integrity"] = Integrity(replacement);
            outputOffset = checked(outputOffset + (replacement?.LongLength ?? entry.Size));
        }
        var nextHeader = Encoding.UTF8.GetBytes(root.ToJsonString());
        if (nextHeader.Length > 32 * 1024 * 1024) throw new IOException("앱 패키지 헤더가 너무 큽니다.");
        var temporary = destinationFile + ".partial-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write))
            {
                WriteHeader(output, nextHeader);
                foreach (var entry in entries.Where(entry => entry.Packed))
                {
                    if (entry.Name == mainName) output.Write(mainBytes);
                    else if (entry.Name == RuntimeName) output.Write(runtimeBytes);
                    else { source.Position = checked(dataOffset + entry.Offset); CopyExactly(source, output, entry.Size); }
                }
            }
            File.Move(temporary, destinationFile, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return new(Hash(headerBytes), Hash(nextHeader), mainName);
    }

    public static void UpdateExecutableIntegrity(string executable, string originalHeaderHash, string patchedHeaderHash)
    {
        if (originalHeaderHash.Length != 64 || patchedHeaderHash.Length != 64) throw new IOException("앱 무결성 해시가 올바르지 않습니다.");
        var length = new FileInfo(executable).Length;
        if (length is < 2 or > 512 * 1024 * 1024) throw new IOException("앱 실행 파일의 크기가 올바르지 않습니다.");
        var bytes = File.ReadAllBytes(executable);
        if (bytes[0] != 'M' || bytes[1] != 'Z') throw new IOException("Windows 앱 실행 파일이 아닙니다.");
        var marker = Encoding.ASCII.GetBytes("\"file\":\"resources\\\\app.asar\",\"alg\":\"SHA256\",\"value\":\"");
        var positions = new List<int>();
        var search = bytes.AsSpan(); var offset = 0;
        while (search.IndexOf(marker) is var index && index >= 0)
        {
            var position = offset + index + marker.Length;
            if (position + 64 >= bytes.Length || bytes[position + 64] != '"'
                || !Encoding.ASCII.GetString(bytes, position, 64).Equals(originalHeaderHash, StringComparison.OrdinalIgnoreCase))
                throw new IOException("공식 앱의 패키지 무결성 정보가 일치하지 않습니다. 설치를 중단했습니다.");
            positions.Add(position); offset = position + 64; search = bytes.AsSpan(offset);
        }
        if (positions.Count == 0) throw new IOException("이 실행 파일의 Electron 패키지 무결성 형식을 지원하지 않습니다.");
        foreach (var position in positions) Encoding.ASCII.GetBytes(patchedHeaderHash.AsSpan(), bytes.AsSpan(position, 64));
        File.WriteAllBytes(executable, bytes);
    }

    private static string Normalize(string name)
    {
        var parts = name.Replace('\\', '/').Split('/').Where(part => part != ".").ToArray();
        if (parts.Length == 0 || parts.Any(part => part.Length == 0 || part == ".." || part.Contains(':')))
            throw new IOException("지원하지 않는 앱 시작 경로입니다.");
        return string.Join('/', parts);
    }

    private static void Walk(JsonObject directory, string prefix, List<Entry> entries, int depth)
    {
        if (depth > 64 || entries.Count > 100000) throw new IOException("앱 패키지 파일 목록이 너무 큽니다.");
        foreach (var pair in directory)
        {
            var node = pair.Value?.AsObject() ?? throw new IOException("앱 패키지 파일 정보가 올바르지 않습니다.");
            var name = prefix + pair.Key;
            if (node["files"] is JsonObject children) { Walk(children, name + "/", entries, depth + 1); continue; }
            var packed = node["link"] is null && node["unpacked"]?.GetValue<bool>() != true;
            var size = packed ? node["size"]?.GetValue<long>() ?? 0 : 0;
            var offset = packed ? long.Parse(node["offset"]?.GetValue<string>() ?? "0", System.Globalization.CultureInfo.InvariantCulture) : 0;
            entries.Add(new(name, node, size, offset, packed));
        }
    }

    private static void Validate(Entry entry, long fileLength, long dataOffset)
    {
        if (entry.Size < 0 || entry.Offset < 0 || entry.Offset > fileLength - dataOffset || entry.Size > fileLength - dataOffset - entry.Offset)
            throw new IOException("앱 패키지 파일 범위가 올바르지 않습니다.");
    }

    private static byte[] ReadEntry(Stream source, Entry entry, long dataOffset, int maximum)
    {
        if (!entry.Packed || entry.Size > maximum) throw new IOException("지원하는 앱 시작 파일 형식이 아닙니다.");
        Validate(entry, source.Length, dataOffset);
        var data = new byte[(int)entry.Size]; source.Position = dataOffset + entry.Offset; source.ReadExactly(data); return data;
    }

    private static JsonObject Integrity(byte[] data)
    {
        var blocks = new JsonArray();
        for (var at = 0; at < data.Length; at += BlockSize) blocks.Add(Hash(data.AsSpan(at, Math.Min(BlockSize, data.Length - at))));
        return new() { ["algorithm"] = "SHA256", ["hash"] = Hash(data), ["blockSize"] = BlockSize, ["blocks"] = blocks };
    }
    private static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static void WriteHeader(Stream output, byte[] json)
    {
        var padding = (4 - json.Length % 4) % 4;
        using var writer = new BinaryWriter(output, Encoding.UTF8, true);
        writer.Write(4u); writer.Write((uint)(8 + json.Length + padding)); writer.Write((uint)(4 + json.Length + padding));
        writer.Write((uint)json.Length); writer.Write(json); writer.Write(new byte[padding]);
    }
    private static void CopyExactly(Stream input, Stream output, long remaining)
    {
        var buffer = new byte[128 * 1024];
        while (remaining > 0)
        {
            var read = input.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
            if (read == 0) throw new EndOfStreamException("앱 패키지가 잘렸습니다.");
            output.Write(buffer, 0, read); remaining -= read;
        }
    }
}
