"""GetText のデータ (モデル・覚えた声など) を置く場所。アプリ (C#) の LocalApplicationData と同じ所。

Windows: %LOCALAPPDATA%\\GetText ・ Mac: ~/Library/Application Support/GetText ・ そのほか: ~/.local/share/GetText
"""
import os
import sys


def app_dir():
    if os.name == "nt":
        return os.path.join(os.environ.get("LOCALAPPDATA", ""), "GetText")
    if sys.platform == "darwin":
        return os.path.join(os.path.expanduser("~"), "Library", "Application Support", "GetText")
    return os.path.join(os.environ.get("XDG_DATA_HOME") or os.path.join(os.path.expanduser("~"), ".local", "share"), "GetText")


MODELS_DIR = os.path.join(app_dir(), "offline", "models")
