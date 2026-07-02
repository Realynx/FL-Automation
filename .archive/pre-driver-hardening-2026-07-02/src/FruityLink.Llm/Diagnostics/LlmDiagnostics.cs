using System.IO;
using System.Text;
using FruityLink.Core.Abstractions;

namespace FruityLink.Llm.Diagnostics;

/// <summary>
/// Default <see cref="ILlmDiagnostics"/>: keeps the last N calls in memory and appends a rolling
/// per-day log file. Every call gets a one-line summary; failures also log the request/response bodies.
/// </summary>
public sealed class LlmDiagnostics : ILlmDiagnostics
{
    private const int Capacity = 200;

    private readonly object _gate = new();
    private readonly LinkedList<LlmCallRecord> _recent = new();
    private readonly string _logDirectory;

    public LlmDiagnostics(string logDirectory)
    {
        _logDirectory = logDirectory;
        Directory.CreateDirectory(_logDirectory);
    }

    public string LogDirectory => _logDirectory;

    public IReadOnlyList<LlmCallRecord> Recent
    {
        get { lock (_gate) return _recent.ToArray(); }
    }

    public event EventHandler? Changed;

    public void Record(LlmCallRecord record)
    {
        lock (_gate)
        {
            _recent.AddFirst(record);
            while (_recent.Count > Capacity) _recent.RemoveLast();
        }

        AppendToFile(record);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Clear()
    {
        lock (_gate) _recent.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void AppendToFile(LlmCallRecord r)
    {
        try
        {
            var sb = new StringBuilder();
            sb.Append('[').Append(r.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff")).Append("]  ")
              .Append(r.Method).Append(' ').Append(r.Uri)
              .Append("  -> ").Append(r.StatusCode?.ToString() ?? "(no response)")
              .Append("  ").Append(r.ElapsedMs).Append("ms");
            if (r.Attempt > 1) sb.Append("  attempt ").Append(r.Attempt);
            if (!r.Ok) sb.Append("  ERROR: ").Append(r.Error);
            sb.AppendLine();

            if (!r.Ok)
            {
                if (!string.IsNullOrEmpty(r.RequestPreview))
                    sb.Append("  REQUEST: ").AppendLine(r.RequestPreview);
                if (!string.IsNullOrEmpty(r.ResponsePreview))
                    sb.Append("  RESPONSE: ").AppendLine(r.ResponsePreview);
            }

            string path = Path.Combine(_logDirectory, $"llm-{r.Timestamp:yyyyMMdd}.log");
            lock (_gate)
                File.AppendAllText(path, sb.ToString());
        }
        catch
        {
            // Diagnostics must never break the call path.
        }
    }
}
