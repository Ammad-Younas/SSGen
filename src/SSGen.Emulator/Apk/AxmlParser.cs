using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace SSGen.Emulator.Apk;

/// <summary>
/// Minimal decoder for Android binary XML (AXML), used to read AndroidManifest.xml when aapt2 isn't available.
/// Attributes are matched by framework resource id when present, because release builds often strip names.
/// </summary>
public static class AxmlParser
{
    public sealed record XmlAttr(string Name, uint ResourceId, string? StringValue, int DataType, uint Data)
    {
        public const int TypeReference = 0x01;
        public const int TypeString = 0x03;
        public const int TypeIntDec = 0x10;
        public const int TypeIntHex = 0x11;
        public const int TypeBoolean = 0x12;

        /// <summary>The value as text: literal strings as-is, integers in decimal, references as @0x7f…</summary>
        public string? AsText() => DataType switch
        {
            TypeString => StringValue,
            TypeIntDec or TypeIntHex => ((int)Data).ToString(CultureInfo.InvariantCulture),
            TypeBoolean => (Data != 0).ToString(CultureInfo.InvariantCulture),
            TypeReference => $"@0x{Data:x8}",
            _ => StringValue ?? Data.ToString(CultureInfo.InvariantCulture),
        };
    }

    public sealed record Element(string Name, int Depth, IReadOnlyList<XmlAttr> Attributes)
    {
        public XmlAttr? Find(uint resourceId, string name) =>
            Attributes.FirstOrDefault(a => resourceId != 0 && a.ResourceId == resourceId) ??
            Attributes.FirstOrDefault(a => a.Name == name);
    }

    private const ushort ChunkStringPool = 0x0001;
    private const ushort ChunkXml = 0x0003;
    private const ushort ChunkResourceMap = 0x0180;
    private const ushort ChunkStartElement = 0x0102;
    private const ushort ChunkEndElement = 0x0103;

    /// <summary>Returns start elements in document order with their nesting depth.</summary>
    public static IReadOnlyList<Element> Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < 8 || BinaryPrimitives.ReadUInt16LittleEndian(data) != ChunkXml)
        {
            throw new FormatException("Not an Android binary XML file.");
        }

        var headerSize = BinaryPrimitives.ReadUInt16LittleEndian(data[2..]);
        var strings = Array.Empty<string>();
        var resourceIds = Array.Empty<uint>();
        var elements = new List<Element>();
        var depth = 0;
        var pos = (int)headerSize;

        while (pos + 8 <= data.Length)
        {
            var type = BinaryPrimitives.ReadUInt16LittleEndian(data[pos..]);
            var chunkHeader = BinaryPrimitives.ReadUInt16LittleEndian(data[(pos + 2)..]);
            var size = (int)BinaryPrimitives.ReadUInt32LittleEndian(data[(pos + 4)..]);
            if (size < 8 || pos + size > data.Length)
            {
                throw new FormatException($"Corrupt chunk at offset {pos}.");
            }

            var chunk = data.Slice(pos, size);
            switch (type)
            {
                case ChunkStringPool:
                    strings = ReadStringPool(chunk);
                    break;
                case ChunkResourceMap:
                    resourceIds = new uint[(size - chunkHeader) / 4];
                    for (var i = 0; i < resourceIds.Length; i++)
                    {
                        resourceIds[i] = BinaryPrimitives.ReadUInt32LittleEndian(chunk[(chunkHeader + (i * 4))..]);
                    }

                    break;
                case ChunkStartElement:
                    elements.Add(ReadStartElement(chunk, chunkHeader, strings, resourceIds, depth));
                    depth++;
                    break;
                case ChunkEndElement:
                    depth--;
                    break;
            }

            pos += size;
        }

        return elements;
    }

    private static Element ReadStartElement(ReadOnlySpan<byte> chunk, int headerSize, string[] strings, uint[] resourceIds, int depth)
    {
        var ext = chunk[headerSize..];
        var name = Str(strings, BinaryPrimitives.ReadUInt32LittleEndian(ext[4..]));
        var attrStart = BinaryPrimitives.ReadUInt16LittleEndian(ext[8..]);
        var attrSize = BinaryPrimitives.ReadUInt16LittleEndian(ext[10..]);
        var attrCount = BinaryPrimitives.ReadUInt16LittleEndian(ext[12..]);
        var attrs = new List<XmlAttr>(attrCount);
        for (var i = 0; i < attrCount; i++)
        {
            var a = ext[(attrStart + (i * attrSize))..];
            var nameIndex = BinaryPrimitives.ReadUInt32LittleEndian(a[4..]);
            var rawValue = BinaryPrimitives.ReadUInt32LittleEndian(a[8..]);
            var dataType = a[15];
            var dataValue = BinaryPrimitives.ReadUInt32LittleEndian(a[16..]);
            var resId = nameIndex < resourceIds.Length ? resourceIds[nameIndex] : 0u;
            var str = rawValue != uint.MaxValue ? Str(strings, rawValue) : dataType == XmlAttr.TypeString ? Str(strings, dataValue) : null;
            attrs.Add(new XmlAttr(Str(strings, nameIndex), resId, str, dataType, dataValue));
        }

        return new Element(name, depth, attrs);
    }

    private static string Str(string[] strings, uint index) => index < strings.Length ? strings[index] : string.Empty;

    private static string[] ReadStringPool(ReadOnlySpan<byte> chunk)
    {
        var count = (int)BinaryPrimitives.ReadUInt32LittleEndian(chunk[8..]);
        var flags = BinaryPrimitives.ReadUInt32LittleEndian(chunk[16..]);
        var stringsStart = (int)BinaryPrimitives.ReadUInt32LittleEndian(chunk[20..]);
        var utf8 = (flags & (1 << 8)) != 0;
        var headerSize = BinaryPrimitives.ReadUInt16LittleEndian(chunk[2..]);
        var result = new string[count];
        for (var i = 0; i < count; i++)
        {
            var offset = stringsStart + (int)BinaryPrimitives.ReadUInt32LittleEndian(chunk[(headerSize + (i * 4))..]);
            result[i] = utf8 ? ReadUtf8(chunk[offset..]) : ReadUtf16(chunk[offset..]);
        }

        return result;
    }

    private static string ReadUtf16(ReadOnlySpan<byte> s)
    {
        int len = BinaryPrimitives.ReadUInt16LittleEndian(s);
        var skip = 2;
        if ((len & 0x8000) != 0)
        {
            len = ((len & 0x7FFF) << 16) | BinaryPrimitives.ReadUInt16LittleEndian(s[2..]);
            skip = 4;
        }

        return Encoding.Unicode.GetString(s.Slice(skip, len * 2));
    }

    private static string ReadUtf8(ReadOnlySpan<byte> s)
    {
        // UTF-16 length (1 or 2 bytes, ignored) followed by UTF-8 byte length (1 or 2 bytes).
        var p = (s[0] & 0x80) != 0 ? 2 : 1;
        int len = s[p];
        p++;
        if ((len & 0x80) != 0)
        {
            len = ((len & 0x7F) << 8) | s[p];
            p++;
        }

        return Encoding.UTF8.GetString(s.Slice(p, len));
    }
}

