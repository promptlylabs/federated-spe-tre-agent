namespace Federated.IntegrationTests;

/// <summary>The researcher code the tests send, as site_analysis(df) functions for run-analysis.</summary>
public static class Code
{
    public const string MeanAge = "def site_analysis(df):\n    return {'n': int(df['age'].notna().sum()), 'mean_age': float(df['age'].mean())}\n";
    public const string CountRows = "def site_analysis(df):\n    return {'n': len(df)}\n";

    /// <summary>Runs for 10 minutes, longer than the dev Agent's MaxRunMinutes, so a test can cancel it while it runs.</summary>
    public const string Sleep = "import time\ndef site_analysis(df):\n    time.sleep(600)\n    return {'n': 0}\n";
}
