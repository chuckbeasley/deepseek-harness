using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Dsh.Agent;
using Dsh.AgentLoop;
using Dsh.Llm;
using Dsh.Session;
using Dsh.Subagent;
using Dsh.Tools;
using AgentLoopService = Dsh.AgentLoop.AgentLoop;

namespace Dsh.Workflow;

/// <summary>
/// The model-facing <c>workflow</c> tool (port of tool-workflow with the recorded script subset
/// interpreted natively — node is not used in the ported version): <c>phase('Title')</c>,
/// <c>const name = await agent('prompt')</c> (optionally <c>, { label: '…' }</c>), and
/// <c>return { ... }</c> statements run in C#,
/// each agent() delegating one fresh one-shot structured child through the agent loop. The
/// durable tool-workflow/* record events fire around the run, and the terminal envelope renders
/// the completed run with the pretty-printed return value.
/// </summary>
public static class WorkflowTool
{
    private const string ParametersSchema =
        "{\"meta\":{\"type\":\"object\",\"required\":true,\"description\":\"The workflow identity: required name and description strings, optional whenToUse and phases array.\"},"
        + "\"script\":{\"type\":\"string\",\"required\":true,\"description\":\"The plain-JS workflow script body (top-level await allowed; NO export const meta statement).\"},"
        + "\"args\":{\"type\":\"object\",\"description\":\"Optional JSON input exposed to the script as the args global.\"}}";

    private static readonly Regex PhaseRegex = new(@"^phase\('([^']*)'\)\s*;?\s*$", RegexOptions.CultureInvariant);
    private static readonly Regex AgentRegex = new(@"^const\s+(\w+)\s*=\s*await\s+agent\('([^']*)'(?:\s*,\s*\{\s*label\s*:\s*'([^']*)'\s*\})?\)\s*;?\s*$", RegexOptions.CultureInvariant);
    private static readonly Regex ReturnRegex = new(@"^return\s+\{(.*)\}\s*;?\s*$", RegexOptions.CultureInvariant);

    /// <summary>Build the tool over the agent loop used to spawn the workflow children.</summary>
    public static ToolDefinition Definition(AgentLoopService loop)
    {
        ArgumentNullException.ThrowIfNull(loop);
        return new ToolDefinition(
            Name: "workflow",
            Description: "Run a JavaScript workflow script that orchestrates subagents at scale. Use this for work that fans out across many independent pieces.",
            Parameters: JsonSerializer.SerializeToElement(JsonNode.Parse(ParametersSchema)!),
            OutputSchema: JsonSerializer.SerializeToElement(JsonNode.Parse("{\"type\":\"object\",\"additionalProperties\":false,\"properties\":{}}")!),
            Execute: (args, context) => ExecuteAsync(loop, args, context),
            Render: (_, value) => new ContentBlock[] { new TextBlock(value.GetString() ?? string.Empty) },
            PersistMeta: false);
    }

    /// <summary>A short display label derived from the prompt when the script passes none.</summary>
    internal static string DefaultLabel(string prompt)
    {
        var newline = prompt.IndexOf('\n');
        var line = newline < 0 ? prompt : prompt[..newline];
        return line.Length <= 48 ? line : line[..47] + "…";
    }

