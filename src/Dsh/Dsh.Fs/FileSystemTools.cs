using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Dsh.Llm;
using Dsh.Session;
using Dsh.Tools;

namespace Dsh.Fs;

/// <summary>
/// Consumer tools over ctx.fs (wave-1 port of packages/fs/tool-fs): the model-facing fs_read and
/// fs_write tools with the TS schemas, result rendering, and error mapping. The tools are
/// non-durable consumers — they append no session events and emit no fs/observed policy
/// observations (the observation-policy seam is deferred). Deferred tools: diff, edit, search,
/// read-image, sandbox, session-cwd. The read tool reads whole files (streaming deferred) and
/// enforces the line/byte caps of the TS read window; the write tool issues an unconditional
/// create-or-overwrite (the bare default — no intent is passed).
/// </summary>
public static class FileSystemTools
{
    /// <summary>Default and maximum number of lines returned by one fs_read call (the TS READ_LIMIT).</summary>
    public const int ReadDefaultLimit = 2000;

    /// <summary>Default maximum characters returned for a single line (the TS READ_MAX_LINE_LENGTH).</summary>
    public const int ReadDefaultMaxLineLength = 2000;

    /// <summary>Default maximum bytes returned for the selected lines of one fs_read call (the TS READ_MAX_BYTES).</summary>
    public const int ReadDefaultMaxBytes = 50 * 1024;

    /// <summary>Resolved fs_read caps; the defaults are the TS constants.</summary>
    public sealed record FsReadToolCaps(
        int Limit = ReadDefaultLimit,
        int MaxLineLength = ReadDefaultMaxLineLength,
        int MaxBytes = ReadDefaultMaxBytes);

    /// <summary>Validated fs_read arguments after defaulting.</summary>
    public sealed record FsReadArgs(string FilePath, int Offset, int Limit);

    /// <summary>Validated fs_write arguments; content passes through untouched.</summary>
    public sealed record FsWriteArgs(string FilePath, string Content);

    /// <summary>Validated edit arguments after defaulting; <c>ReplaceAll</c> defaults to false.</summary>
    public sealed record FsEditArgs(string FilePath, string OldString, string NewString, bool ReplaceAll);

    /// <summary>Canonical edit output (port of the TS edit tool's outcome).</summary>
    public sealed record FsEditResult(
        [property: JsonPropertyName("path")] string Path,
        [property: JsonPropertyName("before")] string Before,
        [property: JsonPropertyName("after")] string After,
        [property: JsonPropertyName("requestedPath")] string? RequestedPath = null);

    /// <summary>Resolved read window; the consumer applies its defaults and caps before calling.</summary>
    public sealed record FsReadWindow(int Offset, int Limit, int MaxLineLength, int MaxBytes);

    /// <summary>One line returned from a text file.</summary>
    public sealed record FsFileTextLine([property: JsonPropertyName("number")] int Number, [property: JsonPropertyName("text")] string Text);

    /// <summary>Canonical fs_read output (the TS read tool's outcome).</summary>
    public sealed record FsReadOutcome(
        [property: JsonPropertyName("path")] string Path,
        [property: JsonPropertyName("offset")] int Offset,
        [property: JsonPropertyName("lines")] IReadOnlyList<FsFileTextLine> Lines,
        [property: JsonPropertyName("totalLines")] int TotalLines);

    /// <summary>Canonical fs_write output (the TS write tool's outcome).</summary>
    public sealed record FsWriteResult(
        [property: JsonPropertyName("path")] string Path,
        [property: JsonPropertyName("operation")] string Operation,
        [property: JsonPropertyName("before")] string? Before,
        [property: JsonPropertyName("after")] string After,
        [property: JsonPropertyName("requestedPath")] string? RequestedPath = null);

    /// <summary>The windowed result <see cref="BuildWindow"/> produces from a file's decoded text.</summary>
    public sealed record FsWindowResult(IReadOnlyList<FsFileTextLine> Lines, int TotalLines, bool TruncatedByBytes);

