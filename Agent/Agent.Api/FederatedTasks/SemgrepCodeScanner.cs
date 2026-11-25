using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agent.Api.Models;

namespace Agent.Api.FederatedTasks
{
    /// <summary>
    /// Scans researcher code with Semgrep and the hospital's rule file, offline: local rules only, no metrics, no version
    /// check. The code and a copy of the rule file are written to a new temporary directory (so the hashed rules are the
    /// rules that ran), and the directory is always deleted. The code's text never leaves this class.
    /// </summary>
    public sealed class SemgrepCodeScanner : ICodeScanner
    {
        /// <summary>The first line of a rule file: "# version: &lt;text&gt;".</summary>
        private const string VersionPrefix = "# version:";

        private const int MaxVersionLength = 64;

        /// <summary>Semgrep's error type for code it could not (fully) parse.</summary>
        private static readonly HashSet<string> ParseErrorTypes =
            ["PartialParsing", "Syntax error", "Lexical error", "Other syntax error"];

        private readonly FederatedTaskSettings _settings;

        public SemgrepCodeScanner(FederatedTaskSettings settings)
        {
            _settings = settings;
        }

        public static string DefaultRulesPath => Path.Combine(AppContext.BaseDirectory, "ScanRules", "default.yaml");

        public async Task<CodeScanResult> ScanAsync(string code, CancellationToken ct)
        {
            var rulesPath = _settings.ScanRulesPath ?? DefaultRulesPath;
            byte[] rules;
            try
            {
                rules = await File.ReadAllBytesAsync(rulesPath, ct);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Unavailable($"the rule file {rulesPath} cannot be read: {ex.Message}");
            }

            var version = ReadVersion(rules);
            if (version == null)
            {
                return Unavailable($"the rule file {rulesPath} does not start with a '{VersionPrefix} <text>' line (at most {MaxVersionLength} characters)");
            }

            var sha = Convert.ToHexStringLower(SHA256.HashData(rules));
            var dir = Directory.CreateTempSubdirectory("fed-scan-");
            try
            {
                var rulesCopy = Path.Combine(dir.FullName, "rules.yaml");
                var codeFile = Path.Combine(dir.FullName, "analysis.py");
                await File.WriteAllBytesAsync(rulesCopy, rules, ct);
                await File.WriteAllTextAsync(codeFile, code, new UTF8Encoding(false), ct);
                return await RunSemgrepAsync(dir.FullName, rulesCopy, codeFile, version, sha, ct);
            }
            finally
            {
                try
                {
                    dir.Delete(recursive: true);
                }
                catch (IOException)
                {
                    // A leftover temp directory holds no data; the next scan uses a new one.
                }
            }
        }

        private async Task<CodeScanResult> RunSemgrepAsync(
            string dir, string rulesFile, string codeFile, string version, string sha, CancellationToken ct)
        {
            var start = new ProcessStartInfo(_settings.SemgrepPath)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                WorkingDirectory = dir,
            };
            foreach (var arg in new[]
                     {
                         "--config", rulesFile, "--json", "--metrics=off", "--disable-version-check", "--no-git-ignore",
                         codeFile
                     })
            {
                start.ArgumentList.Add(arg);
            }

            // Semgrep writes its settings, log and version cache under the user's home: keep them in the temp directory.
            start.Environment["XDG_CONFIG_HOME"] = Path.Combine(dir, "config");
            start.Environment["SEMGREP_SETTINGS_FILE"] = Path.Combine(dir, "config", "settings.yml");
            start.Environment["SEMGREP_LOG_FILE"] = Path.Combine(dir, "semgrep.log");
            start.Environment["SEMGREP_VERSION_CACHE_PATH"] = Path.Combine(dir, "version-cache");
            start.Environment["SEMGREP_ENABLE_VERSION_CHECK"] = "0";
            start.Environment["SEMGREP_SEND_METRICS"] = "off";

            using var process = new Process { StartInfo = start };
            try
            {
                process.Start();
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                return Failed(version, sha, $"Semgrep at {_settings.SemgrepPath} could not be started: {ex.Message}");
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(_settings.ScanTimeoutSeconds));
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
                await Task.WhenAll(stdout, stderr);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                    // It exited meanwhile.
                }

