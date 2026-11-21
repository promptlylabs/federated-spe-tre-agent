def site_analysis(df):
    """Mean age of the cohort. `n` is the number of people the result is based on."""
    ages = df["age"].dropna()
    return {"n": len(ages), "mean_age": float(ages.mean())}
