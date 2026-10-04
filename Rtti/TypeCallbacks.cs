using BinaryReader = OdradekSharp.Io.BinaryReader;

namespace OdradekSharp.Rtti;

/// <summary>
/// Port of the 14 callbacks registered by odradek-game-ds2 (module-info.java:52-67).
///
/// Each callback consumes the object's trailing "MsgReadBinary" bytes and writes the results into the
/// fields of the type's *extension* (extensions.json), whose names and types are fixed there — e.g.
/// Texture -> TextureInfo{Header, Data}, ShaderResource -> ShaderResourceExtension{Size, Unk04, Unk14,
/// Unk24}, LocalizedTextResource -> LocalizedTextResourceExtension{Texts}. Using those exact fields is
/// what makes the exported JSON match odradek's: its JSON walks serializedAttrs(), which includes the
/// extension attributes.
/// </summary>
public static class TypeCallbacks
{
    public static void Read(RttiReader reader, BinaryReader r, TypedObject obj, string declaring)
    {
        // odradek checks `target instanceof ExtraBinaryDataHolder`, and a generated derived interface
        // extends its base's interface — so a type inherits its ancestor's MsgReadBinary callback.
        switch (declaring)
        {
            case "DataBufferResource": DataBufferResource(reader, r, obj); break;
            case "DebugMouseCursorPS4": DebugMouseCursorPS4(reader, r, obj); break;
            case "IndexArrayResource": IndexArrayResource(reader, r, obj); break;
            case "LocalizedTextResource": LocalizedTextResource(reader, r, obj); break;
            case "ShaderResource": ShaderResource(reader, r, obj); break;
            case "Texture": Texture(reader, r, obj); break;
            case "TextureList": TextureList(reader, r, obj); break;
            case "UITexture": UITexture(reader, r, obj); break;
            case "UITextureFrames": UITextureFrames(reader, r, obj); break;
            case "VertexArrayResource": VertexArrayResource(reader, r, obj); break;
            case "ZivaRTResource": ZivaRTResource(reader, r, obj); break;
            case "PhysicsShapeResource": Jolt.ReadPhysicsShapeResource(r); break;
            case "PhysicsRagdollResource": Jolt.ReadPhysicsRagdollResource(r); break;
            case "FacialRigSettingWithLODResource": RigLogic.Read(r); break;
            default:
                throw new NotSupportedException(
                    $"Missing callback for '{declaring}' (object {obj.Type.Name}) " +
                    $"required to read extra data at position {r.Position}");
        }
    }

    // ---- helpers -------------------------------------------------------------------------------

    private static TypedObject New(RttiReader reader, string typeName) =>
        new() { Type = (ClassTypeInfo)reader.Factory.Resolve(typeName) };

    private static EnumValue Enum(RttiReader reader, string typeName, long value) =>
        new((EnumTypeInfo)reader.Factory.Resolve(typeName), value);

    /// <summary>GGUUID / MurmurHashValue: 16 × uint8 read as Data0..Data15.</summary>
    private static TypedObject Blob(RttiReader reader, string typeName, BinaryReader r)
    {
        var o = New(reader, typeName);
        for (var i = 0; i < 16; i++) o.Fields["Data" + i] = (sbyte)r.ReadByte();
        return o;
    }

    /// <summary>
    /// A nested compound read the way odradek's callbacks do it: a plain DS2TypeReader, i.e. without
    /// streaming hooks (no locator/link resolution).
    /// </summary>
    private static TypedObject Nested(RttiReader reader, BinaryReader r, string typeName)
    {
        var sub = new RttiReader(reader.Factory) { Lenient = reader.Lenient, Context = null };
        return sub.ReadCompound((ClassTypeInfo)reader.Factory.Resolve(typeName), r);
    }

    private static byte[] Payload(RttiReader reader, BinaryReader r, int length)
    {
        if (!reader.CollectPayload)
        {
            r.Skip(length);
            return [];
        }
        return r.ReadBytes(length);
    }

    private static List<string> WrittenLanguages(RttiReader reader)
    {
        var en = (EnumTypeInfo)reader.Factory.Resolve("ELanguage");
        return en.Values
            .Where(v => !string.Equals(v.Name, "Unknown", StringComparison.Ordinal))
            .OrderBy(v => v.Value)
            .Select(v => v.Name)
            .ToList();
    }

    // ---- callbacks (order and byte counts follow callbacks/*.java) ------------------------------

