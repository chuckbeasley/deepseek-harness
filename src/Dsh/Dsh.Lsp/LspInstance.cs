using System.Text.Json;
using System.Text.Json.Nodes;

namespace Dsh.Lsp;

/// <summary>Everything an instance needs beyond the connection spec.</summary>
public sealed record LspInstanceSpec(
    string Command,
    IReadOnlyList<string> Args,
    string Cwd,
    IReadOnlyDictionary<string, string> Env,
    int MaxMessageBytes,
    int MaxStderrBytes,
    int KillGraceMs,
    JsonElement? Configuration,
    string WorkspaceUri,
    JsonElement? InitializationOptions,
    int ShutdownTimeoutMs)
    : LspConnectionSpec(Command, Args, Cwd, Env, MaxMessageBytes, MaxStderrBytes, KillGraceMs, Configuration);

/// <summary>
/// A single initialized server process (port of <c>instance.ts</c>): the initialize handshake, the
/// serialized abortable query queue, the transient didOpen→request→didClose lifecycle, and bounded
/// teardown. Queries serialize through one queue so a cancellation that fails to stop the server can
/// terminate it without killing unrelated work; distinct instances run in parallel.
/// </summary>
public sealed class LspInstance
{
    /// <summary>The client capabilities advertised at initialize; the server's returned capabilities are authoritative.</summary>
    private static readonly JsonElement ClientCapabilities = JsonSerializer.SerializeToElement(new
    {
        general = new { positionEncodings = new[] { "utf-16" } },
        workspace = new { workspaceFolders = true, configuration = true },
        textDocument = new
        {
            synchronization = new { dynamicRegistration = false },
            hover = new { contentFormat = new[] { "markdown", "plaintext" } },
            definition = new { linkSupport = true },
            implementation = new { linkSupport = true },
            references = new { },
        },
    });

    /// <summary>Server→client request methods this host acknowledges with an empty result (no dynamic registration).</summary>
    private static readonly HashSet<string> LifecycleNoopMethods = new(StringComparer.Ordinal)
    {
        "window/workDoneProgress/create",
        "client/registerCapability",
        "client/unregisterCapability",
    };

    private readonly LspInstanceSpec _spec;
    private readonly LspConnection _connection;
    private JsonElement? _capabilities;
    private Task _queue = Task.CompletedTask;
    private volatile bool _disposed;
    private volatile bool _processClosed;
    private readonly object _teardownGate = new();
    private Task? _teardownPromise;
    private readonly Task _ready;

    /// <summary>
    /// Create the instance: builds the connection, starts the initialize handshake immediately, and
    /// subscribes to process close.
    /// </summary>
    /// <param name="spec">the launch, initialize, and teardown parameters.</param>
    /// <param name="spawner">the process-handle seam's spawn function.</param>
    /// <param name="writer">optional connection writer used by transport conformance tests.</param>
    public LspInstance(LspInstanceSpec spec, LspConnectionSpawner spawner, LspConnectionWriter? writer = null)
    {
        _spec = spec;
        _connection = new LspConnection(spec, spawner, AnswerServerRequestAsync, writer);
        _ready = InitializeAsync();
        // A handshake rejection must not surface as an unhandled rejection before the first query awaits
        // it; queries attach the real handler.
        _ = _ready.ContinueWith(_ => { }, TaskScheduler.Default);
        _ = _connection.Closed.ContinueWith(_ => { _processClosed = true; }, TaskScheduler.Default);
    }

    /// <summary>The underlying connection, for transport-level assertions in tests.</summary>
    internal LspConnection Connection => _connection;

    /// <summary>Synchronous liveness check: true once the process has closed or the instance was disposed.</summary>
    public bool Dead => _processClosed || _disposed || _connection.Failed;

    /// <summary>True only for the connection's retained fatal transport cause.</summary>
    public bool IsTransportFailure(Exception error) => _connection.FailedWith(error);

