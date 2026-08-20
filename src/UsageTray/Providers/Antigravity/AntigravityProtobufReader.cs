using System;
using System.Collections.Generic;
using System.Text;

namespace UsageTray.Providers.Antigravity;

public readonly record struct RawProtobufField(int FieldNumber, int WireType, ulong Varint, ReadOnlyMemory<byte> Bytes);

public ref struct ProtobufSpanReader
{
    private readonly ReadOnlySpan<byte> _span;
    private int _offset;

    public int FieldNumber { get; private set; }
    public int WireType { get; private set; }
    public ulong Varint { get; private set; }
    public ReadOnlySpan<byte> Bytes { get; private set; }

    public ProtobufSpanReader(ReadOnlySpan<byte> span)
    {
        _span = span;
        _offset = 0;
        FieldNumber = 0;
        WireType = 0;
        Varint = 0;
        Bytes = default;
    }

    public bool ReadNext()
    {
        if (_offset >= _span.Length) return false;
        var (tag, tagLen) = AntigravityProtobufReader.ReadVarint(_span.Slice(_offset));
        if (tagLen == 0) return false;
        _offset += tagLen;
        FieldNumber = (int)(tag >> 3);
        WireType = (int)(tag & 7);
        Varint = 0;
        Bytes = default;

        if (WireType == 0) // Varint
        {
            var (val, valLen) = AntigravityProtobufReader.ReadVarint(_span.Slice(_offset));
            if (valLen == 0) return false;
            _offset += valLen;
            Varint = val;
            return true;
        }
        else if (WireType == 2) // Length-delimited
        {
            var (len, lenBytes) = AntigravityProtobufReader.ReadVarint(_span.Slice(_offset));
            if (lenBytes == 0) return false;
            _offset += lenBytes;
            int byteCount = (int)len;
            if (_offset + byteCount > _span.Length) return false;
            Bytes = _span.Slice(_offset, byteCount);
            _offset += byteCount;
            return true;
        }
        else if (WireType == 1) // 64-bit
        {
            if (_offset + 8 > _span.Length) return false;
            Bytes = _span.Slice(_offset, 8);
            _offset += 8;
            return true;
        }
        else if (WireType == 5) // 32-bit
        {
            if (_offset + 4 > _span.Length) return false;
            Bytes = _span.Slice(_offset, 4);
            _offset += 4;
            return true;
        }
        return false;
    }
}

public static class AntigravityProtobufReader
{
    public static (ulong value, int bytesRead) ReadVarint(ReadOnlySpan<byte> buffer)
    {
        ulong result = 0;
        int shift = 0;
        int bytesRead = 0;
        while (bytesRead < buffer.Length)
        {
            byte b = buffer[bytesRead++];
            result |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return (result, bytesRead);
            shift += 7;
            if (shift >= 64) break;
        }
        return (result, bytesRead);
    }

    public static List<RawProtobufField> DecodeFields(ReadOnlyMemory<byte> memory)
    {
        var fields = new List<RawProtobufField>();
        var reader = new ProtobufSpanReader(memory.Span);
        int currentOffset = 0;
        while (reader.ReadNext())
        {
            ReadOnlyMemory<byte> fieldBytes = default;
            if (reader.WireType == 2 && !reader.Bytes.IsEmpty)
            {
                // Find offset slice in original memory
                var len = reader.Bytes.Length;
                var memorySpan = memory.Span;
                var index = memorySpan.Slice(currentOffset).IndexOf(reader.Bytes);
                if (index >= 0)
                {
                    currentOffset += index;
                    fieldBytes = memory.Slice(currentOffset, len);
                    currentOffset += len;
                }
            }
            fields.Add(new RawProtobufField(reader.FieldNumber, reader.WireType, reader.Varint, fieldBytes));
        }
        return fields;
    }

    public static DateTimeOffset? DecodeTimestamp(ReadOnlySpan<byte> span)
    {
        if (span.IsEmpty) return null;
        try
        {
            var reader = new ProtobufSpanReader(span);
            ulong seconds = 0;
            long nanos = 0;
            bool hasSeconds = false;
            while (reader.ReadNext())
            {
                if (reader.FieldNumber == 1 && reader.WireType == 0)
                {
                    seconds = reader.Varint;
                    hasSeconds = true;
                }
                else if (reader.FieldNumber == 2 && reader.WireType == 0)
                {
                    nanos = (long)reader.Varint;
                }
            }

            if (hasSeconds && seconds > 1_600_000_000 && seconds < 2_500_000_000)
            {
                return DateTimeOffset.FromUnixTimeSeconds((long)seconds).AddTicks(nanos / 100);
            }
        }
        catch { }
        return null;
    }