    /// <summary>Render model for <see cref="FormatReadOutput"/>.</summary>
    public sealed record FsReadRenderModel(int Offset, IReadOnlyList<FsFileTextLine> Lines, int TotalLines, bool TruncatedByBytes);

    /// <summary>Canonical read_image output (port of the TS read-image tool's outcome).</summary>
    public sealed record FsImageReadResult(
        [property: JsonPropertyName("path")] string Path,
        [property: JsonPropertyName("image")] ImageAttachment Image);

    /// <summary>
    /// Build the read_image ToolDefinition (port of tool-fs read-image minus the attachment
    /// normalization seams): the extension gate, the calling route's declared image modality,
    /// the presence/absence observation, and the committed sha256 attachment with its parsed
    /// dimensions; Render projects the text envelope plus the image block.
    /// </summary>
    public static ToolDefinition ReadImage(IFileSystemService fs, Dsh.Attachment.IAttachmentService attachments, Dsh.Llm.LlmRuntime llm)
    {
        ArgumentNullException.ThrowIfNull(fs);
        ArgumentNullException.ThrowIfNull(attachments);
        ArgumentNullException.ThrowIfNull(llm);
        return new ToolDefinition(
            Name: "read_image",
            Description: "Read a PNG/JPEG/WebP/GIF file and return the image itself.",
            Parameters: JsonSerializer.SerializeToElement(JsonNode.Parse(ReadImageParametersSchemaJson)!),
            OutputSchema: JsonSerializer.SerializeToElement(JsonNode.Parse(ReadImageOutputSchemaJson)!),
            Execute: async (args, context) =>
            {
                var filePath = args.TryGetProperty("file_path", out var pathValue) ? pathValue.GetString() ?? string.Empty : string.Empty;
                if (filePath.Trim().Length == 0)
                {
                    throw new ArgumentException("file_path must be a non-empty string");
                }
                var extension = Path.GetExtension(filePath).ToLowerInvariant();
                var mediaType = extension switch
                {
                    ".png" => "image/png",
                    ".jpg" or ".jpeg" => "image/jpeg",
                    ".webp" => "image/webp",
                    ".gif" => "image/gif",
                    _ => null,
                };
                if (mediaType is null)
                {
                    throw new ArgumentException($"cannot read \"{filePath}\": read_image only accepts PNG/JPEG/WebP/GIF paths");
                }
                // The route gate: the exact calling model must declare image input.
                var (provider, model) = RouteOf(context.Session);
                if (provider is null || model is null)
                {
                    throw new InvalidOperationException($"cannot read \"{filePath}\" as an image: the current model route could not be resolved");
                }
                var metadata = llm.ResolveModelMetadata(provider, model);
                if (metadata?.InputModalities is not { } modalities || !modalities.Contains("image"))
                {
                    throw new InvalidOperationException(
                        $"cannot read \"{filePath}\" as an image: model \"{model}\" does not declare image input; switch to an image-capable model to read images");
                }
                var spec = fs.ResolveRead(new FsReadRequest(filePath));
                var info = await fs.StatAsync(fs.ResolveStat(new FsStatRequest(filePath)), context.CancellationToken).ConfigureAwait(false);
                if (info is null)
                {
                    throw new FsError($"cannot read \"{spec.Target.DisplayPath}\": not found", FsErrorCodes.NotFound);
                }
                if (info.Type != FsPathType.File)
                {
                    throw new FsError($"cannot read \"{spec.Target.DisplayPath}\": not a regular file", FsErrorCodes.NotRegularFile);
                }
                var raw = await fs.ReadBytesAsync(fs.ResolveReadBytes(new FsReadBytesRequest(filePath, ReadImageMaxBytes)), context.CancellationToken).ConfigureAwait(false);
                var image = attachments.SaveImage(raw, mediaType, Path.GetFileName(spec.Target.DisplayPath));
                return JsonSerializer.SerializeToElement(new FsImageReadResult(spec.Target.DisplayPath, image));
            },
            Render: (_, value) =>
            {
                var result = JsonSerializer.Deserialize<FsImageReadResult>(value)!;
                return new ContentBlock[]
                {
                    new TextBlock(FormatImageReadOutput(result.Path, result.Image)),
                    new ImageBlock(result.Image),
                };
            },
            PersistMeta: false);
    }