    /// <summary>
    /// Run one query through the serialized queue. The caller's cancellation is observed during the
    /// queue wait too, so a later timeout can give up on a hung earlier query.
    /// </summary>
    /// <param name="request">the resolved provider query.</param>
    /// <param name="source">the pre-validated, already-read host source.</param>
    /// <param name="ct">optional cancellation for this query's full lifecycle.</param>
    /// <returns>the normalized result.</returns>
    public Task<LspQueryResult> QueryAsync(LspProviderQuery request, HostSource source, CancellationToken ct = default)
    {
        var run = RunAfterQueueAsync(request, source, ct);
        // Keep the tail alive regardless of this query's outcome so the next caller still serializes.
        // The tail follows the ACTUAL prior work (_queue), not the abortable view, so a caller giving up
        // on the wait does not deserialize the queue.
        _queue = _queue.ContinueWith(_ => run, TaskScheduler.Default).Unwrap()
            .ContinueWith(_ => { }, TaskScheduler.Default);
        return run;
    }

    /// <summary>Reject queued work, attempt graceful shutdown/exit, then escalate to tree termination.</summary>
    public Task DisposeAsync() => StartTeardown();

    /// <summary>Publish disposal once and make every caller await the same quiescence boundary.</summary>
    private Task StartTeardown()
    {
        _disposed = true;
        lock (_teardownGate)
        {
            _teardownPromise ??= TearDownAsync();
            return _teardownPromise;
        }
    }

    private async Task<LspQueryResult> RunAfterQueueAsync(LspProviderQuery request, HostSource source, CancellationToken ct)
    {
        await LspAbort.Abortable(_queue, ct);
        try
        {
            return await RunQueryAsync(request, source, ct);
        }
        catch (Exception error)
        {
            if (IsTransportFailure(error)) await StartTeardown();
            throw;
        }
    }

    private async Task InitializeAsync()
    {
        var parameters = new JsonObject
        {
            // A subprocess provider may run in another PID namespace or machine; the host PID would let
            // the server monitor an unrelated process.
            ["processId"] = null,
            ["rootUri"] = _spec.WorkspaceUri,
            ["workspaceFolders"] = new JsonArray(new JsonObject { ["uri"] = _spec.WorkspaceUri, ["name"] = "workspace" }),
            ["capabilities"] = JsonNode.Parse(ClientCapabilities.GetRawText()),
            ["initializationOptions"] = ToNode(_spec.InitializationOptions),
        };
        var initializeResult = await _connection.Request("initialize", JsonSerializer.SerializeToElement(parameters));
        if (!initializeResult.HasValue
            || initializeResult.Value.ValueKind != JsonValueKind.Object
            || !initializeResult.Value.TryGetProperty("capabilities", out var capabilities))
        {
            throw new InvalidOperationException("LSP initialize result was missing capabilities");
        }
        // An omitted encoding defaults to utf-16; any other value is a protocol error we reject here.
        var encoding = capabilities.TryGetProperty("positionEncoding", out var positionEncoding) && positionEncoding.ValueKind == JsonValueKind.String
            ? positionEncoding.GetString()
            : null;
        LspTranslate.NegotiatePositionEncoding(encoding);
        _capabilities = capabilities;
        await _connection.Notify("initialized", JsonSerializer.SerializeToElement(new JsonObject()));
    }