/// <summary>Builds <see cref="ApkInfo"/> from a binary AndroidManifest.xml.</summary>
public static class ManifestReader
{
    // Framework attribute resource ids (android.R.attr).
    private const uint AttrLabel = 0x01010001;
    private const uint AttrName = 0x01010003;
    private const uint AttrVersionCode = 0x0101021b;
    private const uint AttrVersionName = 0x0101021c;
    private const uint AttrMinSdk = 0x0101020c;
    private const uint AttrTargetSdk = 0x01010270;

    public static ApkInfo Read(ReadOnlySpan<byte> manifest, IReadOnlyList<string> nativeAbis)
    {
        var elements = AxmlParser.Parse(manifest);
        var root = elements.FirstOrDefault(e => e.Name == "manifest") ?? throw new FormatException("No <manifest> element.");
        var package = root.Find(0, "package")?.AsText() ?? throw new FormatException("The manifest has no package name.");
        var usesSdk = elements.FirstOrDefault(e => e.Name == "uses-sdk");
        var application = elements.FirstOrDefault(e => e.Name == "application");
        var label = application?.Find(AttrLabel, "label");

        return new ApkInfo
        {
            PackageName = package,
            Label = label?.DataType == AxmlParser.XmlAttr.TypeString ? label.StringValue : null,
            VersionCode = long.TryParse(root.Find(AttrVersionCode, "versionCode")?.AsText(), CultureInfo.InvariantCulture, out var vc) ? vc : null,
            VersionName = root.Find(AttrVersionName, "versionName")?.AsText(),
            MinSdk = int.TryParse(usesSdk?.Find(AttrMinSdk, "minSdkVersion")?.AsText(), CultureInfo.InvariantCulture, out var min) ? min : null,
            TargetSdk = int.TryParse(usesSdk?.Find(AttrTargetSdk, "targetSdkVersion")?.AsText(), CultureInfo.InvariantCulture, out var target) ? target : null,
            LauncherActivity = FindLauncher(elements, package),
            Permissions = elements.Where(e => e.Name == "uses-permission")
                .Select(e => e.Find(AttrName, "name")?.AsText())
                .OfType<string>()
                .Distinct()
                .ToList(),
            NativeAbis = nativeAbis,
            RequiresSplits = root.Find(0, "isSplitRequired") is { DataType: AxmlParser.XmlAttr.TypeBoolean, Data: not 0 },
            ParsedBy = "managed",
        };
    }

    /// <summary>The first activity (or alias) with an intent-filter containing MAIN + LAUNCHER.</summary>
    private static string? FindLauncher(IReadOnlyList<AxmlParser.Element> elements, string package)
    {
        for (var i = 0; i < elements.Count; i++)
        {
            var e = elements[i];
            if (e.Name is not ("activity" or "activity-alias"))
            {
                continue;
            }

            bool main = false, launcher = false;
            for (var j = i + 1; j < elements.Count && elements[j].Depth > e.Depth; j++)
            {
                var child = elements[j];
                var value = child.Find(AttrName, "name")?.AsText();
                main |= child.Name == "action" && value == "android.intent.action.MAIN";
                launcher |= child.Name == "category" && value == "android.intent.category.LAUNCHER";
            }

            if (main && launcher && e.Find(AttrName, "name")?.AsText() is { } name)
            {
                return name.StartsWith('.') ? package + name : name.Contains('.', StringComparison.Ordinal) ? name : package + "." + name;
            }
        }

        return null;
    }
}
