using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.Data.Sqlite;
using UsageTray.Core;
using UsageTray.Providers.Antigravity;
using Xunit;

namespace UsageTray.Tests;

public sealed class AntigravitySqliteHistoryParserTests
{
    [Fact]
    public void ParsesProtobufGenMetadataAndStepsCorrectly()
    {
        using var workspace = new TempWorkspace();
        var path = workspace.File("conv_test.db");
        var connectionString = new SqliteConnectionStringBuilder { DataSource = path }.ToString();

        var tsSeconds = 1771480000L; // 2026-02-19...
        var stepData = CreateProtobufStepMetadata(tsSeconds, 500000);
        var genData = CreateProtobufGenMetadata(
            model: "gemini-3.7-flash",
            input: 1000,
            cacheRead: 2000,
            cacheWrite: 300,
            thinking: 400,
            respOutput: 600,
            aggregateOutput: 1000,
            responseId: "resp_12345",
            generationId: "gen_67890"
        );

        using (var connection = new SqliteConnection(connectionString))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
            CREATE TABLE steps (
                idx INTEGER PRIMARY KEY,
                metadata BLOB
            );
            CREATE TABLE gen_metadata (
                idx INTEGER PRIMARY KEY,
                data BLOB
            );
            """;
            command.ExecuteNonQuery();

            using var insertStep = connection.CreateCommand();
            insertStep.CommandText = "INSERT INTO steps(idx, metadata) VALUES(1, $blob)";
            insertStep.Parameters.AddWithValue("$blob", stepData);
            insertStep.ExecuteNonQuery();

            using var insertGen = connection.CreateCommand();
            insertGen.CommandText = "INSERT INTO gen_metadata(idx, data) VALUES(1, $blob)";
            insertGen.Parameters.AddWithValue("$blob", genData);
            insertGen.ExecuteNonQuery();
        }

        var parser = new AntigravitySqliteHistoryParser();
        var result = parser.ParseFile(path);

        Assert.Single(result.Generations);
        var gen = result.Generations[0];
        Assert.Equal("resp_12345", gen.ResponseId);
        Assert.Equal("gen_67890", gen.GenerationId);
        Assert.Equal("gemini-3.7-flash", gen.Model);
        Assert.Equal(1000, gen.InputTokens);
        Assert.Equal(2000, gen.CacheReadTokens);
        Assert.Equal(300, gen.CacheWriteTokens);
        Assert.Equal(400, gen.ThinkingOutputTokens);
        Assert.Equal(600, gen.ResponseOutputTokens);
        Assert.Equal(1000, gen.OutputTokens);
        Assert.True(gen.InvariantHolds);
        Assert.Equal(tsSeconds, gen.Timestamp.ToUnixTimeSeconds());

        Assert.Single(result.Buckets);
        var bucket = result.Buckets[0];
        // In UsageBucket: InputTokens = UncachedInput (1000) + CacheRead (2000) + CacheWrite (300) = 3300
        Assert.Equal(3300, bucket.InputTokens);
        Assert.Equal(2000, bucket.CachedInputTokens);
        Assert.Equal(300, bucket.CacheWriteInputTokens);
        Assert.Equal(1000, bucket.OutputTokens);
        Assert.Equal(CostQuality.ExactTokenSplit, bucket.CostQuality);
    }

    [Fact]
    public void DeduplicationKeyPrefersResponseIdThenGenerationId()
    {
        var g1 = new AntigravityGenerationUsage("c1", "gen1", "resp1", DateTimeOffset.UtcNow, "model", null, 10, 0, 0, 0, 5, 5, null, "db1", 1);
        var g2 = new AntigravityGenerationUsage("c1", "gen2", null, DateTimeOffset.UtcNow, "model", null, 10, 0, 0, 0, 5, 5, null, "db1", 2);
        var g3 = new AntigravityGenerationUsage("c1", null, null, DateTimeOffset.UtcNow, "model", null, 10, 0, 0, 0, 5, 5, null, "db1", 3);

        Assert.Equal("resp1", g1.EffectiveKey);
        Assert.Equal("gen2", g2.EffectiveKey);
        Assert.Equal("c1#3#model#", g3.EffectiveKey);
    }

    private static byte[] CreateProtobufGenMetadata(
        string model,
        long input,
        long cacheRead,
        long cacheWrite,
        long thinking,
        long respOutput,
        long aggregateOutput,
        string responseId,
        string generationId)
    {
        var statsBytes = new List<byte>();
        WriteVarintField(statsBytes, 2, input);
        WriteVarintField(statsBytes, 3, aggregateOutput);
        WriteVarintField(statsBytes, 4, cacheWrite);
        WriteVarintField(statsBytes, 5, cacheRead);
        WriteVarintField(statsBytes, 9, thinking);
        WriteVarintField(statsBytes, 10, respOutput);
        WriteStringField(statsBytes, 11, responseId);

        var modelMetaBytes = new List<byte>();
        WriteBytesField(modelMetaBytes, 4, statsBytes.ToArray());
        WriteStringField(modelMetaBytes, 19, model);

        var outerBytes = new List<byte>();
        WriteBytesField(outerBytes, 1, modelMetaBytes.ToArray());
        WriteStringField(outerBytes, 4, generationId);

        return outerBytes.ToArray();
    }

    private static byte[] CreateProtobufStepMetadata(long seconds, int nanos)
    {
        var tsBytes = new List<byte>();
        WriteVarintField(tsBytes, 1, seconds);
        WriteVarintField(tsBytes, 2, nanos);

        var outerBytes = new List<byte>();
        WriteBytesField(outerBytes, 1, tsBytes.ToArray());
        return outerBytes.ToArray();
    }

    private static void WriteVarintField(List<byte> buffer, int fieldNumber, long value)
    {
        WriteTag(buffer, fieldNumber, 0);
        WriteVarint(buffer, (ulong)value);
    }

    private static void WriteStringField(List<byte> buffer, int fieldNumber, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        WriteBytesField(buffer, fieldNumber, bytes);
    }

    private static void WriteBytesField(List<byte> buffer, int fieldNumber, byte[] bytes)
    {
        WriteTag(buffer, fieldNumber, 2);
        WriteVarint(buffer, (ulong)bytes.Length);
        buffer.AddRange(bytes);
    }

    private static void WriteTag(List<byte> buffer, int fieldNumber, int wireType)
    {
        WriteVarint(buffer, (ulong)((fieldNumber << 3) | wireType));
    }

    private static void WriteVarint(List<byte> buffer, ulong value)
    {
        while (value >= 0x80)
        {
            buffer.Add((byte)((value & 0x7F) | 0x80));
            value >>= 7;
        }
        buffer.Add((byte)value);
    }
}