    private static void DataBufferResource(RttiReader reader, BinaryReader r, TypedObject obj)
    {
        var count = r.ReadInt();
        obj.Fields["Count"] = count;
        if (count == 0) return;
        var isStreaming = r.ReadInt() != 0;
        obj.Fields["IsStreaming"] = isStreaming;
        obj.Fields["Flags"] = r.ReadInt();
        obj.Fields["Format"] = Enum(reader, "EDataBufferFormat", r.ReadInt());
        var stride = r.ReadInt();
        obj.Fields["Stride"] = stride;
        if (!isStreaming) obj.Fields["Data"] = Payload(reader, r, stride * count);
    }

    private static void DebugMouseCursorPS4(RttiReader reader, BinaryReader r, TypedObject obj)
    {
        obj.Fields["Stride"] = r.ReadInt();
        obj.Fields["Data"] = Payload(reader, r, r.ReadInt());
    }

    private static void IndexArrayResource(RttiReader reader, BinaryReader r, TypedObject obj)
    {
        var count = r.ReadInt();
        obj.Fields["Count"] = count;
        obj.Fields["Flags"] = r.ReadInt();
        var format = r.ReadInt();
        obj.Fields["Format"] = Enum(reader, "EIndexFormat", format);
        var isStreaming = r.ReadInt() != 0;
        obj.Fields["Checksum"] = Blob(reader, "MurmurHashValue", r);
        obj.Fields["IsStreaming"] = isStreaming;
        if (!isStreaming)
        {
            // EIndexFormatExtension.stride(): Index16 (0) -> 2, Index32 (1) -> 4
            var stride = format switch { 0 => 2, 1 => 4, _ => throw new NotSupportedException($"Unexpected index format {format}") };
            obj.Fields["Data"] = Payload(reader, r, count * stride);
        }
    }

    private static void LocalizedTextResource(RttiReader reader, BinaryReader r, TypedObject obj)
    {
        var languages = WrittenLanguages(reader);
        var texts = new List<object?>(languages.Count);
        foreach (var _ in languages)
        {
            var entry = New(reader, "LocalizedTextResourceText");
            entry.Fields["Text"] = r.ReadShortString();
            entry.Fields["AltText"] = r.ReadShortString();
            entry.Fields["Mode"] = Enum(reader, "ESubtitleMode", r.ReadByte());
            texts.Add(entry);
        }
        obj.Fields["Texts"] = texts;
    }

    private static void ShaderResource(RttiReader reader, BinaryReader r, TypedObject obj)
    {
        obj.Fields["Size"] = r.ReadInt();
        obj.Fields["Unk04"] = Blob(reader, "GGUUID", r);
        obj.Fields["Unk14"] = Blob(reader, "GGUUID", r);
        obj.Fields["Unk24"] = Nested(reader, r, "StreamingDataSource");
    }

    private static void Texture(RttiReader reader, BinaryReader r, TypedObject obj)
    {
        obj.Fields["Header"] = TextureHeader(reader, r);
        obj.Fields["Data"] = TextureData(reader, r);
    }

    private static TypedObject TextureHeader(RttiReader reader, BinaryReader r)
    {
        var h = New(reader, "TextureHeader");
        h.Fields["Type"] = Enum(reader, "ETextureType", r.ReadShort());
        h.Fields["Width"] = r.ReadShort();
        h.Fields["Height"] = r.ReadShort();
        h.Fields["NumSurfaces"] = r.ReadShort();
        h.Fields["NumMips"] = r.ReadByte();
        h.Fields["PixelFormat"] = Enum(reader, "EPixelFormat", r.ReadByte());
        h.Fields["Unk0A"] = r.ReadByte();
        h.Fields["ColorSpace"] = Enum(reader, "ETexColorSpace", r.ReadByte());
        h.Fields["Unk0C"] = r.ReadByte();
        h.Fields["Unk0D"] = r.ReadByte();
        h.Fields["Unk0E"] = r.ReadByte();
        h.Fields["Unk0F"] = r.ReadByte();
        h.Fields["Hash"] = Blob(reader, "MurmurHashValue", r);
        return h;
    }

    private static TypedObject TextureData(RttiReader reader, BinaryReader r)
    {
        var d = New(reader, "TextureData");
        var totalSize = r.ReadInt();
        d.Fields["TotalSize"] = totalSize;
        d.Fields["EmbeddedSize"] = r.ReadInt();
        d.Fields["StreamedSize"] = r.ReadInt();
        d.Fields["StreamedMips"] = r.ReadInt();
        // NOTE: totalSize - 12, not - 16 (TextureCallback.java:52) — do not "fix" this.
        d.Fields["EmbeddedData"] = Payload(reader, r, totalSize - 12);
        return d;
    }

