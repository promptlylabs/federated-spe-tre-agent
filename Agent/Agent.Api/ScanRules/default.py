# Semgrep rule tests for default.yaml: run ./test.sh. A "ruleid" annotation means that rule must find the next line,
# an "ok" annotation means it must not; any other finding fails the test.
# Code that does not parse can't be annotated here (Semgrep refuses the whole file); the Agent's scan tests cover it.

# --- import-not-allowed -----------------------------------------------------------------------------------------------
# ok: import-not-allowed
import pandas as pd
# ok: import-not-allowed
from numpy import mean
# ok: import-not-allowed
import scipy.stats
# ruleid: import-not-allowed
import socket
# ruleid: import-not-allowed
import socket as s
# ruleid: import-not-allowed
from socket import create_connection
# ruleid: import-not-allowed, back-door
from urllib.request import urlopen
# ruleid: import-not-allowed
from http import *
# ruleid: import-not-allowed
import pandasx


def site_analysis(df):
    # ok: import-not-allowed, dynamic-code, process-call, file-access, back-door
    return {"n": len(df), "mean_age": float(df.groupby("sex")["age"].mean().max())}


# --- dynamic-code -----------------------------------------------------------------------------------------------------
# ruleid: dynamic-code
eval("1 + 1")
# ruleid: dynamic-code
exec(code)
# ruleid: dynamic-code
compile(code, "x", "exec")
# ruleid: dynamic-code
f = eval
# ruleid: dynamic-code
__import__("so" + "cket")
# ruleid: import-not-allowed
import importlib
# ruleid: dynamic-code
importlib.import_module("socket")
# ruleid: dynamic-code
getattr(df, name)
# ruleid: dynamic-code
setattr(df, name, 1)
# ruleid: dynamic-code
globals()
# ruleid: dynamic-code
df.__class__.__subclasses__()
# ruleid: dynamic-code
smf.ols("y ~ I(__import__('os').system('id'))", data=df)
# ruleid: dynamic-code
df.query("age > @vars(np.lib)['o' + 's']")
# ok: dynamic-code
getattr(df, "age")
# ok: dynamic-code
smf.ols("age ~ C(sex)", data=df)
# ok: dynamic-code
re.compile(r"\d+")
# ok: dynamic-code
if __name__ == "__main__":
    site_analysis(df)

# --- process-call -----------------------------------------------------------------------------------------------------
# ruleid: import-not-allowed
import os
# ruleid: import-not-allowed
import subprocess
# ruleid: process-call
os.system("id")
# ruleid: process-call
subprocess.run(["id"])
# ruleid: process-call
os.execv("/bin/sh", ["sh"])
# ruleid: dynamic-code, process-call
getattr(os, "sys" + "tem")("id")
# ruleid: import-not-allowed, process-call
from os import popen
# ok: process-call
os.path.join("a", "b")

# --- file-access ------------------------------------------------------------------------------------------------------
# ruleid: file-access
open("/data/x", "w")
# ruleid: file-access
df.to_csv("/data/x")
# ruleid: file-access
pd.read_csv("/data/other.csv")
# ruleid: file-access
pd.DataFrame(rows).to_parquet("/tmp/x")
# ruleid: file-access
np.load("/data/x.npy")
# ruleid: file-access
from pandas import read_pickle
# ruleid: file-access
getattr(df, "to_csv")("/data/x")
# ruleid: file-access
getattr(pd, "read_csv")("/data/other.csv")
# ruleid: file-access
arr.dump("/tmp/x")
# ruleid: file-access
scipy.sparse.save_npz("/tmp/x", m)
# ruleid: file-access
np.lib.npyio.DataSource("/tmp")
# ok: file-access
df.to_dict()
# ok: file-access
df.to_string()
# ok: file-access
df.to_csv()

# --- back-door --------------------------------------------------------------------------------------------------------
# ruleid: back-door
import numpy.ctypeslib
# ruleid: back-door
from numpy import f2py
# ruleid: back-door
from scipy.io import loadmat
# ruleid: back-door
pd.HDFStore("/tmp/x.h5")
# ruleid: back-door
np.memmap("/tmp/x", mode="w+")
# ruleid: back-door
operator.attrgetter(name)(os)
# ruleid: back-door
df.query(expr)
# ruleid: back-door
df.query(f"age > {limit}")
# ruleid: back-door, process-call
pd.io.common.os.system("id")
# ruleid: back-door
typing.get_type_hints(site_analysis)
# ruleid: back-door, process-call
getattr(getattr(pd.io.common, "os"), "system")("id")
# ruleid: back-door
f = pd.eval
# ruleid: back-door
q = getattr(df, "query")
# ruleid: back-door
np.testing.runstring(code, {})
# ruleid: back-door
t.runstring(code, {})
# ruleid: back-door
pd.compat.pickle_compat.loads(data)
# ruleid: back-door
from pandas.compat.pickle_compat import Unpickler
# ruleid: back-door
import pandas.testing
# ruleid: back-door
from numpy import testing
# ruleid: back-door
from pandas import compat
# ruleid: back-door
pd.io.common.urlopen(url)
# ruleid: back-door
from pandas.io.common import urlopen
# ruleid: back-door
pd.io.common.gzip.GzipFile(name)
# ruleid: back-door
c.codecs.decode(data)
# ok: back-door
df.groupby("sex").agg("mean")
# ok: back-door
df.query("age > 40")
# ok: back-door
pd.eval("a + b")
# ok: back-door
df.eval("bmi = weight / height ** 2")
# ok: back-door
operator.attrgetter("age")(df)