    public static AntigravityParsedGeneration? ParseGenerationMetadata(ReadOnlySpan<byte> blob, int sourceIdx)
    {
        if (blob.IsEmpty) return null;
        try
        {
            var genReader = new ProtobufSpanReader(blob);
            string? generationId = null;
            ReadOnlySpan<byte> chatMetaBlob = default;

            while (genReader.ReadNext())
            {
                if (genReader.FieldNumber == 4 && genReader.WireType == 2 && !genReader.Bytes.IsEmpty)
                {
                    generationId = Encoding.UTF8.GetString(genReader.Bytes);
                }
                else if (genReader.FieldNumber == 1 && genReader.WireType == 2 && !genReader.Bytes.IsEmpty)
                {
                    chatMetaBlob = genReader.Bytes;
                }
            }

            if (chatMetaBlob.IsEmpty) return null;

            var chatReader = new ProtobufSpanReader(chatMetaBlob);
            string? responseModel = null;
            string? displayName = null;
            ReadOnlySpan<byte> usageBlob = default;

            while (chatReader.ReadNext())
            {
                if (chatReader.FieldNumber == 19 && chatReader.WireType == 2 && !chatReader.Bytes.IsEmpty)
                {
                    responseModel = Encoding.UTF8.GetString(chatReader.Bytes);
                }
                else if (chatReader.FieldNumber == 21 && chatReader.WireType == 2 && !chatReader.Bytes.IsEmpty)
                {
                    displayName = Encoding.UTF8.GetString(chatReader.Bytes);
                }
                else if (chatReader.FieldNumber == 4 && chatReader.WireType == 2 && !chatReader.Bytes.IsEmpty)
                {
                    usageBlob = chatReader.Bytes;
                }
            }

            if (usageBlob.IsEmpty) return null;

            var usageReader = new ProtobufSpanReader(usageBlob);
            long inputTokens = 0;
            long aggregateOutputTokens = 0;
            long cacheWriteTokens = 0;
            long cacheReadTokens = 0;
            long thinkingOutputTokens = 0;
            long responseOutputTokens = 0;
            string? responseId = null;

            while (usageReader.ReadNext())
            {
                if (usageReader.WireType == 0)
                {
                    switch (usageReader.FieldNumber)
                    {
                        case 2: inputTokens = (long)usageReader.Varint; break;
                        case 3: aggregateOutputTokens = (long)usageReader.Varint; break;
                        case 4: cacheWriteTokens = (long)usageReader.Varint; break;
                        case 5: cacheReadTokens = (long)usageReader.Varint; break;
                        case 9: thinkingOutputTokens = (long)usageReader.Varint; break;
                        case 10: responseOutputTokens = (long)usageReader.Varint; break;
                    }
                }
                else if (usageReader.FieldNumber == 11 && usageReader.WireType == 2 && !usageReader.Bytes.IsEmpty)
                {
                    responseId = Encoding.UTF8.GetString(usageReader.Bytes);
                }
            }

            return new AntigravityParsedGeneration(
                sourceIdx,
                generationId,
                responseId,
                responseModel,
                displayName,
                inputTokens,
                cacheReadTokens,
                cacheWriteTokens,
                thinkingOutputTokens,
                responseOutputTokens,
                aggregateOutputTokens
            );
        }
        catch
        {
            return null;
        }
    }

    public static DateTimeOffset? ParseStepTimestamp(ReadOnlySpan<byte> stepMetadataBlob)
    {
        if (stepMetadataBlob.IsEmpty) return null;
        try
        {
            var reader = new ProtobufSpanReader(stepMetadataBlob);
            while (reader.ReadNext())
            {
                if (reader.WireType == 2 && (reader.FieldNumber == 1 || reader.FieldNumber == 6 ||
                    reader.FieldNumber == 7 || reader.FieldNumber == 8 || reader.FieldNumber == 22 || reader.FieldNumber == 32))
                {
                    var ts = DecodeTimestamp(reader.Bytes);
                    if (ts.HasValue) return ts;
                }
            }
        }
        catch { }
        return null;
    }
}

public sealed record AntigravityParsedGeneration(
    int SourceIdx,
    string? GenerationId,
    string? ResponseId,
    string? ResponseModel,
    string? DisplayName,
    long InputTokens,
    long CacheReadTokens,
    long CacheWriteTokens,
    long ThinkingOutputTokens,
    long ResponseOutputTokens,
    long AggregateOutputTokens
);
