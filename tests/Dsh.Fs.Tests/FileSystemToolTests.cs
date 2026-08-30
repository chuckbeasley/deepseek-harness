using System.Text.Json;
using System.Text.Json.Nodes;

namespace Dsh.Fs.Tests;

/// <summary>Consumer coverage: fs_read/fs_write registered on the tool runtime and executed with valid and invalid args.</summary>
public static class FileSystemToolTests
{
    private static JsonElement Args(string json) => JsonSerializer.SerializeToElement(JsonNode.Parse(json)!);

    private static JsonElement WriteArgs(string path, string content)
    {
        var root = new JsonObject { ["file_path"] = path, ["content"] = content };
        return JsonSerializer.SerializeToElement(root);
    }

    private static ToolExecutionResult Execute(Harness h, string name, JsonElement args)
        => h.Tools.ExecuteAsync(new ToolExecutionInput(new ToolCallId("call-1"), name, args, CancellationToken.None), CancellationToken.None).GetAwaiter().GetResult();

    public static void WriteThenReadThroughRuntime(Harness h)
    {
        var write = Execute(h, "fs_write", WriteArgs("greeting.txt", "hello\nworld\n"));
        Assert.False(write.IsError, "fs_write succeeded");
        var writeSuccess = Assert.IsType<ToolExecutionSuccess>(write);
        Assert.Equal("greeting.txt", writeSuccess.Value.GetProperty("path").GetString());
        Assert.Equal("create", writeSuccess.Value.GetProperty("operation").GetString());
        var writeBlock = Assert.IsType<TextBlock>(writeSuccess.Content[0]);
        Assert.True(writeBlock.Text.Contains("Created file"), "render says Created");

        var read = Execute(h, "fs_read", Args("{\"file_path\":\"greeting.txt\"}"));
        Assert.False(read.IsError, "fs_read succeeded");
        var readSuccess = Assert.IsType<ToolExecutionSuccess>(read);
        Assert.Equal(1, readSuccess.Value.GetProperty("offset").GetInt32());
        Assert.Equal(2, readSuccess.Value.GetProperty("totalLines").GetInt32());
        var lines = readSuccess.Value.GetProperty("lines");
        Assert.Equal(2, lines.GetArrayLength());
        Assert.Equal(1, lines[0].GetProperty("number").GetInt32());
        Assert.Equal("hello", lines[0].GetProperty("text").GetString());
        Assert.Equal(2, lines[1].GetProperty("number").GetInt32());
        Assert.Equal("world", lines[1].GetProperty("text").GetString());
        var readBlock = Assert.IsType<TextBlock>(readSuccess.Content[0]);
        Assert.True(readBlock.Text.Contains("1: hello"), "render numbers lines");
        Assert.True(readBlock.Text.Contains("(End of file - total 2 lines)"), "render footers EOF");
    }

    public static void ReadWindowAndContinuationFooter(Harness h)
    {
        Execute(h, "fs_write", WriteArgs("multi.txt", "l1\nl2\nl3\nl4\nl5\n"));
        var read = Execute(h, "fs_read", Args("{\"file_path\":\"multi.txt\",\"offset\":3,\"limit\":2}"));
        Assert.False(read.IsError, "windowed read succeeded");
        var success = Assert.IsType<ToolExecutionSuccess>(read);
        Assert.Equal(3, success.Value.GetProperty("offset").GetInt32());
        var lines = success.Value.GetProperty("lines");
        Assert.Equal(2, lines.GetArrayLength());
        Assert.Equal(3, lines[0].GetProperty("number").GetInt32());
        Assert.Equal("l3", lines[0].GetProperty("text").GetString());
        Assert.Equal(4, lines[1].GetProperty("number").GetInt32());
        var block = Assert.IsType<TextBlock>(success.Content[0]);
        Assert.True(block.Text.Contains("(Showing lines 3-4 of 5. Use offset=5 to continue.)"), "continuation footer");
    }

    public static void ReadOffsetOutOfRangeFails(Harness h)
    {
        Execute(h, "fs_write", WriteArgs("short.txt", "a\nb\nc\n"));
        var read = Execute(h, "fs_read", Args("{\"file_path\":\"short.txt\",\"offset\":5}"));
        Assert.True(read.IsError, "out-of-range offset fails");
        var failure = Assert.IsType<ToolExecutionFailure>(read);
        Assert.True(failure.Error.Message.Contains("out of range"), "message names the range violation");
    }

    public static void ReadMissingFileMapsToTypedError(Harness h)
    {
        var read = Execute(h, "fs_read", Args("{\"file_path\":\"nope.txt\"}"));
        Assert.True(read.IsError, "missing file fails");
        var failure = Assert.IsType<ToolExecutionFailure>(read);
        Assert.True(failure.Error.Message.Contains("not found"), "message names not found");
    }

