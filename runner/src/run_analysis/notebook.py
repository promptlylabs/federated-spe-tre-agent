"""convert-notebook: turns the researcher's notebook into analysis.py, the file that is submitted.

The same notebook always gives the same bytes, so the same fed.code_sha256: only code cells are kept, cells tagged
spe-only are left out, outputs and execution counts are never read, and the text is normalised. Shell commands refuse
the notebook, because the hospitals can't run them; other magics are removed. No code is run.
"""

import ast
import io
import json
import re
import sys
import tokenize
from pathlib import Path

from run_analysis.check import check_code
from run_analysis.errors import AnalysisError

MARKER = "# Generated from {name} by convert-notebook. Edit the notebook, not this file."
_MARKER_START, _MARKER_END = MARKER.split("{name}")
TEMPLATE_DIR = Path(__file__).parent / "templates"
SPE_ONLY = "spe-only"

USAGE = "usage: convert-notebook [--force] <notebook.ipynb> [<analysis.py>]"

# Cell magics whose body is still Python: the magic line is dropped, the body kept.
_KEEP_BODY = {"%%time", "%%timeit", "%%capture"}
_SHELL_CELL_MAGICS = {"%%bash", "%%sh", "%%script", "%%system"}
_SHELL_LINE_MAGICS = {"%pip", "%conda", "%sx", "%system"}
# Line magics that run the statement after them: the magic is dropped, the statement kept.
_KEEP_STATEMENT = {"%time", "%timeit", "%prun"}
_BANG_ASSIGN = re.compile(r"^\w[\w, ]*=\s*!")
_LEADING_MAGIC = re.compile(r"^(\s*)([!%]+)")
_FIX = "; remove it or tag the cell spe-only"


def _refuse(message: str) -> AnalysisError:
    return AnalysisError("invalid_notebook", message + _FIX)


def _cells(notebook_text: str, name: str) -> list:
    try:
        notebook = json.loads(notebook_text)
    except ValueError:
        notebook = None
    if not isinstance(notebook, dict) or notebook.get("nbformat") != 4 or not isinstance(notebook.get("cells"), list):
        raise AnalysisError("invalid_notebook", f"{name} is not a Jupyter notebook (nbformat 4)")
    return notebook["cells"]


def _logical_starts(lines: list[str], candidates: set[int]) -> set[int]:
    """The candidate lines (0-based) that start a logical Python line, which is where IPython looks for % and !.

    A line inside a triple-quoted string, inside brackets or after a backslash is Python, not a magic. In each
    candidate the leading ! and % are replaced by _ (the rest is kept, so brackets still balance) and the cell is
    tokenized; a candidate starts a logical line if its first token comes after a line break, outside any bracket.
    """
    probe = [_LEADING_MAGIC.sub(lambda m: m.group(1) + "_" * len(m.group(2)), line) if i in candidates else line
             for i, line in enumerate(lines)]
    starts, depth, previous = set(), 0, None
    try:
        for token in tokenize.generate_tokens(io.StringIO("\n".join(probe) + "\n").readline):
            if token.type in (tokenize.INDENT, tokenize.DEDENT):
                continue
            row = token.start[0] - 1
            indent = len(lines[row]) - len(lines[row].lstrip()) if row < len(lines) else -1
            if (row in candidates and token.start[1] == indent and depth == 0
                    and previous in (None, tokenize.NEWLINE, tokenize.NL)):
                starts.add(row)
            if token.type == tokenize.OP and token.string in "([{":
                depth += 1
            elif token.type == tokenize.OP and token.string in ")]}":
                depth = max(depth - 1, 0)
            previous = token.type
    except (tokenize.TokenError, SyntaxError):
        # Can't tell (for example an unclosed string): treat every candidate as a magic, as before IPython 7.
        return set(candidates)
    return starts