    private static async Task<JsonElement> ExecuteAsync(AgentLoopService loop, JsonElement args, ToolRunContext context)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty("meta", out var meta) || meta.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("workflow arguments must carry a \"meta\" object");
        }
        var name = meta.TryGetProperty("name", out var nameValue) ? nameValue.GetString() ?? string.Empty : string.Empty;
        if (name.Length == 0) throw new ArgumentException("workflow meta.name must be a non-empty string");
        var script = args.TryGetProperty("script", out var scriptValue) ? scriptValue.GetString() ?? string.Empty : string.Empty;
        if (script.Trim().Length == 0) throw new ArgumentException("workflow script must be a non-empty string");

        var session = context.Session
            ?? throw new InvalidOperationException("workflow tool requires a calling session");
        var runId = Guid.NewGuid().ToString("D");
        session.Append(new ToolWorkflowRunStartEvent { RunId = runId, Name = name });
        var interpreter = new Interpreter(loop, session, context, runId);
        try
        {
            var value = await interpreter.RunAsync(script, context.CancellationToken);
            session.Append(new ToolWorkflowRunEndEvent { RunId = runId, StopReason = WorkflowStopReason.Completed });
            var agents = interpreter.AgentsStarted;
            var noun = agents == 1 ? "agent" : "agents";
            var text = $"workflow \"{name}\" completed ({agents} {noun}).\nReturn value:\n{PrettyJson(value)}";
            return JsonSerializer.SerializeToElement(text);
        }
        catch
        {
            session.Append(new ToolWorkflowRunEndEvent { RunId = runId, StopReason = WorkflowStopReason.Error });
            throw;
        }
    }

    /// <summary>Two-space-indented JSON with LF line endings (the recorded spelling).</summary>
    internal static string PrettyJson(JsonObject value)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true, NewLine = "\n" }))
        {
            value.WriteTo(writer);
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>The native script subset interpreter: phase/agent/return with string-literal object values.</summary>
    private sealed class Interpreter
    {
        private readonly AgentLoopService _loop;
        private readonly Dsh.Session.Session _session;
        private readonly ToolRunContext _context;
        private readonly string _runId;
        private readonly Dictionary<string, JsonNode?> _variables = new(StringComparer.Ordinal);
        private string? _phase;

        public Interpreter(AgentLoopService loop, Dsh.Session.Session session, ToolRunContext context, string runId)
        {
            _loop = loop;
            _session = session;
            _context = context;
            _runId = runId;
        }

        public int AgentsStarted { get; private set; }

        public async Task<JsonObject> RunAsync(string script, CancellationToken ct)
        {
            foreach (var rawLine in script.Split('\n'))
            {
                var line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal)) continue;
                var phase = PhaseRegex.Match(line);
                if (phase.Success)
                {
                    _phase = phase.Groups[1].Value;
                    continue;
                }
                var agent = AgentRegex.Match(line);
                if (agent.Success)
                {
                    var label = agent.Groups[3].Success ? agent.Groups[3].Value : DefaultLabel(agent.Groups[2].Value);
                    var value = await RunAgentAsync(agent.Groups[2].Value, label, ct);
                    _variables[agent.Groups[1].Value] = JsonValue.Create(value);
                    continue;
                }
                var ret = ReturnRegex.Match(line);
                if (ret.Success)
                {
                    return ParseObject(ret.Groups[1].Value);
                }
                throw new InvalidOperationException($"workflow script: unsupported statement \"{line}\"");
            }
            throw new InvalidOperationException("workflow script ended without a return statement");
        }

        private async Task<string> RunAgentAsync(string prompt, string label, CancellationToken ct)
        {
            var seq = ++AgentsStarted;
            var (provider, model) = _session.Events.OfType<RequestHeaderEvent>().Select(evt => evt.Header.Config).LastOrDefault() is { } config
                ? (config.Provider, config.Model)
                : (null, null);
            var depth = _session.Header.DelegationDepth + 1;
            if (depth > RalphTool.MaxDelegationDepth)
            {
                throw new InvalidOperationException($"subagent depth {depth} exceeds maxDepth {RalphTool.MaxDelegationDepth}");
            }
            var sessionId = new SessionId(Guid.NewGuid().ToString("D"));
            var options = new AgentOptions
            {
                Provider = provider,
                Model = model,
                Cwd = Environment.CurrentDirectory,
                DelegationDepth = depth,
                ParentSessionId = _session.Id.Value,
                Origin = "subagent",
                SubagentProvider = "spawn",
            };
            var handle = _loop.Create(sessionId, options, source: "subagent");
            try
            {
                _session.Append(new ToolWorkflowAgentStartEvent
                {
                    RunId = _runId,
                    Seq = seq,
                    Label = label,
                    Phase = _phase,
                    ChildId = sessionId.Value,
                });
                var driver = _loop.GetLoop(sessionId)
                    ?? throw new InvalidOperationException("workflow: the child loop was not published");
                var message = new UserMessage
                {
                    Id = new MessageId(Guid.NewGuid().ToString("D")),
                    Content = new ContentBlock[] { new TextBlock(prompt) },
                    Source = new UserSource(),
                };
                driver.Send(message, InboxTarget.NextTurn, wakeup: true);
                await driver.WhenIdleAsync().ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                var child = handle.Agent.Session;
                var text = string.Concat(child.Events
                    .OfType<AssistantMessageEvent>()
                    .SelectMany(evt => evt.Message.Content.OfType<TextBlock>())
                    .Select(block => block.Text));
                _session.Append(new ToolWorkflowAgentEndEvent { RunId = _runId, Seq = seq, Outcome = "completed" });
                return text;
            }
            finally
            {
                handle.Dispose();
            }
        }

        /// <summary>Parse the recorded object literal: identifier shorthands and string-literal values.</summary>
        private JsonObject ParseObject(string body)
        {
            var result = new JsonObject();
            if (body.Trim().Length == 0) return result;
            foreach (var part in body.Split(','))
            {
                var entry = part.Trim();
                if (entry.Length == 0) continue;
                var colon = entry.IndexOf(':');
                if (colon < 0)
                {
                    // Identifier shorthand: the variable's value.
                    result[entry.Trim()] = _variables.GetValueOrDefault(entry.Trim())?.DeepClone();
                    continue;
                }
                var key = entry[..colon].Trim();
                var raw = entry[(colon + 1)..].Trim();
                if (raw.Length >= 2 && raw[0] == '\'' && raw[^1] == '\'')
                {
                    result[key] = raw[1..^1];
                }
                else
                {
                    result[key] = _variables.GetValueOrDefault(raw)?.DeepClone();
                }
            }
            return result;
        }
    }
}