    private static TypedObject TextureInfo(RttiReader reader, BinaryReader r)
    {
        var info = New(reader, "TextureInfo");
        info.Fields["Header"] = TextureHeader(reader, r);
        info.Fields["Data"] = TextureData(reader, r);
        return info;
    }

    private static void TextureList(RttiReader reader, BinaryReader r, TypedObject obj)
    {
        var count = r.ReadInt();
        var entries = new List<object?>(count);
        for (var i = 0; i < count; i++)
        {
            var entry = New(reader, "TextureListEntryInfo");
            entry.Fields["StreamingOffset"] = r.ReadInt();
            entry.Fields["StreamingLength"] = r.ReadInt();
            entry.Fields["Texture"] = TextureInfo(reader, r);
            entries.Add(entry);
        }
        obj.Fields["Entries"] = entries;
    }

    private static void UITexture(RttiReader reader, BinaryReader r, TypedObject obj)
    {
        var animated = r.ReadBool();
        obj.Fields["Animated"] = animated;
        var smallSize = r.ReadInt();
        var largeSize = r.ReadInt();
        if (smallSize > 0)
            obj.Fields[animated ? "SmallTextureFrames" : "SmallTexture"] =
                animated ? UITextureFramesInfo(reader, r) : TextureInfo(reader, r);
        if (largeSize > 0)
            obj.Fields[animated ? "LargeTextureFrames" : "LargeTexture"] =
                animated ? UITextureFramesInfo(reader, r) : TextureInfo(reader, r);
    }

    private static void UITextureFrames(RttiReader reader, BinaryReader r, TypedObject obj)
    {
        var info = UITextureFramesInfo(reader, r);
        foreach (var (k, v) in info.Fields) obj.Fields[k] = v;
    }

    private static TypedObject UITextureFramesInfo(RttiReader reader, BinaryReader r)
    {
        var f = New(reader, "UITextureFramesInfo");
        f.Fields["Data"] = Payload(reader, r, r.ReadInt());
        var spanCount = r.ReadInt();
        var spans = new long[spanCount];
        for (var i = 0; i < spanCount; i++) spans[i] = r.ReadLong(); // i64 per span
        f.Fields["Spans"] = spans;
        f.Fields["Width"] = r.ReadInt();
        f.Fields["Height"] = r.ReadInt();
        f.Fields["PixelFormat"] = Enum(reader, "EPixelFormat", r.ReadInt());
        f.Fields["Frequency"] = Enum(reader, "EUpdateFrequency", (byte)r.ReadInt());
        f.Fields["Size"] = r.ReadInt();
        f.Fields["Scale"] = Nested(reader, r, "FSize");
        return f;
    }

    private static void VertexArrayResource(RttiReader reader, BinaryReader r, TypedObject obj)
    {
        var numVertices = r.ReadInt();
        var numStreams = r.ReadInt();
        obj.Fields["Count"] = numVertices;
        var isStreaming = r.ReadBool();
        obj.Fields["IsStreaming"] = isStreaming;
        var streams = new List<object?>(numStreams);
        for (var i = 0; i < numStreams; i++)
        {
            var s = New(reader, "VertexArrayStreamInfo");
            s.Fields["Flags"] = r.ReadInt();
            var stride = r.ReadInt();
            s.Fields["Stride"] = stride;
            var elementCount = r.ReadInt();
            var elements = new List<object?>(elementCount);
            for (var e = 0; e < elementCount; e++)
            {
                var el = New(reader, "VertexArrayStreamElementInfo");
                el.Fields["Offset"] = r.ReadByte();
                el.Fields["StorageType"] = Enum(reader, "EVertexElementStorageType", r.ReadByte());
                el.Fields["SlotsUsed"] = r.ReadByte();
                el.Fields["Element"] = Enum(reader, "EVertexElement", r.ReadByte());
                elements.Add(el);
            }
            s.Fields["Elements"] = elements;
            s.Fields["Hash"] = Blob(reader, "MurmurHashValue", r);
            if (!isStreaming) s.Fields["Data"] = Payload(reader, r, stride * numVertices);
            streams.Add(s);
        }
        obj.Fields["Streams"] = streams;
    }

    private static void ZivaRTResource(RttiReader reader, BinaryReader r, TypedObject obj)
    {
        obj.Fields["Data"] = Payload(reader, r, r.ReadInt());
    }
}
