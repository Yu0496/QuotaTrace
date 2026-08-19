using System;
using System.Collections.Generic;
using System.Text;

namespace UsageTray.Providers.Antigravity;

public readonly record struct RawProtobufField(int FieldNumber, int WireType, ulong Varint, ReadOnlyMemory<byte> Bytes);

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
        var span = memory.Span;
        int offset = 0;
        while (offset < span.Length)
        {
            var (tag, tagLen) = ReadVarint(span.Slice(offset));
            if (tagLen == 0) break;
            offset += tagLen;
            int fieldNumber = (int)(tag >> 3);
            int wireType = (int)(tag & 7);

            if (wireType == 0) // Varint
            {
                var (val, valLen) = ReadVarint(span.Slice(offset));
                if (valLen == 0) break;
                offset += valLen;
                fields.Add(new RawProtobufField(fieldNumber, wireType, val, default));
            }
            else if (wireType == 2) // Length-delimited
            {
                var (len, lenBytes) = ReadVarint(span.Slice(offset));
                if (lenBytes == 0) break;
                offset += lenBytes;
                int byteCount = (int)len;
                if (offset + byteCount > span.Length) break;
                var slice = memory.Slice(offset, byteCount);
                offset += byteCount;
                fields.Add(new RawProtobufField(fieldNumber, wireType, 0, slice));
            }
            else if (wireType == 1) // 64-bit
            {
                if (offset + 8 > span.Length) break;
                var slice = memory.Slice(offset, 8);
                offset += 8;
                fields.Add(new RawProtobufField(fieldNumber, wireType, 0, slice));
            }
            else if (wireType == 5) // 32-bit
            {
                if (offset + 4 > span.Length) break;
                var slice = memory.Slice(offset, 4);
                offset += 4;
                fields.Add(new RawProtobufField(fieldNumber, wireType, 0, slice));
            }
            else
            {
                break;
            }
        }
        return fields;
    }

    public static DateTimeOffset? DecodeTimestamp(ReadOnlyMemory<byte> memory)
    {
        if (memory.IsEmpty) return null;
        try
        {
            var fields = DecodeFields(memory);
            ulong seconds = 0;
            long nanos = 0;
            bool hasSeconds = false;
            foreach (var f in fields)
            {
                if (f.FieldNumber == 1 && f.WireType == 0)
                {
                    seconds = f.Varint;
                    hasSeconds = true;
                }
                else if (f.FieldNumber == 2 && f.WireType == 0)
                {
                    nanos = (long)f.Varint;
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

    public static AntigravityParsedGeneration? ParseGenerationMetadata(ReadOnlyMemory<byte> blob, int sourceIdx)
    {
        if (blob.IsEmpty) return null;
        try
        {
            var genFields = DecodeFields(blob);
            string? generationId = null;
            ReadOnlyMemory<byte> chatMetaBlob = default;

            foreach (var field in genFields)
            {
                if (field.FieldNumber == 4 && field.WireType == 2 && !field.Bytes.IsEmpty)
                {
                    generationId = Encoding.UTF8.GetString(field.Bytes.Span);
                }
                else if (field.FieldNumber == 1 && field.WireType == 2 && !field.Bytes.IsEmpty)
                {
                    chatMetaBlob = field.Bytes;
                }
            }

            if (chatMetaBlob.IsEmpty) return null;

            var chatFields = DecodeFields(chatMetaBlob);
            string? responseModel = null;
            string? displayName = null;
            ReadOnlyMemory<byte> usageBlob = default;

            foreach (var field in chatFields)
            {
                if (field.FieldNumber == 19 && field.WireType == 2 && !field.Bytes.IsEmpty)
                {
                    responseModel = Encoding.UTF8.GetString(field.Bytes.Span);
                }
                else if (field.FieldNumber == 21 && field.WireType == 2 && !field.Bytes.IsEmpty)
                {
                    displayName = Encoding.UTF8.GetString(field.Bytes.Span);
                }
                else if (field.FieldNumber == 4 && field.WireType == 2 && !field.Bytes.IsEmpty)
                {
                    usageBlob = field.Bytes;
                }
            }

            if (usageBlob.IsEmpty) return null;

            var usageFields = DecodeFields(usageBlob);
            long inputTokens = 0;
            long aggregateOutputTokens = 0;
            long cacheWriteTokens = 0;
            long cacheReadTokens = 0;
            long thinkingOutputTokens = 0;
            long responseOutputTokens = 0;
            string? responseId = null;

            foreach (var field in usageFields)
            {
                if (field.WireType == 0)
                {
                    switch (field.FieldNumber)
                    {
                        case 2: inputTokens = (long)field.Varint; break;
                        case 3: aggregateOutputTokens = (long)field.Varint; break;
                        case 4: cacheWriteTokens = (long)field.Varint; break;
                        case 5: cacheReadTokens = (long)field.Varint; break;
                        case 9: thinkingOutputTokens = (long)field.Varint; break;
                        case 10: responseOutputTokens = (long)field.Varint; break;
                    }
                }
                else if (field.FieldNumber == 11 && field.WireType == 2 && !field.Bytes.IsEmpty)
                {
                    responseId = Encoding.UTF8.GetString(field.Bytes.Span);
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

    public static DateTimeOffset? ParseStepTimestamp(ReadOnlyMemory<byte> stepMetadataBlob)
    {
        if (stepMetadataBlob.IsEmpty) return null;
        try
        {
            var fields = DecodeFields(stepMetadataBlob);
            foreach (var field in fields)
            {
                if (field.FieldNumber == 1 && field.WireType == 2)
                {
                    var ts = DecodeTimestamp(field.Bytes);
                    if (ts.HasValue) return ts;
                }
            }
            // Alternate timestamp fields
            int[] alternateFieldNumbers = [6, 7, 8, 22, 32];
            foreach (var fNum in alternateFieldNumbers)
            {
                foreach (var field in fields)
                {
                    if (field.FieldNumber == fNum && field.WireType == 2)
                    {
                        var ts = DecodeTimestamp(field.Bytes);
                        if (ts.HasValue) return ts;
                    }
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
