"""NVIDIA GPU で翻訳・音声認識を動かすための DLL (cuBLAS など) の場所を登録する。

CUDA Toolkit が入っていない PC では、セットアップが pip の nvidia-cublas-cu12 などを入れる。
その DLL は site-packages/nvidia/*/bin にあるので、CTranslate2 を読み込む前に探す場所へ加える。
CUDA Toolkit が入っている PC や GPU の無い PC では何もしない (見つからなければ CPU で動く)。
"""
import glob
import os
import sys


def register():
    for base in sys.path:
        for bin_dir in glob.glob(os.path.join(base, "nvidia", "*", "bin")):
            if not os.path.isdir(bin_dir):
                continue
            try:
                os.add_dll_directory(bin_dir)
            except (OSError, AttributeError):
                pass
            if bin_dir not in os.environ.get("PATH", ""):
                os.environ["PATH"] = bin_dir + os.pathsep + os.environ.get("PATH", "")


register()
