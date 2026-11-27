namespace Federated.IntegrationTests;

/// <summary>
/// The researcher code the tests send: site_analysis(df) for the hospitals and aggregate(results) for the orchestrator
/// (intake refuses code without aggregate).
/// </summary>
public static class Code
{
    /// <summary>Sums n, the usual pooling for a count; appended to the snippets below.</summary>
    private const string SumN = "\n\ndef aggregate(results):\n    return {'n': sum(r['n'] for r in results)}\n";
    /// <summary>Mean age at each hospital; aggregate() pools it as the n-weighted mean.</summary>
    public const string MeanAge = "def site_analysis(df):\n    return {'n': int(df['age'].notna().sum()), 'mean_age': float(df['age'].mean())}\n" +
        "\n\ndef aggregate(results):\n    n = sum(r['n'] for r in results)\n    return {'n': n, 'mean_age': sum(r['n'] * r['mean_age'] for r in results) / n}\n";

    public const string CountRows = "def site_analysis(df):\n    return {'n': len(df)}\n" + SumN;

    /// <summary>
    /// The people of each sex as an ACRO table (site_analysis(df, acro)): the aggregator pools it and withholds small
    /// pooled cells. aggregate() only adds up n.
    /// </summary>
    public const string AcroCountBySex = "def site_analysis(df, acro):\n    acro.pivot_table(df, index='sex', values='age', aggfunc='count')\n" +
        "    return {'n': len(df)}\n" + SumN;

    /// <summary>A count at each hospital whose aggregate() raises: the study fails with code_error.</summary>
    public const string AggregateRaises = "def site_analysis(df):\n    return {'n': len(df)}\n\n\ndef aggregate(results):\n    raise ValueError('no')\n";

    /// <summary>A count with no aggregate(): intake refuses it.</summary>
    public const string NoAggregate = "def site_analysis(df):\n    return {'n': len(df)}\n";

    /// <summary>
    /// Imports socket on line 1, which the hospital's default scan rules do not allow. The "# nosemgrep" comment must not
    /// switch the rule off.
    /// </summary>
    public const string OpenSocket = "import socket  # nosemgrep\ndef site_analysis(df):\n    socket.create_connection(('example.org', 80))\n    return {'n': len(df)}\n" + SumN;

    /// <summary>
    /// Runs for 10 minutes, longer than the dev Agent's MaxRunMinutes, so a test can cancel it while it runs. A busy
    /// loop, because the hospital's code scan does not allow importing time; the cancel, or MaxRunMinutes (5 on the dev
    /// stack), ends it.
    /// </summary>
    public const string Sleep =
        "import datetime\ndef site_analysis(df):\n    end = datetime.datetime.now() + datetime.timedelta(minutes=10)\n" +
        "    while datetime.datetime.now() < end:\n        pass\n    return {'n': 0}\n" + SumN;
}