    /// <summary>The current session's last routed provider/model, or nulls.</summary>
    private static (string? Provider, string? Model) RouteOf(Dsh.Session.Session? session)
        => session is null
            ? (null, null)
            : session.Events.OfType<RequestHeaderEvent>().Select(evt => evt.Header.Config).LastOrDefault() is { } config
                ? (config.Provider, config.Model)
                : (null, null);

    /// <summary>Format an image read as the model-facing envelope (port of read-image formatImageReadOutput).</summary>
    public static string FormatImageReadOutput(string displayPath, ImageAttachment image)
        => $"<path>{displayPath}</path>\n<type>image</type>\n<content>\n{image.MediaType} image, {image.Width}x{image.Height} px, {image.Bytes} bytes\n</content>";

    /// <summary>Byte cap for one read_image payload (the corpus images are tiny; the cap mirrors the TS per-message bound).</summary>
    public const int ReadImageMaxBytes = 20 * 1024 * 1024;

    /// <summary>
    /// Build the fs_read ToolDefinition. Execute validates the model arguments, resolves and
    /// stats the target (recording the presence/absence observation), reads the whole text,
    /// windows it, and returns the canonical {path, offset, lines, totalLines} value; Render
    /// projects it to the line-numbered envelope.
    /// </summary>
    public static ToolDefinition Read(IFileSystemService fs, FsReadToolCaps? caps = null, FsObservations? observations = null)
    {
        ArgumentNullException.ThrowIfNull(fs);
        var resolved = caps ?? new FsReadToolCaps();
        return new ToolDefinition(
            Name: "read",
            Description: "Read a UTF-8 text file and return line-numbered content.",
            Parameters: ReadParameters(resolved),
            OutputSchema: JsonSerializer.SerializeToElement(JsonNode.Parse(ReadOutputSchemaJson)!),
            Execute: async (args, context) =>
            {
                var input = ParseReadArgs(args, resolved.Limit);
                var spec = fs.ResolveRead(new FsReadRequest(input.FilePath));
                var info = await fs.StatAsync(fs.ResolveStat(new FsStatRequest(input.FilePath)), context.CancellationToken).ConfigureAwait(false);
                if (info is null)
                {
                    observations?.Observe(context.Session?.Id.Value, spec.Target.TargetKey.Value,
                        new FsObserved(FsObservedKind.Absent, new FsVersion("")));
                    throw new FsError($"cannot read \"{spec.Target.DisplayPath}\": not found", FsErrorCodes.NotFound);
                }
                if (info.Type != FsPathType.File)
                {
                    throw new FsError($"cannot read \"{spec.Target.DisplayPath}\": not a regular file", FsErrorCodes.NotRegularFile);
                }
                observations?.Observe(context.Session?.Id.Value, spec.Target.TargetKey.Value,
                    new FsObserved(FsObservedKind.Present, info.Version));
                var text = await fs.ReadTextAsync(spec, context.CancellationToken).ConfigureAwait(false);
                var window = BuildWindow(text, new FsReadWindow(input.Offset, input.Limit, resolved.MaxLineLength, resolved.MaxBytes), spec.Target.DisplayPath);
                return JsonSerializer.SerializeToElement(new FsReadOutcome(spec.Target.DisplayPath, input.Offset, window.Lines, window.TotalLines));
            },
            Render: (args, value) =>
            {
                var input = ParseReadArgs(args, resolved.Limit);
                var outcome = JsonSerializer.Deserialize<FsReadOutcome>(value)!;
                var endLine = outcome.Lines.Count > 0 ? outcome.Lines[^1].Number : Math.Max(0, outcome.Offset - 1);
                var truncatedByBytes = outcome.Lines.Count < input.Limit && endLine < outcome.TotalLines;
                var text = FormatReadOutput(outcome.Path, new FsReadRenderModel(outcome.Offset, outcome.Lines, outcome.TotalLines, truncatedByBytes));
                return new ContentBlock[] { new TextBlock(text) };
            });
    }

