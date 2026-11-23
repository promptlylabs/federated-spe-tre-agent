"""Print the installed Python distributions as sorted name==version lines (like pip freeze, without pip)."""

import importlib.metadata

lines = {f"{d.metadata['Name']}=={d.version}" for d in importlib.metadata.distributions()}
print("\n".join(sorted(lines, key=str.lower)))
