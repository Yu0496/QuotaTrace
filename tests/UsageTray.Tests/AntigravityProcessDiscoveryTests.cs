using UsageTray.Providers.Antigravity;

namespace UsageTray.Tests;

public sealed class AntigravityProcessDiscoveryTests
{
    [Theory]
    [InlineData("language_server.exe --csrf_token abc-123 --https_server_port 0", "abc-123")]
    [InlineData("language_server.exe --csrf-token=token_value --standalone", "token_value")]
    public void ExtractsCsrfTokenFromLanguageServerCommandLine(string commandLine, string expected)
    {
        Assert.Equal(expected, AntigravityProcessDiscovery.ExtractCsrfToken(commandLine));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("language_server.exe --https_server_port 0")]
    public void ReturnsNullWhenCommandLineDoesNotContainCsrfToken(string? commandLine)
    {
        Assert.Null(AntigravityProcessDiscovery.ExtractCsrfToken(commandLine));
    }
}
