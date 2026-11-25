using Agent.Api.FederatedTasks;
using Agent.Api.Models;
using FiveSafesTes.Core.Models.FederatedTasks;
using Xunit;

namespace Agent.Api.Tests.FederatedTasks;

public class SemgrepCodeScannerTests
{
    private const string Version = "2026-11-1";
    private const string Sha = "0000000000000000000000000000000000000000000000000000000000000000";

    // Trimmed from semgrep 1.185.0 --json output (the config path's directories prefix check_id). Line 7 is
    // "import socket, subprocess": one rule, two messages.
    private const string TwoResults = """
        {"version":"1.185.0","results":[
          {"check_id":"tmp.ScanRules.import-not-allowed","path":"/tmp/analysis.py","start":{"line":7,"col":1,"offset":78},
           "end":{"line":7,"col":26,"offset":103},
           "extra":{"message":"'socket' is not on this hospital's list of allowed imports","metadata":{},"severity":"ERROR"}},
          {"check_id":"tmp.ScanRules.import-not-allowed","path":"/tmp/analysis.py","start":{"line":7,"col":1,"offset":78},
           "end":{"line":7,"col":26,"offset":103},
           "extra":{"message":"'subprocess' is not on this hospital's list of allowed imports","metadata":{},"severity":"ERROR"}},
          {"check_id":"tmp.ScanRules.dynamic-code","path":"/tmp/analysis.py","start":{"line":3,"col":9,"offset":53},
           "end":{"line":3,"col":13,"offset":57},
           "extra":{"message":"runs code made from text, or looks up a name put together while it runs, so what it does can't be checked in advance","metadata":{},"severity":"ERROR"}}
        ],"errors":[],"paths":{"scanned":["/tmp/analysis.py"]}}
        """;

    private const string NoResults = """
        {"version":"1.185.0","results":[],"errors":[],"paths":{"scanned":["/tmp/analysis.py"]}}
        """;

    private const string SyntaxError = """
        {"version":"1.185.0","results":[],"errors":[
          {"code":3,"level":"warn",
           "type":["PartialParsing",[{"path":"/tmp/analysis.py","start":{"line":3,"col":1,"offset":0},"end":{"line":4,"col":13,"offset":34}}]],
           "message":"Syntax error at line /tmp/analysis.py:3:\n `def site_analysis(df:\n    return {` was unexpected",
           "path":"/tmp/analysis.py",
           "spans":[{"file":"/tmp/analysis.py","start":{"line":3,"col":1,"offset":0},"end":{"line":4,"col":13,"offset":34}}]}
        ],"paths":{"scanned":["/tmp/analysis.py"]}}
        """;

    // Rule files Semgrep 1.185.0 can't load: a bad pattern, invalid YAML, a rule without its message.
    private const string RuleParseError = """
        {"version":"1.185.0","results":[],"errors":[
          {"code":2,"level":"error","type":"Rule parse error","rule_id":"tmp.broken",
           "message":"Rule parse error in rule tmp.broken:\n Invalid pattern for Python: Stdlib.Parsing.Parse_error\n----- pattern -----\neval(\n----- end pattern -----\n"}
        ],"paths":{"scanned":[]}}
        """;

    private const string InvalidYaml = """
        {"version":"1.185.0","results":[],"errors":[
          {"code":5,"level":"error","type":"SemgrepError","message":"Invalid YAML file rules.yaml:\n\twhile parsing a flow sequence"},
          {"code":7,"level":"error","type":"SemgrepError","message":"invalid configuration file found (1 configs were invalid)"}
        ],"paths":{"scanned":[]}}
        """;

    private const string MissingMessage = """
        {"version":"1.185.0","results":[],"errors":[
          {"code":4,"level":"error","type":"InvalidRuleSchemaError","long_msg":"One of these properties is missing: 'message'",
           "short_msg":"Invalid rule schema","spans":[]},
          {"code":7,"level":"error","type":"SemgrepError","message":"invalid configuration file found (1 configs were invalid)"}
        ],"paths":{"scanned":[]}}
        """;

    private const string Timeout = """
        {"version":"1.185.0","results":[],"errors":[
          {"code":3,"level":"warn","type":"Timeout","rule_id":"tmp.dynamic-code","message":"Timeout when running tmp.dynamic-code on /tmp/analysis.py"}
        ],"paths":{"scanned":["/tmp/analysis.py"]}}
        """;

