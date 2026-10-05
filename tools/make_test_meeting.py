"""議事録の動作確認用に、架空の会議の音声を VOICEVOX の 3 人の声で作る (正解の発言と時刻も書き出す)。

実際の会議の録音を使わずに、文字起こし・話者の聞き分け・音声の保存・要約までを確かめるため。
使い方: python make_test_meeting.py 出力.wav 正解.json
出力: 16kHz・モノラル・16bit の WAV と、[{"start", "end", "speaker", "text"}, ...] の JSON
"""
import json
import os
import sys
import wave

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import voicevox_tts as V  # noqa: E402

RATE = 16000
GAP_SEC = 0.7  # 発言の間
# (話者, VOICEVOX の声, スタイル)。登場人物は架空
VOICES = {"田中": ("No.7", "アナウンス"), "佐藤": ("四国めたん", "ノーマル"), "鈴木": ("青山龍星", "ノーマル")}
LINES = [
    ("田中", "それでは定例会議を始めます。今日は新しい受付システムの進捗を確認します。"),
    ("佐藤", "画面の試作はできていて、今週中に社内でテストを始める予定です。"),
    ("佐藤", "試作の画面では、予約の状況を一目で確認できるようにしました。"),
    ("田中", "テストの結果は、いつ頃共有できそうですか。"),
    ("佐藤", "来週の水曜日までには共有できると思います。"),
    ("鈴木", "問い合わせ窓口の人数が足りない件はどうなっていますか。"),
    ("鈴木", "窓口の利用者は先月より二割ほど増えていて、特に午前中が混み合っています。"),
    ("田中", "それは来月の会議で、予算と一緒に決めましょう。"),
    ("佐藤", "はい。"),
    ("鈴木", "わかりました。それまでに必要な人数を見積もっておきます。"),
    ("田中", "利用者の声も集めておきたいので、アンケートの準備もお願いできますか。"),
    ("鈴木", "はい、アンケートの案は私の方で作っておきます。"),
    ("田中", "では、テスト結果の共有は佐藤さん、人数の見積もりとアンケートは鈴木さんにお願いします。"),
    ("佐藤", "承知しました。"),
    ("田中", "ほかに何かありますか。なければ、これで終わります。お疲れさまでした。"),
]


def style_ids():
    ids = {}
    speakers = V.api("/speakers")
    for who, (name, style) in VOICES.items():
        ids[who] = next(st["id"] for sp in speakers if sp["name"] == name for st in sp["styles"] if st["name"] == style)
    return ids


def synthesize(text, speaker):
    query = V.api("/audio_query", data=b"", params={"text": text, "speaker": speaker})
    query["outputSamplingRate"] = RATE
    query["prePhonemeLength"] = 0.05
    query["postPhonemeLength"] = 0.05
    wav = V.api("/synthesis", data=query, params={"speaker": speaker}, raw=True)
    return np.frombuffer(wav[44:], np.int16).astype(np.float32) / 32768.0


def main():
    out_wav, out_json = sys.argv[1], sys.argv[2]
    proc = V.start_engine()
    try:
        ids = style_ids()
        rng = np.random.default_rng(0)
        audio = [np.zeros(int(RATE * 1.0), np.float32)]
        truth, t = [], 1.0
        for who, text in LINES:
            a = synthesize(text, ids[who])
            truth.append({"start": round(t, 2), "end": round(t + len(a) / RATE, 2), "speaker": who, "text": text})
            audio += [a, np.zeros(int(RATE * GAP_SEC), np.float32)]
            t += len(a) / RATE + GAP_SEC
        audio.append(np.zeros(RATE, np.float32))
        pcm = np.concatenate(audio)
        pcm += rng.normal(0, 0.0015, len(pcm)).astype(np.float32)  # 部屋の小さな雑音
        with wave.open(out_wav, "wb") as w:
            w.setnchannels(1)
            w.setsampwidth(2)
            w.setframerate(RATE)
            w.writeframes((np.clip(pcm, -1, 1) * 32767).astype(np.int16).tobytes())
        json.dump(truth, open(out_json, "w", encoding="utf-8"), ensure_ascii=False, indent=1)
        print(f"{out_wav}: {len(pcm) / RATE:.1f} 秒, 発言 {len(truth)}")
    finally:
        proc.kill()


if __name__ == "__main__":
    main()