                ct.ThrowIfCancellationRequested();
                return Failed(version, sha, $"Semgrep ran longer than {_settings.ScanTimeoutSeconds} seconds");
            }

            var json = stdout.Result;
            var error = stderr.Result;
            CodeScanResult result;
            try
            {
                result = ParseOutput(json, version, sha);
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException)
            {
                return Failed(version, sha, $"Semgrep exited with {process.ExitCode} and no readable JSON: {error}");
            }

            if (result.Kind == CodeScanKind.ScannerFailed)
            {
                return result with { Detail = $"{result.Detail} (exit {process.ExitCode}): {error}" };
            }

            // Semgrep exits 0 with or without findings; any other exit cannot vouch for its output.
            return process.ExitCode == 0
                ? result
                : Failed(version, sha, $"Semgrep exited with {process.ExitCode}: {error}");
        }

        /// <summary>
        /// Reads Semgrep's JSON output: results become findings (rule id without the path prefix Semgrep adds), a parse
        /// error becomes a "parse-error" finding with its line, and any other error makes the scan fail.
        /// </summary>
        internal static CodeScanResult ParseOutput(string json, string version, string sha)
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var findings = new List<CodeScanFinding>();
            var otherErrors = new List<string>();

            foreach (var error in root.GetProperty("errors").EnumerateArray())
            {
                var type = error.GetProperty("type");
                var typeName = type.ValueKind == JsonValueKind.Array ? type[0].GetString() : type.GetString();
                if (typeName != null && ParseErrorTypes.Contains(typeName))
                {
                    findings.Add(new CodeScanFinding(
                        CodeScanMessages.ParseErrorRuleId, ParseErrorLine(error, type), CodeScanMessages.ParseErrorMessage));
                }
                else
                {
                    // Logged only: an error that is not a parse error is about the rules or Semgrep, not the code.
                    otherErrors.Add($"{typeName}: {(error.TryGetProperty("message", out var m) ? m.GetString() : "")}");
                }
            }

            if (otherErrors.Count > 0)
            {
                return Failed(version, sha, string.Join(" | ", otherErrors));
            }

            foreach (var match in root.GetProperty("results").EnumerateArray())
            {
                var checkId = match.GetProperty("check_id").GetString()!;
                findings.Add(new CodeScanFinding(
                    checkId[(checkId.LastIndexOf('.') + 1)..],
                    match.GetProperty("start").GetProperty("line").GetInt32(),
                    match.GetProperty("extra").GetProperty("message").GetString() ?? ""));
            }

            // One line can match the same rule more than once.
            var unique = findings
                .DistinctBy(f => (f.RuleId, f.Line))
                .OrderBy(f => f.Line)
                .ThenBy(f => f.RuleId, StringComparer.Ordinal)
                .ToList();
            return new CodeScanResult(
                unique.Count == 0 ? CodeScanKind.Clean : CodeScanKind.Findings, unique, version, sha, null);
        }

        /// <summary>The first line of the error's span, or of its location in the type; line 1 when Semgrep gives none.</summary>
        private static int ParseErrorLine(JsonElement error, JsonElement type)
        {
            if (error.TryGetProperty("spans", out var spans) && spans.ValueKind == JsonValueKind.Array &&
                spans.GetArrayLength() > 0)
            {
                return spans[0].GetProperty("start").GetProperty("line").GetInt32();
            }

            if (type.ValueKind == JsonValueKind.Array && type.GetArrayLength() > 1 &&
                type[1].ValueKind == JsonValueKind.Array && type[1].GetArrayLength() > 0)
            {
                return type[1][0].GetProperty("start").GetProperty("line").GetInt32();
            }

            return 1;
        }

        /// <summary>The version from a "# version: &lt;text&gt;" first line, or null.</summary>
        private static string? ReadVersion(byte[] rules)
        {
            var text = new UTF8Encoding(false).GetString(rules).TrimStart('﻿');
            var end = text.IndexOf('\n');
            var firstLine = (end < 0 ? text : text[..end]).TrimEnd('\r');
            if (!firstLine.StartsWith(VersionPrefix, StringComparison.Ordinal))
            {
                return null;
            }

            // The orchestrator accepts at most 64 printable characters.
            var version = firstLine[VersionPrefix.Length..].Trim();
            return version.Length is 0 or > MaxVersionLength || version.Any(char.IsControl) ? null : version;
        }

        private static CodeScanResult Unavailable(string detail) =>
            new(CodeScanKind.RulesUnavailable, [], null, null, detail);

        private static CodeScanResult Failed(string version, string sha, string detail) =>
            new(CodeScanKind.ScannerFailed, [], version, sha, detail);
    }
}