    private async Task<LspQueryResult> RunQueryAsync(LspProviderQuery request, HostSource source, CancellationToken ct)
    {
        if (_disposed) throw new LspError("LSP instance was disposed", "LSP_DISPOSED");
        // The abortable queue wait rejects a pre-aborted signal before runQuery; this is a
        // belt-and-suspenders guard.
        LspAbort.ThrowIfAborted(ct);
        // Observe abort during the handshake wait, and never pool a poisoned instance: if the wait ends
        // in failure — an abort on a still-pending handshake, OR initialize rejecting (utf-8
        // negotiation, malformed result) without the process exiting — tear the instance down so a
        // permanently-rejecting/pending ready can't make every later query fail.
        try
        {
            await LspAbort.Abortable(_ready, ct);
        }
        catch
        {
            if (!Dead) await StartTeardown();
            throw;
        }
        var capabilities = _capabilities;
        if (!capabilities.HasValue) throw new InvalidOperationException("LSP instance is not initialized");
        if (!LspTranslate.SupportsOperation(capabilities.Value, request.Operation))
        {
            throw new LspError($"server does not support {LspTranslate.OperationName(request.Operation)}", "LSP_UNSUPPORTED_OPERATION");
        }
        if (!LspTranslate.SupportsTransientOpen(ReadTextDocumentSync(capabilities.Value)))
        {
            throw new LspError("server does not support the transient textDocument/didOpen this host requires", "LSP_UNSUPPORTED_OPERATION");
        }

        var uri = source.FileUrl;
        var opened = false;
        try
        {
            // Guards an abort landing between the ready wait and didOpen; not deterministically
            // reproducible otherwise.
            LspAbort.ThrowIfAborted(ct);
            try
            {
                var didOpen = new JsonObject
                {
                    ["textDocument"] = new JsonObject
                    {
                        ["uri"] = uri,
                        ["languageId"] = request.LanguageId,
                        ["version"] = 1,
                        ["text"] = source.Text,
                    },
                };
                await LspAbort.Abortable(_connection.Notify("textDocument/didOpen", JsonSerializer.SerializeToElement(didOpen)), ct);
            }
            catch
            {
                // A canceled backpressured write or failed stdin leaves the protocol stream unusable
                // before `opened` can arm the didClose cleanup. Teardown here makes the pool evict the
                // instance.
                await StartTeardown();
                throw;
            }
            opened = true;
            var payload = await SendRequestAsync(request.Operation, uri, request.Position, ct);
            return Normalize(request.Operation, payload);
        }
        finally
        {
            // A disposed or closed instance (for example an aborted request whose server ignored
            // $/cancelRequest) is already tearing down; sending didClose would race that teardown and
            // let the next queued query's document lifecycle overlap the still-active request.
            if (opened && !Dead)
            {
                try
                {
                    var didClose = new JsonObject { ["textDocument"] = new JsonObject { ["uri"] = uri } };
                    await _connection.Notify("textDocument/didClose", JsonSerializer.SerializeToElement(didClose));
                }
                catch
                {
                    // A close-write failure does not replace the settled result/error, but the instance
                    // can no longer be trusted: invalidate it and await bounded process termination.
                    try
                    {
                        await StartTeardown();
                    }
                    catch
                    {
                        // Teardown owns all expected process races; this only preserves the already
                        // settled query outcome if an unexpected cleanup primitive itself rejects.
                    }
                }
            }
        }
    }

    private async Task<JsonElement?> SendRequestAsync(LspOperation operation, string uri, LspPosition position, CancellationToken ct)
    {
        var parameters = new JsonObject
        {
            ["textDocument"] = new JsonObject { ["uri"] = uri },
            ["position"] = new JsonObject { ["line"] = position.Line, ["character"] = position.Character },
        };
        // findReferences always includes declarations: the caller gets no flag and impact analysis
        // never omits the defining site.
        if (operation == LspOperation.FindReferences)
        {
            parameters["context"] = new JsonObject { ["includeDeclaration"] = true };
        }
        var requestId = _connection.PeekNextId();
        var send = _connection.Request(LspTranslate.RequestMethod(operation), JsonSerializer.SerializeToElement(parameters));
        if (!ct.CanBeCanceled) return await send;
        return await RaceAbortAsync(send, requestId, ct);
    }

    /// <summary>
    /// Race a pending request against abort. On abort, send $/cancelRequest and give the server a
    /// bounded grace to acknowledge; if it does not settle in time, invalidate and tear down the
    /// instance so the still-active request cannot overlap the next queued query's document lifecycle.
    /// </summary>
    private async Task<JsonElement?> RaceAbortAsync(Task<JsonElement?> send, long requestId, CancellationToken ct)
    {
        try
        {
            return await LspAbort.Abortable(send, ct);
        }
        catch (Exception error)
        {
            // A server error response with a live signal is NOT an abort.
            if (!ct.IsCancellationRequested) throw;
            _connection.Cancel(requestId);
            // Wait, bounded, for the server to honor the cancellation. If it does not, the request is
            // still running: terminate the instance (disposal awaits process close) so nothing outlives
            // the query. killGraceMs is the same budget as the seam's SIGTERM→SIGKILL window.
            using var grace = new CancellationTokenSource();
            var deadline = Task.Delay(_spec.KillGraceMs, grace.Token);
            try
            {
                var first = await Task.WhenAny(send, deadline);
                if (first == deadline) await StartTeardown();
            }
            finally
            {
                grace.Cancel();
            }
            // Preserve the original abort's stack and classification (CA2200: no plain rethrow).
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
            throw error; // unreachable; satisfies the compiler's definite-return analysis
        }
    }

