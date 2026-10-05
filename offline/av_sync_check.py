"""録画の音と映像のずれを測る (動作確認用): GetText.exe --selftest-record で作った record_test.mp4 で、
窓が白くなった時刻 (音を鳴らし始めた合図) と、音 (1kHz) の出だしの時刻を比べる。
使い方: python av_sync_check.py record_test.mp4
音が映像より遅ければ正、早ければ負。再生するプロセスが音を出し始めるまでの遅れ (数十ミリ秒) があるので、少し正になるのが普通。
"""
import sys

import av
import numpy as np


def main(path):
    white = None
    with av.open(path) as c:
        for f in c.decode(video=0):
            g = f.to_ndarray(format="gray")
            h, w = g.shape
            center = g[h // 3: 2 * h // 3, w // 4: 3 * w // 4]
            if center.mean() > 235:
                white = float(f.time)
                break
    onset = None
    with av.open(path) as c:
        rs = av.AudioResampler(format="flt", layout="mono", rate=48000)
        t0 = None
        buf = []
        for f in c.decode(audio=0):
            if t0 is None:
                t0 = float(f.time or 0)
            for g in rs.resample(f):
                buf.append(g.to_ndarray().reshape(-1))
        a = np.concatenate(buf) if buf else np.zeros(0)
        win = 480
        n = len(a) // win
        rms = np.sqrt((a[: n * win].reshape(n, win) ** 2).mean(1)) if n else np.zeros(0)
        loud = np.where(rms > 0.02)[0]
        if len(loud):
            onset = (t0 or 0) + loud[0] * win / 48000
    if white is None or onset is None:
        print(f"測れませんでした (白い画面 {white} / 音の出だし {onset})")
        return 2
    print(f"白い画面 {white:.3f} 秒 / 音の出だし {onset:.3f} 秒 / ずれ {onset - white:+.3f} 秒")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1]))
