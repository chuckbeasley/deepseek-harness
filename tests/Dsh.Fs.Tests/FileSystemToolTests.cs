using System.Text.Json;
using System.Text.Json.Nodes;

namespace Harness.Fs.Tests;

/// <summary>Consumer coverage: read/write registered on the tool runtime and executed with valid and invalid args.</summary>
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

    private static ToolExecutionResult ExecuteWithSession(Harness h, global::Harness.Session.Session session, string name, JsonElement args)
        => h.Tools.ExecuteAsync(new ToolExecutionInput(new ToolCallId("call-1"), name, args, CancellationToken.None)
        {
            Session = session,
        }, CancellationToken.None).GetAwaiter().GetResult();

    private static JsonElement EditArgs(string path, string oldString, string newString, bool replaceAll = false)
    {
        var root = new JsonObject
        {
            ["file_path"] = path,
            ["old_string"] = oldString,
            ["new_string"] = newString,
        };
        if (replaceAll) root["replace_all"] = true;
        return JsonSerializer.SerializeToElement(root);
    }

    public static void EditThroughRuntimeWithPolicy(Harness h)
    {
        Assert.True(h.Observations is not null && h.Sessions is not null, "policy harness");
        var session = h.Sessions.Create();
        ExecuteWithSession(h, session, "write", WriteArgs("config.txt", "mode=DEBUG\nlevel=info\n"));
        ExecuteWithSession(h, session, "read", Args("{\"file_path\":\"config.txt\"}"));
        var edit = ExecuteWithSession(h, session, "edit", EditArgs("config.txt", "DEBUG", "RELEASE"));
        Assert.False(edit.IsError, "edit succeeded after a read");
        var success = Assert.IsType<ToolExecutionSuccess>(edit);
        Assert.Equal("mode=RELEASE", File.ReadAllText(Path.Combine(h.WorkspaceRoot, "config.txt")).Split('\n')[0]);
        var block = Assert.IsType<TextBlock>(success.Content[0]);
        Assert.Equal("The file " + Path.Combine(h.WorkspaceRoot, "config.txt").Replace('\\', '/') + " has been updated successfully.", block.Text);
        var meta = Assert.IsType<JsonElement>(success.Meta);
        Assert.Equal("mode=DEBUG\nlevel=info", meta.GetProperty("diffs")[0].GetProperty("oldText").GetString());
        Assert.Equal("mode=RELEASE\nlevel=info", meta.GetProperty("diffs")[0].GetProperty("newText").GetString());
    }

    public static void EditWithoutReadRefusesWithRemedy(Harness h)
    {
        Assert.True(h.Observations is not null && h.Sessions is not null, "policy harness");
        var session = h.Sessions.Create();
        File.WriteAllText(Path.Combine(h.WorkspaceRoot, "settings.txt"), "color: blue\n");
        var edit = ExecuteWithSession(h, session, "edit", EditArgs("settings.txt", "blue", "green"));
        Assert.True(edit.IsError, "unobserved edit refuses");
        var failure = Assert.IsType<ToolExecutionFailure>(edit);
        Assert.Equal("FsError", failure.Error.Name);
        Assert.Equal("FS_NOT_OBSERVED", failure.Error.Code);
        Assert.True(failure.Error.Message.Contains("edit requires reading"), "message names the read-first rule");
        Assert.True(failure.Error.Message.Contains("read the file, then retry"), "message carries the remediation");
    }

    public static void EditStaleVersionRefuses(Harness h)
    {
        Assert.True(h.Observations is not null && h.Sessions is not null, "policy harness");
        var session = h.Sessions.Create();
        ExecuteWithSession(h, session, "write", WriteArgs("stale.txt", "one\n"));
        ExecuteWithSession(h, session, "read", Args("{\"file_path\":\"stale.txt\"}"));
        // An external mutation between the read and the edit invalidates the observed version.
        File.WriteAllText(Path.Combine(h.WorkspaceRoot, "stale.txt"), "two\n");
        var edit = ExecuteWithSession(h, session, "edit", EditArgs("stale.txt", "two", "three"));
        Assert.True(edit.IsError, "stale edit refuses");
        var failure = Assert.IsType<ToolExecutionFailure>(edit);
        Assert.Equal("FS_STALE_VERSION", failure.Error.Code);
    }

    public static void EditAmbiguousRefusesThenReplaceAllSucceeds(Harness h)
    {
        Assert.True(h.Observations is not null && h.Sessions is not null, "policy harness");
        var session = h.Sessions.Create();
        ExecuteWithSession(h, session, "write", WriteArgs("dup.txt", "a\nb\na\n"));
        ExecuteWithSession(h, session, "read", Args("{\"file_path\":\"dup.txt\"}"));
        var edit = ExecuteWithSession(h, session, "edit", EditArgs("dup.txt", "a", "x"));
        Assert.True(edit.IsError, "ambiguous edit refuses");
        Assert.Equal("FS_AMBIGUOUS_EDIT", Assert.IsType<ToolExecutionFailure>(edit).Error.Code);
        var replaceAll = ExecuteWithSession(h, session, "edit", EditArgs("dup.txt", "a", "x", replaceAll: true));
        Assert.False(replaceAll.IsError, "replace-all edit succeeds");
        Assert.Equal("x\nb\nx\n", File.ReadAllText(Path.Combine(h.WorkspaceRoot, "dup.txt")));
    }

    public static void EditConfirmedAbsentRefusesWithNotFound(Harness h)
    {
        Assert.True(h.Observations is not null && h.Sessions is not null, "policy harness");
        var session = h.Sessions.Create();
        ExecuteWithSession(h, session, "write", WriteArgs("gone.txt", "here\n"));
        // An absent observation (the read of a deleted file) makes the edit refuse with not-found.
        File.Delete(Path.Combine(h.WorkspaceRoot, "gone.txt"));
        ExecuteWithSession(h, session, "read", Args("{\"file_path\":\"gone.txt\"}"));
        var edit = ExecuteWithSession(h, session, "edit", EditArgs("gone.txt", "here", "there"));
        Assert.True(edit.IsError, "absent-target edit refuses");
        Assert.Equal("FS_NOT_FOUND", Assert.IsType<ToolExecutionFailure>(edit).Error.Code);
    }

    public static void WriteThenReadThroughRuntime(Harness h)
    {
        var write = Execute(h, "write", WriteArgs("greeting.txt", "hello\nworld\n"));
        Assert.False(write.IsError, "write succeeded");
        var writeSuccess = Assert.IsType<ToolExecutionSuccess>(write);
        Assert.True(writeSuccess.Value.GetProperty("path").GetString().EndsWith("/greeting.txt", StringComparison.Ordinal), "the write outcome carries the absolute TS-style path");
        Assert.Equal("create", writeSuccess.Value.GetProperty("operation").GetString());
        var writeBlock = Assert.IsType<TextBlock>(writeSuccess.Content[0]);
        Assert.True(writeBlock.Text.Contains("Created file"), "render says Created");

        var read = Execute(h, "read", Args("{\"file_path\":\"greeting.txt\"}"));
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
        Execute(h, "write", WriteArgs("multi.txt", "l1\nl2\nl3\nl4\nl5\n"));
        var read = Execute(h, "read", Args("{\"file_path\":\"multi.txt\",\"offset\":3,\"limit\":2}"));
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
        Execute(h, "write", WriteArgs("short.txt", "a\nb\nc\n"));
        var read = Execute(h, "read", Args("{\"file_path\":\"short.txt\",\"offset\":5}"));
        Assert.True(read.IsError, "out-of-range offset fails");
        var failure = Assert.IsType<ToolExecutionFailure>(read);
        Assert.True(failure.Error.Message.Contains("out of range"), "message names the range violation");
    }

    public static void ReadMissingFileMapsToTypedError(Harness h)
    {
        var read = Execute(h, "read", Args("{\"file_path\":\"nope.txt\"}"));
        Assert.True(read.IsError, "missing file fails");
        var failure = Assert.IsType<ToolExecutionFailure>(read);
        Assert.True(failure.Error.Message.Contains("not found"), "message names not found");
    }

    public static void ReadDirectoryFailsThroughTool(Harness h)
    {
        h.Fs.MkdirAsync(h.Fs.ResolveMkdir(new FsMkdirRequest("dir"))).GetAwaiter().GetResult();
        var read = Execute(h, "read", Args("{\"file_path\":\"dir\"}"));
        Assert.True(read.IsError, "directory read fails");
        var failure = Assert.IsType<ToolExecutionFailure>(read);
        Assert.True(failure.Error.Message.Contains("not a regular file"), "message names the type violation");
    }

    public static void InvalidArgumentsAreRejected(Harness h)
    {
        Assert.True(Execute(h, "write", Args("{\"file_path\":\"  \",\"content\":\"x\"}")).IsError, "blank path rejected");
        Assert.True(Execute(h, "write", Args("{\"content\":\"x\"}")).IsError, "missing file_path rejected");
        Assert.True(Execute(h, "read", Args("{\"file_path\":\"a.txt\",\"limit\":0}")).IsError, "zero limit rejected");
        Assert.True(Execute(h, "read", Args("{\"file_path\":\"a.txt\",\"limit\":3000}")).IsError, "over-cap limit rejected");
        Assert.True(Execute(h, "read", Args("{\"file_path\":\"a.txt\",\"offset\":-1}")).IsError, "negative offset rejected");
    }

    public static void WriteOverwriteRendersUpdated(Harness h)
    {
        Execute(h, "write", WriteArgs("u.txt", "one"));
        var second = Execute(h, "write", WriteArgs("u.txt", "two"));
        Assert.False(second.IsError, "overwrite succeeded");
        var success = Assert.IsType<ToolExecutionSuccess>(second);
        Assert.Equal("update", success.Value.GetProperty("operation").GetString());
        var block = Assert.IsType<TextBlock>(success.Content[0]);
        Assert.True(block.Text.Contains("Updated file"), "render says Updated");
    }

    public static void EmptyContentWritesEmptyFile(Harness h)
    {
        var write = Execute(h, "write", WriteArgs("empty.txt", ""));
        Assert.False(write.IsError, "empty content accepted");
        Assert.Equal(string.Empty, File.ReadAllText(Path.Combine(h.WorkspaceRoot, "empty.txt")));
        var read = Execute(h, "read", Args("{\"file_path\":\"empty.txt\"}"));
        Assert.False(read.IsError, "empty file read succeeded");
        var success = Assert.IsType<ToolExecutionSuccess>(read);
        Assert.Equal(0, success.Value.GetProperty("totalLines").GetInt32());
        Assert.Equal(0, success.Value.GetProperty("lines").GetArrayLength());
    }

    public static void ReadTruncatesLongLine(Harness h)
    {
        const string suffix = "... (line truncated to 2000 chars)";
        var longLine = new string('x', 5000);
        Execute(h, "write", WriteArgs("long.txt", longLine));
        var read = Execute(h, "read", Args("{\"file_path\":\"long.txt\"}"));
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
        Execute(h, "write", WriteArgs("bytecap.txt", content));
        var read = Execute(h, "read", Args("{\"file_path\":\"bytecap.txt\"}"));
        Assert.False(read.IsError, "byte-capped read succeeded");
        var success = Assert.IsType<ToolExecutionSuccess>(read);
        Assert.Equal(40, success.Value.GetProperty("totalLines").GetInt32());
        Assert.True(success.Value.GetProperty("lines").GetArrayLength() < 40, "byte cap stopped the window early");
        var block = Assert.IsType<TextBlock>(success.Content[0]);
        Assert.True(block.Text.Contains("(Output capped."), "render marks the byte cap");
    }
}
