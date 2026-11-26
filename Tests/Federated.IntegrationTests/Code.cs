namespace Federated.IntegrationTests;

/// <summary>The researcher code the tests send, as site_analysis(df) functions for run-analysis.</summary>
public static class Code
{
    public const string MeanAge = "def site_analysis(df):\n    return {'n': int(df['age'].notna().sum()), 'mean_age': float(df['age'].mean())}\n";
    public const string CountRows = "def site_analysis(df):\n    return {'n': len(df)}\n";

    /// <summary>
    /// Imports socket on line 1, which the hospital's default scan rules do not allow. The "# nosemgrep" comment must not
    /// switch the rule off.
    /// </summary>
    public const string OpenSocket = "import socket  # nosemgrep\ndef site_analysis(df):\n    socket.create_connection(('example.org', 80))\n    return {'n': len(df)}\n";

    /// <summary>
    /// Runs for 10 minutes, longer than the dev Agent's MaxRunMinutes, so a test can cancel it while it runs. A busy
    /// loop, because the hospital's code scan does not allow importing time; the cancel, or MaxRunMinutes (5 on the dev
    /// stack), ends it.
    /// </summary>
    public const string Sleep =
        "import datetime\ndef site_analysis(df):\n    end = datetime.datetime.now() + datetime.timedelta(minutes=10)\n" +
        "    while datetime.datetime.now() < end:\n        pass\n    return {'n': 0}\n";
}