    public static void ReadDirectoryFailsThroughTool(Harness h)
    {
        h.Fs.MkdirAsync(h.Fs.ResolveMkdir(new FsMkdirRequest("dir"))).GetAwaiter().GetResult();
        var read = Execute(h, "fs_read", Args("{\"file_path\":\"dir\"}"));
        Assert.True(read.IsError, "directory read fails");
        var failure = Assert.IsType<ToolExecutionFailure>(read);
        Assert.True(failure.Error.Message.Contains("not a regular file"), "message names the type violation");
    }

    public static void InvalidArgumentsAreRejected(Harness h)
    {
        Assert.True(Execute(h, "fs_write", Args("{\"file_path\":\"  \",\"content\":\"x\"}")).IsError, "blank path rejected");
        Assert.True(Execute(h, "fs_write", Args("{\"content\":\"x\"}")).IsError, "missing file_path rejected");
        Assert.True(Execute(h, "fs_read", Args("{\"file_path\":\"a.txt\",\"limit\":0}")).IsError, "zero limit rejected");
        Assert.True(Execute(h, "fs_read", Args("{\"file_path\":\"a.txt\",\"limit\":3000}")).IsError, "over-cap limit rejected");
        Assert.True(Execute(h, "fs_read", Args("{\"file_path\":\"a.txt\",\"offset\":-1}")).IsError, "negative offset rejected");
    }

    public static void WriteOverwriteRendersUpdated(Harness h)
    {
        Execute(h, "fs_write", WriteArgs("u.txt", "one"));
        var second = Execute(h, "fs_write", WriteArgs("u.txt", "two"));
        Assert.False(second.IsError, "overwrite succeeded");
        var success = Assert.IsType<ToolExecutionSuccess>(second);
        Assert.Equal("update", success.Value.GetProperty("operation").GetString());
        var block = Assert.IsType<TextBlock>(success.Content[0]);
        Assert.True(block.Text.Contains("Updated file"), "render says Updated");
    }

    public static void EmptyContentWritesEmptyFile(Harness h)
    {
        var write = Execute(h, "fs_write", WriteArgs("empty.txt", ""));
        Assert.False(write.IsError, "empty content accepted");
        Assert.Equal(string.Empty, File.ReadAllText(Path.Combine(h.WorkspaceRoot, "empty.txt")));
        var read = Execute(h, "fs_read", Args("{\"file_path\":\"empty.txt\"}"));
        Assert.False(read.IsError, "empty file read succeeded");
        var success = Assert.IsType<ToolExecutionSuccess>(read);
        Assert.Equal(0, success.Value.GetProperty("totalLines").GetInt32());
        Assert.Equal(0, success.Value.GetProperty("lines").GetArrayLength());
    }

    public static void ReadTruncatesLongLine(Harness h)
    {
        const string suffix = "... (line truncated to 2000 chars)";
        var longLine = new string('x', 5000);
        Execute(h, "fs_write", WriteArgs("long.txt", longLine));
        var read = Execute(h, "fs_read", Args("{\"file_path\":\"long.txt\"}"));
        Assert.False(read.IsError, "long line read succeeded");
        var success = Assert.IsType<ToolExecutionSuccess>(read);
        var line = success.Value.GetProperty("lines")[0].GetProperty("text").GetString();
        Assert.Equal(2000 + suffix.Length, line!.Length);
        var block = Assert.IsType<TextBlock>(success.Content[0]);
        Assert.True(block.Text.Contains("line truncated to 2000 chars"), "render marks the truncation");
    }

    public static void ReadByteCapTruncatesWindow(Harness h)
    {
        // Default caps: 51200 output bytes. Forty 2000-char lines exceed the cap, so the
        // window stops early while the scan still reports the exact total line count.
        var content = string.Join("\n", Enumerable.Range(1, 40).Select(i => new string((char)('a' + i % 26), 2000))) + "\n";
        Execute(h, "fs_write", WriteArgs("bytecap.txt", content));
        var read = Execute(h, "fs_read", Args("{\"file_path\":\"bytecap.txt\"}"));
        Assert.False(read.IsError, "byte-capped read succeeded");
        var success = Assert.IsType<ToolExecutionSuccess>(read);
        Assert.Equal(40, success.Value.GetProperty("totalLines").GetInt32());
        Assert.True(success.Value.GetProperty("lines").GetArrayLength() < 40, "byte cap stopped the window early");
        var block = Assert.IsType<TextBlock>(success.Content[0]);
        Assert.True(block.Text.Contains("(Output capped."), "render marks the byte cap");
    }
}