    /// <summary>
    /// Build the fs_write ToolDefinition. Execute validates the model arguments, resolves the
    /// write spec under the observation-policy intent (replaceIfVersion for an observed-present
    /// target, createIfAbsent otherwise), runs the provider write with guarded-mutation error
    /// remediation, records the resulting observation, and returns the canonical {path,
    /// operation, before, after} value; Render projects it to the Created/Updated confirmation
    /// envelope.
    /// </summary>
    public static ToolDefinition Write(IFileSystemService fs, FsObservations? observations = null)
    {
        ArgumentNullException.ThrowIfNull(fs);
        return new ToolDefinition(
            Name: "write",
            Description: "Create or fully replace a UTF-8 text file.",
            Parameters: JsonSerializer.SerializeToElement(JsonNode.Parse(WriteParametersSchemaJson)!),
            OutputSchema: JsonSerializer.SerializeToElement(JsonNode.Parse(WriteOutputSchemaJson)!),
            Execute: async (args, context) =>
            {
                var input = ParseWriteArgs(args);
                var sessionId = context.Session?.Id.Value;
                var spec = fs.ResolveWrite(new FsWriteRequest(input.FilePath, input.Content));
                FsWriteIntent intent;
                if (observations is null)
                {
                    intent = new FsUnconditionalIntent();
                }
                else if (observations.Get(sessionId, spec.Target.TargetKey.Value) is { Kind: FsObservedKind.Present } observed)
                {
                    intent = new FsReplaceIfVersionIntent(observed.Version);
                }
                else
                {
                    intent = new FsCreateIfAbsentIntent();
                }
                spec = fs.ResolveWrite(new FsWriteRequest(input.FilePath, input.Content, intent));
                FsWriteOutcome outcome;
                try
                {
                    outcome = await fs.WriteTextAsync(spec, context.CancellationToken).ConfigureAwait(false);
                }
                catch (FsError error)
                {
                    throw RemediateFsError(error);
                }
                observations?.Observe(sessionId, spec.Target.TargetKey.Value,
                    new FsObserved(FsObservedKind.Present, outcome.Version));
                return JsonSerializer.SerializeToElement(new FsWriteResult(spec.Target.DisplayPath, outcome.Operation, outcome.Before, outcome.After, input.FilePath));
            },
            Render: (_, value) =>
            {
                var result = JsonSerializer.Deserialize<FsWriteResult>(value)!;
                return new ContentBlock[] { new TextBlock(FormatWriteOutput(result.Path, result.Operation)) };
            },
            // The TS write tool's durable meta is the {diffs} presentation payload: empty for a
            // create or an undiffable overwrite (before null), one {path, oldText, newText} entry
            // per replaced hunk otherwise (computeHunkDiffs over the LF-normalized basis).
            MetaOf: value =>
            {
                var result = JsonSerializer.Deserialize<FsWriteResult>(value)!;
                var diffs = new JsonArray();
                if (result.Operation != "create" && result.Before is not null)
                {
                    foreach (var diff in HunkDiffs.Compute(result.RequestedPath ?? result.Path, result.Before, result.After))
                    {
                        diffs.Add(new JsonObject
                        {
                            ["path"] = diff.Path,
                            ["oldText"] = diff.OldText,
                            ["newText"] = diff.NewText,
                        });
                    }
                }
                return JsonSerializer.SerializeToElement(new JsonObject { ["diffs"] = diffs });
            });
    }

