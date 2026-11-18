// SPDX-License-Identifier: GPL-3.0-or-later
// This file is part of PushNav (camera server, C# / .NET).

using PushNav.CameraServer.Cli;

namespace PushNav.CameraServer.Tests.Cli;

public class CommandLineTests
{
    [Fact]
    public void DefaultsMatchTheSpec()
    {
        var result = CommandLine.Parse([]);

        Assert.True(result.Success);
        var o = result.Options!;
        Assert.Equal(8764, o.Port);
        Assert.Null(o.Backend);
        Assert.Null(o.FakeImagesDirectory);
        Assert.False(o.List);
        Assert.Equal(90, o.JpegQuality);
        Assert.Equal(LogLevel.Info, o.LogLevel);
    }

    [Fact]
    public void ParsesAllFlagsInSeparateAndInlineForm()
    {
        var result = CommandLine.Parse(
            ["--port", "9000", "--backend=fake", "--fake-images", @"C:\img dir", "--list", "--jpeg-quality=75", "--log-level", "debug"]);

        Assert.True(result.Success, result.Error);
        var o = result.Options!;
        Assert.Equal(9000, o.Port);
        Assert.Equal("fake", o.Backend);
        Assert.Equal(@"C:\img dir", o.FakeImagesDirectory);
        Assert.True(o.List);
        Assert.Equal(75, o.JpegQuality);
        Assert.Equal(LogLevel.Debug, o.LogLevel);
    }

    [Theory]
    [InlineData("ids")]
    [InlineData("uvc")]
    [InlineData("fake")]
    [InlineData("FAKE")]
    public void AcceptsTheSpecifiedBackends(string backend)
    {
        var result = CommandLine.Parse(["--backend", backend]);

        Assert.True(result.Success, result.Error);
        Assert.Equal(backend.ToLowerInvariant(), result.Options!.Backend);
    }

    [Theory]
    [InlineData("--port", "abc")]
    [InlineData("--port", "-1")]
    [InlineData("--port", "65536")]
    [InlineData("--port", "")]
    [InlineData("--backend", "directshow")]
    [InlineData("--jpeg-quality", "0")]
    [InlineData("--jpeg-quality", "101")]
    [InlineData("--jpeg-quality", "9.5")]
    [InlineData("--log-level", "trace")]
    public void RejectsInvalidValues(string flag, string value)
    {
        var result = CommandLine.Parse([flag, value]);

        Assert.False(result.Success);
        Assert.Contains(flag, result.Error);
    }

    [Theory]
    [InlineData("--port")]
    [InlineData("--backend")]
    [InlineData("--fake-images")]
    [InlineData("--jpeg-quality")]
    [InlineData("--log-level")]
    public void RejectsAMissingValue(string flag)
    {
        var result = CommandLine.Parse([flag]);

        Assert.False(result.Success);
        Assert.Contains("needs a value", result.Error);
    }

    [Fact]
    public void RejectsUnknownArguments()
    {
        var result = CommandLine.Parse(["--bogus"]);

        Assert.False(result.Success);
        Assert.Contains("--bogus", result.Error);
    }

    [Fact]
    public void ListTakesNoValue()
    {
        Assert.False(CommandLine.Parse(["--list=yes"]).Success);
    }

    [Fact]
    public void FakeImagesNeedsTheFakeBackend()
    {
        Assert.False(CommandLine.Parse(["--backend", "ids", "--fake-images", "x"]).Success);
        Assert.True(CommandLine.Parse(["--backend", "fake", "--fake-images", "x"]).Success);
        Assert.True(CommandLine.Parse(["--fake-images", "x"]).Success); // backend unspecified: checked when backends are created
    }

    [Theory]
    [InlineData("-h")]
    [InlineData("--help")]
    public void HelpIsRecognised(string flag)
    {
        var result = CommandLine.Parse([flag]);

        Assert.True(result.Success);
        Assert.True(result.Options!.ShowHelp);
    }

    [Fact]
    public void PortZeroAsksForAFreePort()
    {
        Assert.Equal(0, CommandLine.Parse(["--port", "0"]).Options!.Port);
    }
}