def _convert_cell(number: int, source: str) -> str:
    lines = [line.rstrip() for line in source.replace("\r\n", "\n").replace("\r", "\n").split("\n")]
    # IPython skips blank lines before it looks for a cell magic.
    first = next((i for i, line in enumerate(lines) if line.strip()), None)
    if first is not None and lines[first].lstrip().startswith("%%"):
        magic = lines[first].split()[0]
        if magic in _SHELL_CELL_MAGICS:
            raise _refuse(f"cell {number} uses {magic}, which runs shell commands")
        if magic not in _KEEP_BODY:
            raise _refuse(f"cell {number} uses the cell magic {magic}, which may not be Python")
        lines[first] = ""
    candidates = {i for i, line in enumerate(lines)
                  if line.lstrip().startswith(("!", "%")) or _BANG_ASSIGN.match(line.lstrip())}
    magics = _logical_starts(lines, candidates) if candidates else set()
    kept = []
    for i, line in enumerate(lines):
        if i not in magics:
            kept.append(line)
            continue
        stripped = line.lstrip()
        where = f"cell {number}, line {i + 1}"
        if stripped.startswith("!") or _BANG_ASSIGN.match(stripped):
            raise _refuse(f"{where} runs a shell command")
        magic, _, rest = stripped.partition(" ")
        if magic.startswith("%%"):
            raise _refuse(f"{where} has the cell magic {magic}, which only works on the first line of a cell")
        if magic in _SHELL_LINE_MAGICS:
            raise _refuse(f"{where} uses {magic}, which installs packages or runs shell commands")
        if magic in _KEEP_STATEMENT and rest.strip():
            # %time r = f() runs the statement in Jupyter, so the statement is kept.
            kept.append(line[:len(line) - len(stripped)] + rest.strip())
    text = "\n".join(kept).strip("\n")
    try:
        ast.parse(text)
    except SyntaxError as e:
        raise _refuse(f"cell {number} has a syntax error at line {e.lineno} of the cell") from None
    return text


def convert(notebook_text: str, notebook_name: str) -> str:
    """The analysis.py text for the notebook. Raises AnalysisError("invalid_notebook", ...) if it can't be converted."""
    parts = []
    for number, cell in enumerate(_cells(notebook_text, notebook_name), start=1):
        if not isinstance(cell, dict) or cell.get("cell_type") != "code":
            continue
        tags = (cell.get("metadata") or {}).get("tags") or []
        if SPE_ONLY in tags:
            continue
        source = cell.get("source", "")
        text = _convert_cell(number, "".join(source) if isinstance(source, list) else str(source))
        if text:
            parts.append(text)
    return MARKER.format(name=notebook_name) + "\n" + "\n\n\n".join(parts) + "\n"


def _may_overwrite(out: Path) -> bool:
    if not out.exists():
        return True
    existing = out.read_bytes()
    first = existing.split(b"\n", 1)[0].decode("utf-8", "replace")
    if first.startswith(_MARKER_START) and first.endswith(_MARKER_END):
        return True
    return existing == (TEMPLATE_DIR / "analysis.py").read_bytes()


def main(argv: list[str] | None = None) -> int:
    args = list(sys.argv[1:] if argv is None else argv)
    force = bool(args) and args[0] == "--force"
    if force:
        args = args[1:]
    if len(args) not in (1, 2) or any(a.startswith("--") for a in args):
        print(USAGE, file=sys.stderr)
        return 2
    notebook = Path(args[0])
    out = Path(args[1]) if len(args) == 2 else notebook.with_name("analysis.py")
    try:
        text = convert(notebook.read_text(encoding="utf-8"), notebook.name)
    except OSError as e:
        print(f"{notebook} could not be read ({type(e).__name__})", file=sys.stderr)
        return 1
    except (AnalysisError, UnicodeDecodeError) as e:
        print(e.message if isinstance(e, AnalysisError) else f"{notebook.name} is not UTF-8 text", file=sys.stderr)
        return 1
    if not force and not _may_overwrite(out):
        print(f"{out} was not generated by convert-notebook; pass --force to overwrite it", file=sys.stderr)
        return 1
    data = text.encode("utf-8")
    out.write_bytes(data)
    print(f"wrote {out}", file=sys.stderr)
    try:
        check_code(data, out.name)
    except AnalysisError as e:
        print(e.message, file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
