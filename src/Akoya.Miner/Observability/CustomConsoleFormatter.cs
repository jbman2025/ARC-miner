using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;
using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Akoya.Miner.Observability;

public sealed class CustomConsoleFormatter : ConsoleFormatter
{
    private const string Reset = "\u001b[0m";
    private const string Bold = "\u001b[1m";
    private const string DarkGray = "\u001b[90m";
    private const string Cyan = "\u001b[36m";
    private const string BrightCyan = "\u001b[96m";
    private const string Green = "\u001b[32m";
    private const string BrightGreen = "\u001b[92m";
    private const string Yellow = "\u001b[33m";
    private const string BrightYellow = "\u001b[93m";
    private const string Red = "\u001b[31m";
    private const string BrightRed = "\u001b[91m";
    private const string Magenta = "\u001b[35m";
    private const string BrightMagenta = "\u001b[95m";
    private const string Blue = "\u001b[34m";
    private const string BrightBlue = "\u001b[94m";

    public CustomConsoleFormatter() : base("akoya") { }

    public override void Write<TState>(
        in LogEntry<TState> logEntry,
        IExternalScopeProvider? scopeProvider,
        TextWriter textWriter)
    {
        string message = logEntry.Formatter(logEntry.State, logEntry.Exception);
        if (string.IsNullOrEmpty(message) && logEntry.Exception == null) return;

        var sb = new StringBuilder();

        // 1. Timestamp
        DateTime timestamp = DateTime.Now;
        sb.Append(DarkGray).Append('[').Append(timestamp.ToString("HH:mm:ss.fff")).Append("] ").Append(Reset);

        // 2. Log Level Icon and Tag
        switch (logEntry.LogLevel)
        {
            case LogLevel.Trace:
                sb.Append(DarkGray).Append("🔍 [TRC] ").Append(Reset);
                break;
            case LogLevel.Debug:
                sb.Append(DarkGray).Append("⚙️ [DBG] ").Append(Reset);
                break;
            case LogLevel.Information:
                sb.Append(BrightCyan).Append("ℹ️ [INF] ").Append(Reset);
                break;
            case LogLevel.Warning:
                sb.Append(BrightYellow).Append("⚠️ [WRN] ").Append(Reset);
                break;
            case LogLevel.Error:
                sb.Append(BrightRed).Append("❌ [ERR] ").Append(Reset);
                break;
            case LogLevel.Critical:
                sb.Append(Bold).Append(BrightRed).Append("🔥 [CRT] ").Append(Reset);
                break;
            default:
                sb.Append(" [LOG] ");
                break;
        }

        // 3. Category (simplified)
        string category = logEntry.Category;
        int lastDot = category.LastIndexOf('.');
        if (lastDot >= 0 && lastDot < category.Length - 1)
        {
            category = category[(lastDot + 1)..];
        }
        sb.Append(DarkGray).Append(category.PadRight(15)).Append(" ▏ ").Append(Reset);

        // 4. Message Body Highlighting
        if (!string.IsNullOrEmpty(message))
        {
            string highlighted = HighlightMessage(message);
            sb.Append(highlighted);
        }

        if (logEntry.Exception != null)
        {
            sb.AppendLine();
            sb.Append(Red).Append(logEntry.Exception.ToString()).Append(Reset);
        }

        textWriter.WriteLine(sb.ToString());
    }

    private static string HighlightMessage(string message)
    {
        // Highlight keys in key-value pairs like name=value or name:value
        // We'll match identifiers followed by '=' or ':' and color the identifier and delimiter.
        // Avoid matching http:// or https:// urls
        string result = Regex.Replace(message, @"\b([a-zA-Z0-9_/]+)([=:](?![/]))([^\s,]+)", m =>
        {
            string key = m.Groups[1].Value;
            string separator = m.Groups[2].Value;
            string val = m.Groups[3].Value;

            // Highlight values differently depending on content
            string valColor = Reset;
            if (val.EndsWith("H/s") || val.EndsWith('s') || double.TryParse(val, out _) || val.Contains("e+") || val.Contains("E+"))
            {
                valColor = BrightGreen;
            }
            else if (val.Equals("true", StringComparison.OrdinalIgnoreCase) || val.Equals("yes", StringComparison.OrdinalIgnoreCase))
            {
                valColor = BrightGreen;
            }
            else if (val.Equals("false", StringComparison.OrdinalIgnoreCase) || val.Equals("no", StringComparison.OrdinalIgnoreCase))
            {
                valColor = BrightRed;
            }

            return $"{DarkGray}{key}{separator}{valColor}{val}{Reset}";
        });

        // Highlight worker index (e.g. worker[0])
        result = Regex.Replace(result, @"worker\[(\d+)\]", $"{BrightMagenta}worker[$1]{Reset}");

        // Highlight triggers and shares
        result = Regex.Replace(result, @"✦ trigger", $"{Bold}{BrightMagenta}✦ trigger{Reset}");
        result = Regex.Replace(result, @"(a|A)ccepted", $"{BrightGreen}Accepted{Reset}");
        result = Regex.Replace(result, @"(r|R)ejected", $"{BrightRed}Rejected{Reset}");
        result = Regex.Replace(result, @"(s|S)hare(s)?", $"{BrightBlue}Share$2{Reset}");

        return result;
    }
}
