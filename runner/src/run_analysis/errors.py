class AnalysisError(Exception):
    """A failure reported in result.json. The message never holds a data value."""

    def __init__(self, code: str, message: str):
        super().__init__(code, message)
        self.code = code
        self.message = message
