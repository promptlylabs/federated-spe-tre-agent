def site_analysis(df, acro):
    """People of each sex, as an ACRO table: the aggregator pools it and withholds pooled cells below its minimum."""
    acro.pivot_table(df, index="sex", values="age", aggfunc="count")
    return {"n": len(df)}


def aggregate(results):
    """The ACRO table is pooled by the aggregator itself; aggregate() only adds up n."""
    return {"n": sum(r["n"] for r in results)}
