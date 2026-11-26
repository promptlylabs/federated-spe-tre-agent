import pytest

from run_analysis.check import MAX_CODE_BYTES, check_code
from run_analysis.errors import AnalysisError

GOOD = (
    "def site_analysis(df):\n"
    "    return {'n': len(df)}\n"
    "\n"
    "\n"
    "def aggregate(results):\n"
    "    return {'n': sum(r['n'] for r in results)}\n"
)
TEMPLATE_BODY = (
    "def site_analysis(df):\n"
    '    """doc"""\n'
    '    raise NotImplementedError("write your analysis here")\n'
    "\n"
    "\n"
    "def aggregate(results):\n"
    "    return {'n': 0}\n"
)


@pytest.mark.parametrize("source", [GOOD.encode(), b"\xef\xbb\xbf" + GOOD.encode()], ids=["plain", "bom"])
def test_check_code_accepts(source):
    check_code(source)


@pytest.mark.parametrize(
    "source, message_part",
    [
        (GOOD.replace("def site_analysis", "def other").encode(), "no site_analysis(df) function"),
        (GOOD.replace("def aggregate", "def other").encode(), "no aggregate(results) function"),
        (GOOD.replace("def aggregate(results):", "class C:\n  def aggregate(results):").encode(),
         "no aggregate(results) function"),
        (GOOD.replace("def aggregate", "async def aggregate").encode(), "no aggregate(results) function"),
        (GOOD.split("def aggregate")[0].encode() + b"aggregate = lambda results: {'n': 0}\n",
         "no aggregate(results) function"),
        (GOOD.encode() + b"#" * MAX_CODE_BYTES, f"at most {MAX_CODE_BYTES} bytes"),
        (b"def site_analysis(df:\n", "line 1"),
        (b"\xff\xfe", "UTF-8"),
        (TEMPLATE_BODY.encode(), "site_analysis is still the template"),
    ],
    ids=["no-site", "no-aggregate", "nested", "async", "lambda", "too-large", "syntax", "not-utf8", "template"],
)
def test_check_code_refuses(source, message_part):
    with pytest.raises(AnalysisError) as e:
        check_code(source)
    assert e.value.code == "invalid_code"
    assert message_part in e.value.message