    /// <summary>
    /// Build the edit ToolDefinition. Execute validates the model arguments, resolves the edit
    /// spec under the observation-policy guard (an unobserved target refuses with FS_NOT_OBSERVED,
    /// a confirmed-absent target with FS_NOT_FOUND), runs the provider edit with guarded-mutation
    /// error remediation, records the resulting observation, and returns the canonical {path,
    /// before, after} value; Render projects it to the Claude-style confirmation sentence and the
    /// durable meta carries the applied hunk diffs.
    /// </summary>
    public static ToolDefinition Edit(IFileSystemService fs, FsObservations? observations = null)
    {
        ArgumentNullException.ThrowIfNull(fs);
        return new ToolDefinition(
            Name: "edit",
            Description: "Edit an existing UTF-8 text file by replacing literal text.",
            Parameters: JsonSerializer.SerializeToElement(JsonNode.Parse(EditParametersSchemaJson)!),
            OutputSchema: JsonSerializer.SerializeToElement(JsonNode.Parse(EditOutputSchemaJson)!),
            Execute: async (args, context) =>
            {
                var input = ParseEditArgs(args);
                var sessionId = context.Session?.Id.Value;
                var spec = fs.ResolveEdit(new FsEditRequest(input.FilePath, input.OldString, input.NewString, input.ReplaceAll));
                FsEditOutcome outcome;
                try
                {
                    var version = observations is null ? null : EditGuard(observations, sessionId, spec.Target);
                    spec = fs.ResolveEdit(new FsEditRequest(input.FilePath, input.OldString, input.NewString, input.ReplaceAll, version));
                    outcome = await fs.EditTextAsync(spec, context.CancellationToken).ConfigureAwait(false);
                }
                catch (FsError error)
                {
                    throw RemediateFsError(error);
                }
                observations?.Observe(sessionId, spec.Target.TargetKey.Value,
                    new FsObserved(FsObservedKind.Present, outcome.Version));
                return JsonSerializer.SerializeToElement(new FsEditResult(spec.Target.DisplayPath, outcome.Before, outcome.After, input.FilePath));
            },
            Render: (args, value) =>
            {
                var input = ParseEditArgs(args);
                var result = JsonSerializer.Deserialize<FsEditResult>(value)!;
                return new ContentBlock[] { new TextBlock(FormatEditOutput(result.Path, input.ReplaceAll)) };
            },
            // The TS edit tool's durable meta is the {diffs} presentation payload: one
            // {path, oldText, newText} entry per applied hunk (computeHunkDiffs over the
            // LF-normalized before/after basis).
            MetaOf: value =>
            {
                var result = JsonSerializer.Deserialize<FsEditResult>(value)!;
                var diffs = new JsonArray();
                foreach (var diff in HunkDiffs.Compute(result.RequestedPath ?? result.Path, result.Before, result.After))
                {
                    diffs.Add(new JsonObject
                    {
                        ["path"] = diff.Path,
                        ["oldText"] = diff.OldText,
                        ["newText"] = diff.NewText,
                    });
                }
                return JsonSerializer.SerializeToElement(new JsonObject { ["diffs"] = diffs });
            });
    }

    /// <summary>
    /// Derive the edit version guard from the observation state (port of the fs-observation-policy
    /// <c>editIntent</c>): an unseen target refuses with FS_NOT_OBSERVED, a confirmed-absent target
    /// with FS_NOT_FOUND, and a present target supplies its observed version as the CAS basis.
    /// </summary>
    private static FsVersion? EditGuard(FsObservations observations, string? sessionId, FsTarget target)
    {
        var observed = observations.Get(sessionId, target.TargetKey.Value);
        if (observed is null)
        {
            throw new FsError($"edit requires reading \"{target.DisplayPath}\" first", FsErrorCodes.NotObserved);
        }
        if (observed.Kind == FsObservedKind.Absent)
        {
            throw new FsError($"cannot edit \"{target.DisplayPath}\": not found", FsErrorCodes.NotFound);
        }
        return observed.Version;
    }

    /// <summary>Validate constraints the read schema cannot express; offset/limit must be positive integers when given.</summary>
    public static FsReadArgs ParseReadArgs(JsonElement args, int maxLimit)
    {
        var filePath = args.TryGetProperty("file_path", out var filePathElement) ? filePathElement.GetString() ?? string.Empty : string.Empty;
        if (filePath.Trim().Length == 0)
        {
            throw new ArgumentException("file_path must be a non-empty string");
        }
        var offset = args.TryGetProperty("offset", out var offsetElement) ? ParsePositiveInteger(offsetElement, "offset") : 1;
        var limit = args.TryGetProperty("limit", out var limitElement) ? ParsePositiveInteger(limitElement, "limit") : maxLimit;
        if (limit > maxLimit)
        {
            throw new ArgumentException($"limit must be less than or equal to {maxLimit}");
        }
        return new FsReadArgs(filePath, offset, limit);
    }

