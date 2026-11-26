def site_analysis(df):
    """The number of records in the dataset."""
    return {"n": len(df)}


def aggregate(results):
    """The number of records across all hospitals."""
    return {"n": sum(r["n"] for r in results)}
