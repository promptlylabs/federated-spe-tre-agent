def site_analysis(df):
    """Number of people and their mean age. `n` is the number of people the result is based on."""
    ages = df["age"].dropna()
    return {"n": len(ages), "mean_age": float(ages.mean())}
