def site_analysis(df):
    """Mean age of the cohort. `n` is the number of people the result is based on."""
    ages = df["age"].dropna()
    return {"n": len(ages), "mean_age": float(ages.mean())}


def aggregate(results):
    """Pools the hospitals' results: the mean age across all of them, each hospital weighted by its n."""
    n = sum(r["n"] for r in results)
    return {"n": n, "mean_age": sum(r["n"] * r["mean_age"] for r in results) / n if n else None}