    /// <summary>Validate fs_write arguments: only a non-blank file_path — an empty content is legitimate.</summary>
    public static FsWriteArgs ParseWriteArgs(JsonElement args)
    {
        var filePath = args.TryGetProperty("file_path", out var filePathElement) ? filePathElement.GetString() ?? string.Empty : string.Empty;
        if (filePath.Trim().Length == 0)
        {
            throw new ArgumentException("file_path must be a non-empty string");
        }
        var content = args.TryGetProperty("content", out var contentElement) ? contentElement.GetString() ?? string.Empty : string.Empty;
        return new FsWriteArgs(filePath, content);
    }

    /// <summary>Validate edit arguments: a non-blank file_path, a non-empty old_string, and differing strings (the TS parseEditArgs).</summary>
    public static FsEditArgs ParseEditArgs(JsonElement args)
    {
        var filePath = args.TryGetProperty("file_path", out var filePathElement) ? filePathElement.GetString() ?? string.Empty : string.Empty;
        if (filePath.Trim().Length == 0)
        {
            throw new ArgumentException("file_path must be a non-empty string");
        }
        var oldString = args.TryGetProperty("old_string", out var oldElement) ? oldElement.GetString() ?? string.Empty : string.Empty;
        if (oldString.Length == 0)
        {
            throw new ArgumentException("old_string must be a non-empty string");
        }
        var newString = args.TryGetProperty("new_string", out var newElement) ? newElement.GetString() ?? string.Empty : string.Empty;
        if (oldString == newString)
        {
            throw new ArgumentException("old_string and new_string must differ");
        }
        var replaceAll = args.TryGetProperty("replace_all", out var replaceElement) && replaceElement.ValueKind == JsonValueKind.True;
        return new FsEditArgs(filePath, oldString, newString, replaceAll);
    }

    /// <summary>
    /// Build one window from a file's whole decoded text, enforcing line and byte caps while
    /// still scanning to an exact total line count, and throwing FS_NOT_FOUND when the requested
    /// offset is past EOF (port of read-render.ts buildWindow over a single whole-file chunk).
    /// </summary>
    public static FsWindowResult BuildWindow(string text, FsReadWindow request, string displayPath)
    {
        var acc = new WindowAccumulator();
        var lineBufferCap = request.MaxLineLength + 1;
        var lineBuffer = new StringBuilder();
        var start = 0;
        while (true)
        {
            var newline = text.IndexOf('\n', start);
            if (newline < 0) break;
            AppendToLineBuffer(lineBuffer, text.Substring(start, newline - start), lineBufferCap);
            ConsumeLine(acc, StripCarriageReturn(lineBuffer.ToString()), request);
            lineBuffer.Clear();
            start = newline + 1;
        }
        AppendToLineBuffer(lineBuffer, text.Substring(start), lineBufferCap);
        if (lineBuffer.Length > 0)
        {
            ConsumeLine(acc, StripCarriageReturn(lineBuffer.ToString()), request);
        }
        return Finish(acc, request, displayPath);
    }

    /// <summary>Format a read outcome as one OpenCode-style line-numbered text block body (port of read-render.ts formatReadOutput).</summary>
    public static string FormatReadOutput(string displayPath, FsReadRenderModel model)
    {
        var endLine = model.Lines.Count > 0 ? model.Lines[^1].Number : Math.Max(0, model.Offset - 1);
        string footer;
        if (model.TruncatedByBytes)
        {
            footer = $"(Output capped. Showing lines {model.Offset}-{endLine}. Use offset={endLine + 1} to continue.)";
        }
        else if (endLine < model.TotalLines)
        {
            footer = $"(Showing lines {model.Offset}-{endLine} of {model.TotalLines}. Use offset={endLine + 1} to continue.)";
        }
        else
        {
            footer = $"(End of file - total {model.TotalLines} lines)";
        }
        var body = model.Lines.Count > 0
            ? $"{string.Join('\n', model.Lines.Select(line => $"{line.Number}: {line.Text}"))}\n\n{footer}"
            : footer;
        return $"<path>{displayPath}</path>\n<type>file</type>\n<content>\n{body}\n</content>";
    }

