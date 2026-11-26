# Your federated analysis.
#
# This file is what is submitted. Each hospital runs site_analysis on its own data; only the dict it returns can
# leave the hospital, after a reviewer there approves it. The orchestrator then pools the approved results with
# aggregate. Nobody sees the hospitals' data.
#
# You can write this file by hand, or work in notebook_analysis.ipynb and run
#     convert-notebook notebook_analysis.ipynb
# which writes this file for you (and replaces what is here).
#
# site_analysis(df)
#     df is the hospital's dataset as a pandas DataFrame, with the columns of the dataset's schema.json.
#     Return a dict that can be saved as JSON (numbers, text, lists, dicts), at most 61,440 bytes, with "n": the
#     number of people the result is based on.
#
# aggregate(results)
#     results is the list of the dicts site_analysis returned, one per hospital. Return the pooled dict, also with
#     "n". It must work when two hospitals return the same values.
#
# The code has no files, no network and cannot install packages; it gets its data as df and returns a dict.
# Before your study is submitted, this file is tried on synthetic data shaped like the hospitals' data. If it fails
# there, it is not submitted. To try it yourself:
#     check-analysis --dataset <synthetic dataset folder> analysis.py


def site_analysis(df):
    """Runs at each hospital on its dataset. Returns a dict with n."""
    raise NotImplementedError("write your analysis here")


def aggregate(results):
    """Runs once on the list of the hospitals' results. Returns the pooled dict with n."""
    raise NotImplementedError("write your analysis here")