    private LspQueryResult Normalize(LspOperation operation, JsonElement? payload)
    {
        if (operation == LspOperation.Hover) return new LspHoverResult(LspTranslate.NormalizeHover(payload));
        // The filesystem provider owns URI syntax for the execution platform, which may differ from the
        // harness host. Preserve that coordinate through rendering instead of reparsing spec.Cwd there.
        return new LspLocationsResult(LspTranslate.NormalizeLocations(payload), _spec.WorkspaceUri);
    }

    private Task<JsonElement?> AnswerServerRequestAsync(string method, JsonElement? parameters)
    {
        if (method == "workspace/configuration")
        {
            // Answer every requested item with the one static configuration value.
            var items = 0;
            if (parameters.HasValue
                && parameters.Value.ValueKind == JsonValueKind.Object
                && parameters.Value.TryGetProperty("items", out var itemsElement)
                && itemsElement.ValueKind == JsonValueKind.Array)
            {
                items = itemsElement.GetArrayLength();
            }
            var result = new JsonArray();
            for (var i = 0; i < items; i++) result.Add(ToNode(_spec.Configuration));
            return Task.FromResult<JsonElement?>(JsonSerializer.SerializeToElement(result));
        }
        if (LifecycleNoopMethods.Contains(method))
        {
            // Accept lifecycle bookkeeping requests with an empty result; we register nothing dynamic.
            return Task.FromResult<JsonElement?>(LspJson.NullElement());
        }
        if (method == "workspace/applyEdit")
        {
            // This host never applies edits or runs commands.
            return Task.FromException<JsonElement?>(new InvalidOperationException("workspace/applyEdit is not permitted by this host"));
        }
        return Task.FromException<JsonElement?>(new InvalidOperationException($"unsupported server request: {method}"));
    }

    private async Task TearDownAsync()
    {
        using var shutdownDeadline = LspAbort.Deadline("LSP_SHUTDOWN", _spec.ShutdownTimeoutMs);
        try
        {
            await GracefulShutdownAsync(shutdownDeadline.Token);
        }
        catch
        {
            // Graceful shutdown failed or timed out; process-tree cleanup below remains authoritative.
        }
        await ForceTerminateAsync();
    }

    /// <summary>Best-effort LSP shutdown/exit, including process close, bounded by <paramref name="ct"/>.</summary>
    private async Task GracefulShutdownAsync(CancellationToken ct)
    {
        await LspAbort.Abortable(_connection.Request("shutdown", LspJson.NullElement()), ct, "LSP_SHUTDOWN");
        await _connection.Notify("exit", null);
        await LspAbort.Abortable(_connection.Closed, ct, "LSP_SHUTDOWN");
    }

    /// <summary>
    /// Terminate the tree, then await leader and helper exit. The awaits are unbounded on purpose: the
    /// seam's escalation already committed to the kill, so quiescence — not another timer — is the
    /// postcondition disposal owes its callers.
    /// </summary>
    private async Task ForceTerminateAsync()
    {
        _connection.Terminate();
        await Task.WhenAll(_connection.Closed, _connection.WaitForProcessTreeExit());
    }

    /// <summary>Read the capabilities' <c>textDocumentSync</c> slot for the transient-open check.</summary>
    private static JsonElement? ReadTextDocumentSync(JsonElement capabilities)
    {
        return capabilities.TryGetProperty("textDocumentSync", out var sync) ? sync.Clone() : null;
    }

    /// <summary>Convert a nullable element to a JSON node (null when missing).</summary>
    private static JsonNode? ToNode(JsonElement? element)
        => element.HasValue ? JsonNode.Parse(element.Value.GetRawText()) : null;
}
