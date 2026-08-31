using System.Text;

namespace Dsh.Terminal;

/// <summary>
/// Terminal output sanitizer (port of the TS <c>TerminalSanitizer</c>): strips CSI/OSC/short
/// escape sequences with split-sequence carry across chunks, normalizes CRLF/CR to <c>\n</c>,
/// removes BEL, and recognizes the controlled-prompt marker <c>ESC ] 133;D; ... BEL|ESC \</c>.
/// The marker recognition drives send readiness: a completed marker plus the prompt text after it
/// proves the shell returned control.
/// </summary>
public sealed class TerminalSanitizer
{
    private enum State
    {
        Normal,
        Escape,
        Csi,
        Osc,
        EscapeAfterOsc,
    }

    private readonly StringBuilder _osc = new();
    private State _state = State.Normal;
    private bool _markerPending;

    /// <summary>
    /// Sanitize one decoded chunk and return the display text. The marker state is consumed
    /// through <see cref="CompletePromptMarker"/>.
    /// </summary>
    public string Sanitize(string chunk)
    {
        var output = new StringBuilder(chunk.Length);
        for (var index = 0; index < chunk.Length; index++)
        {
            var ch = chunk[index];
            switch (_state)
            {
                case State.Normal:
                    switch (ch)
                    {
                        case '\r':
                            output.Append('\n');
                            if (index + 1 < chunk.Length && chunk[index + 1] == '\n') index++;
                            break;
                        case '\u001b':
                            _state = State.Escape;
                            break;
                        case '\a':
                            break; // BEL is removed
                        default:
                            output.Append(ch);
                            break;
                    }
                    break;
                case State.Escape:
                    if (ch == '[')
                    {
                        _state = State.Csi;
                    }
                    else if (ch == ']')
                    {
                        _state = State.Osc;
                        _osc.Clear();
                    }
                    else
                    {
                        _state = State.Normal; // short escape dropped
                    }
                    break;
                case State.Csi:
                    if (ch is >= '@' and <= '~')
                    {
                        _state = State.Normal; // final byte: the whole sequence is dropped
                    }
                    break;
                case State.Osc:
                    if (ch == '\a')
                    {
                        EndOsc();
                    }
                    else if (ch == '\u001b')
                    {
                        _state = State.EscapeAfterOsc;
                    }
                    else
                    {
                        _osc.Append(ch);
                    }
                    break;
                case State.EscapeAfterOsc:
                    if (ch == '\\')
                    {
                        EndOsc(); // ESC \ is the ST terminator
                    }
                    else
                    {
                        _state = State.Normal; // stray ESC inside OSC: drop the sequence
                    }
                    break;
            }
        }
        return output.ToString();
    }

    /// <summary>
    /// Whether a completed prompt marker is waiting for its prompt text. The session confirms the
    /// marker by observing the prompt text in subsequent output.
    /// </summary>
    public bool IsPromptMarkerPending => _markerPending;

    /// <summary>Consume a completed marker, returning whether one was pending.</summary>
    public bool TakePromptMarker()
    {
        var pending = _markerPending;
        _markerPending = false;
        return pending;
    }

    /// <summary>Reset all state (a fresh session or a poisoned stream).</summary>
    public void Reset()
    {
        _state = State.Normal;
        _osc.Clear();
        _markerPending = false;
    }

    private void EndOsc()
    {
        // The controlled prompt marker payload is exactly "133;D;" (optionally with params).
        var payload = _osc.ToString();
        var starts = payload.StartsWith("133;D;", StringComparison.Ordinal)
            || payload.StartsWith("133;D", StringComparison.Ordinal);
        if (starts) _markerPending = true;
        _state = State.Normal;
    }
}