    [Fact]
    public void ParseOutput_Results_SortedFindings()
    {
        var result = SemgrepCodeScanner.ParseOutput(TwoResults, Version, Sha);

        Assert.Equal(CodeScanKind.Findings, result.Kind);
        Assert.Equal(
            [
                new CodeScanFinding("dynamic-code", 3,
                    "runs code made from text, or looks up a name put together while it runs, so what it does can't be checked in advance"),
                new CodeScanFinding("import-not-allowed", 7, "'socket' is not on this hospital's list of allowed imports"),
                new CodeScanFinding("import-not-allowed", 7, "'subprocess' is not on this hospital's list of allowed imports"),
            ],
            result.Findings);
        Assert.Equal(Version, result.RulesVersion);
        Assert.Equal(Sha, result.RulesSha256);
    }

    [Fact]
    public void ParseOutput_NoResults_Clean()
    {
        var result = SemgrepCodeScanner.ParseOutput(NoResults, Version, Sha);

        Assert.Equal(CodeScanKind.Clean, result.Kind);
        Assert.Empty(result.Findings);
        Assert.Equal(Version, result.RulesVersion);
    }

    [Fact]
    public void ParseOutput_SyntaxError_IsFinding()
    {
        var result = SemgrepCodeScanner.ParseOutput(SyntaxError, Version, Sha);

        Assert.Equal(CodeScanKind.Findings, result.Kind);
        var finding = Assert.Single(result.Findings);
        Assert.Equal(new CodeScanFinding("parse-error", 3, "the code could not be parsed"), finding);
        Assert.Equal("parse-error line 3: the code could not be parsed", CodeScanMessages.Format(result.Findings));
    }

    [Theory]
    [InlineData(RuleParseError, CodeScanKind.RulesUnavailable, "Rule parse error")]
    [InlineData(InvalidYaml, CodeScanKind.RulesUnavailable, "Invalid YAML")]
    [InlineData(MissingMessage, CodeScanKind.RulesUnavailable, "'message'")]
    [InlineData(Timeout, CodeScanKind.ScannerFailed, "Timeout")]
    public void ParseOutput_OtherError_RulesUnavailableOrScannerFailed(string json, CodeScanKind kind, string detail)
    {
        var result = SemgrepCodeScanner.ParseOutput(json, Version, Sha);

        Assert.Equal(kind, result.Kind);
        Assert.Empty(result.Findings);
        Assert.Contains(detail, result.Detail);
    }

    [Fact]
    public void FormatFindings_Truncates()
    {
        const string secret = "SECRET_SOURCE_TEXT";
        var findings = Enumerable.Range(1, 40)
            .Select(line => new CodeScanFinding("file-access", line,
                "reads or writes files; the code gets its data only as df and returns its result as a dict"))
            .ToList();

        var message = CodeScanMessages.Format(findings);

        Assert.True(message.Length <= FederatedResultLimits.MaxErrorMessageLength);
        Assert.StartsWith("file-access line 1: reads or writes files", message);
        Assert.Matches(@"; … and \d+ more$", message);
        var shown = message.Split("; ").Count(part => part.StartsWith("file-access line "));
        Assert.EndsWith($"; … and {40 - shown} more", message);
        Assert.DoesNotContain(secret, message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("rules:\n- id: x\n")]
    [InlineData("# version: \nrules: []\n")]
    [InlineData("# version: 0123456789012345678901234567890123456789012345678901234567890123456789\nrules: []\n")]
    public async Task Scan_RulesWithoutVersion_RulesUnavailable(string? content)
    {
        var dir = Directory.CreateTempSubdirectory("scan-rules-test-");
        try
        {
            var path = Path.Combine(dir.FullName, "rules.yaml");
            if (content != null)
            {
                await File.WriteAllTextAsync(path, content);
            }

            var scanner = new SemgrepCodeScanner(new FederatedTaskSettings
            {
                ScanRulesPath = path,
                SemgrepPath = "/nonexistent/semgrep",
            });

            var result = await scanner.ScanAsync("import pandas\n", CancellationToken.None);

            Assert.Equal(CodeScanKind.RulesUnavailable, result.Kind);
            Assert.Null(result.RulesVersion);
            Assert.False(string.IsNullOrEmpty(result.Detail));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