    /// <summary>Format a write outcome as one model-facing confirmation envelope (port of write.ts formatWriteOutput).</summary>
    public static string FormatWriteOutput(string displayPath, string operation)
    {
        var verb = operation == "create" ? "Created" : "Updated";
        return $"<path>{displayPath}</path>\n<type>file</type>\n<content>\n{verb} file\n</content>";
    }

    /// <summary>Format an edit success as one Claude-style confirmation sentence (port of edit.ts formatEditOutput).</summary>
    public static string FormatEditOutput(string displayPath, bool replaceAll)
        => replaceAll
            ? $"The file {displayPath} has been updated. All occurrences were successfully replaced."
            : $"The file {displayPath} has been updated successfully.";

    /// <summary>
    /// Append the correct recovery instruction to a guarded-mutation failure's message (port of
    /// tool-fs error.ts remediateFsError): FS_STALE_VERSION recovers only by re-reading,
    /// FS_NOT_OBSERVED by reading. The code is preserved and anything else passes through.
    /// </summary>
    public static FsError RemediateFsError(FsError error)
    {
        return error.Code switch
        {
            FsErrorCodes.StaleVersion => new FsError($"{error.Message} — re-read the file, then retry", error.Code, error),
            FsErrorCodes.NotObserved => new FsError($"{error.Message} — read the file, then retry", error.Code, error),
            _ => error,
        };
    }

    private static int ParsePositiveInteger(JsonElement value, string name)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var parsed) || parsed < 1)
        {
            throw new ArgumentException($"{name} must be a positive integer");
        }
        return parsed;
    }

    private static JsonElement ReadParameters(FsReadToolCaps caps)
    {
        var root = new JsonObject
        {
            ["file_path"] = new JsonObject
            {
                ["type"] = "string",
                ["required"] = true,
                ["description"] = "Path to read, resolved by the filesystem backend.",
            },
            ["offset"] = new JsonObject
            {
                ["type"] = "number",
                ["description"] = "1-based first line to return. Defaults to 1.",
            },
            ["limit"] = new JsonObject
            {
                ["type"] = "number",
                ["description"] = $"Maximum number of lines to return. Defaults to {caps.Limit}.",
            },
        };
        return JsonSerializer.SerializeToElement(root);
    }

    private const string ReadOutputSchemaJson =
        "{\"type\":\"object\",\"additionalProperties\":false,\"properties\":{\"path\":{\"type\":\"string\",\"required\":true},\"offset\":{\"type\":\"integer\",\"required\":true},\"lines\":{\"type\":\"array\",\"required\":true,\"items\":{\"type\":\"object\",\"additionalProperties\":false,\"properties\":{\"number\":{\"type\":\"integer\",\"required\":true},\"text\":{\"type\":\"string\",\"required\":true}}}},\"totalLines\":{\"type\":\"integer\",\"required\":true}}}";

    private const string WriteParametersSchemaJson =
        "{\"file_path\":{\"type\":\"string\",\"required\":true,\"description\":\"Path to write, resolved by the filesystem backend.\"},\"content\":{\"type\":\"string\",\"required\":true,\"description\":\"Full UTF-8 text content to write.\"}}";

    private const string WriteOutputSchemaJson =
        "{\"type\":\"object\",\"additionalProperties\":false,\"properties\":{\"path\":{\"type\":\"string\",\"required\":true},\"operation\":{\"type\":\"string\",\"required\":true,\"enum\":[\"create\",\"update\"]},\"before\":{\"required\":true,\"oneOf\":[{\"type\":\"string\"},{\"type\":\"null\"}]},\"after\":{\"type\":\"string\",\"required\":true}}}";

    private const string EditParametersSchemaJson =
        "{\"file_path\":{\"type\":\"string\",\"required\":true,\"description\":\"Path to edit, resolved by the filesystem backend.\"},\"old_string\":{\"type\":\"string\",\"required\":true,\"description\":\"Literal text to replace. Must match exactly.\"},\"new_string\":{\"type\":\"string\",\"required\":true,\"description\":\"Literal replacement text. Use an empty string to delete the match.\"},\"replace_all\":{\"type\":\"boolean\",\"description\":\"Replace all matches. Defaults to false; when false, old_string must appear exactly once.\"}}";

    private const string EditOutputSchemaJson =
        "{\"type\":\"object\",\"additionalProperties\":false,\"properties\":{\"path\":{\"type\":\"string\",\"required\":true},\"before\":{\"type\":\"string\",\"required\":true},\"after\":{\"type\":\"string\",\"required\":true}}}";

    private const string ReadImageParametersSchemaJson =
        "{\"file_path\":{\"type\":\"string\",\"required\":true,\"description\":\"Path to the image file, resolved by the filesystem backend.\"}}";

    private const string ReadImageOutputSchemaJson =
        "{\"type\":\"object\",\"additionalProperties\":false,\"properties\":{\"path\":{\"type\":\"string\",\"required\":true},\"image\":{\"type\":\"object\",\"additionalProperties\":false,\"required\":true,\"properties\":{\"attachmentId\":{\"type\":\"string\",\"required\":true},\"mediaType\":{\"type\":\"string\",\"required\":true},\"bytes\":{\"type\":\"integer\",\"required\":true},\"width\":{\"type\":\"integer\",\"required\":true},\"height\":{\"type\":\"integer\",\"required\":true},\"name\":{\"type\":\"string\"}}}}}";

    private sealed class WindowAccumulator
    {
        public List<FsFileTextLine> Lines { get; } = new();

        public int TotalLines { get; set; }

        public int OutputBytes { get; set; }

        public bool TruncatedByBytes { get; set; }
    }

    private static void AppendToLineBuffer(StringBuilder buffer, string segment, int cap)
    {
        if (buffer.Length >= cap) return;
        buffer.Append(segment);
        if (buffer.Length > cap) buffer.Length = cap;
    }

    private static void ConsumeLine(WindowAccumulator acc, string rawLine, FsReadWindow request)
    {
        acc.TotalLines += 1;
        if (acc.TruncatedByBytes || acc.TotalLines < request.Offset || acc.Lines.Count >= request.Limit) return;
        var text = TruncateLine(rawLine, request.MaxLineLength);
        var bytes = Encoding.UTF8.GetByteCount(text) + (acc.Lines.Count > 0 ? 1 : 0);
        if (acc.OutputBytes + bytes > request.MaxBytes)
        {
            acc.TruncatedByBytes = true;
            return;
        }
        acc.OutputBytes += bytes;
        acc.Lines.Add(new FsFileTextLine(acc.TotalLines, text));
    }

    private static string TruncateLine(string line, int maxLineLength)
        => line.Length > maxLineLength ? $"{line.Substring(0, maxLineLength)}... (line truncated to {maxLineLength} chars)" : line;

    private static string StripCarriageReturn(string line)
        => line.EndsWith('\r') ? line.Substring(0, line.Length - 1) : line;

    private static FsWindowResult Finish(WindowAccumulator acc, FsReadWindow request, string displayPath)
    {
        if (!acc.TruncatedByBytes && request.Offset > acc.TotalLines && !(acc.TotalLines == 0 && request.Offset == 1))
        {
            throw new FsError($"offset {request.Offset} is out of range for \"{displayPath}\" ({acc.TotalLines} lines)", FsErrorCodes.NotFound);
        }
        return new FsWindowResult(acc.Lines.ToArray(), acc.TotalLines, acc.TruncatedByBytes);
    }
}
