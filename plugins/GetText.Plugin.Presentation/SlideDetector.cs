namespace GetText.Plugins.Presentation;

/// <summary>新しいスライドとみなす厳しさ。</summary>
public enum SlideSensitivity
{
    /// <summary>厳しめ: 画面が長く止まり、大きく変わったときだけ (スライドの数が少ない)。</summary>
    Strict,
    /// <summary>普通。</summary>
    Normal,
    /// <summary>ゆるめ: 箇条書きが 1 行増えたくらいでも新しいスライドに (数が多い)。</summary>
    Loose,
}

public enum SlideDecision
{
    /// <summary>何もしない (動いている・前と同じ)。</summary>
    None,
    /// <summary>止まった新しいスライド: 保存する。</summary>
    NewSlide,
    /// <summary>ほぼ一色 (保護された画面など): 保存しない。続く間は 1 度だけ知らせる。</summary>
    Protected,
}

/// <summary>
/// 取り込んだコマを順に見て、「画面が止まった」かつ「前に保存したスライドと違う」ときに新しいスライドとする。
/// 動画・アニメーション・スクロールの途中は保存しない。同じスライドに戻ったときも保存しない (直前のものと比べる)。
/// </summary>
public sealed class SlideDetector(SlideSensitivity sensitivity = SlideSensitivity.Normal)
{
    /// <summary>
    /// 止まったとみなすまでの、続けて似ているコマの数・似ているとみなす dHash の差・
    /// 新しいとみなす pHash の差・新しいとみなす変わった面積 (どちらかを超えたら新しいスライド)。
    /// </summary>
    public static (int StableFrames, int StableDistance, int NewDistance, double NewArea) Thresholds(SlideSensitivity s) => s switch
    {
        SlideSensitivity.Strict => (3, 2, 14, 0.20),
        SlideSensitivity.Loose => (2, 6, 5, 0.015),
        _ => (2, 4, 9, 0.06),
    };

    private ulong? _previous;
    private double[]? _previousThumbnail;
    private int _stable;
    private bool _savedThisStill;
    private bool _protectedReported;

    public SlideSensitivity Sensitivity { get; set; } = sensitivity;

    /// <summary>最後に保存したスライドの pHash。</summary>
    public ulong? LastSaved { get; private set; }

    private double[]? _lastThumbnail;

    public int Saved { get; private set; }

    /// <param name="dHash">このコマの dHash。</param>
    /// <param name="pHash">このコマの pHash。</param>
    /// <param name="thumbnail">このコマの 32×32 の明るさ (ImageHash.Thumbnail)。</param>
    /// <param name="uniform">ほぼ一色か。</param>
    public SlideDecision Observe(ulong dHash, ulong pHash, double[] thumbnail, bool uniform)
    {
        var (stableFrames, stableDistance, newDistance, newArea) = Thresholds(Sensitivity);
        if (uniform)
        {
            _previous = null;
            _previousThumbnail = null;
            _stable = 0;
            _savedThisStill = false;
            if (_protectedReported) return SlideDecision.None;
            _protectedReported = true;
            return SlideDecision.Protected;
        }
        _protectedReported = false;

        // 前のコマと似ていれば「止まっている」が続く。全体の形 (dHash) か、変わった面積のどちらかが大きければ動いた
        bool same = _previous is { } prev && ImageHash.Distance(prev, dHash) <= stableDistance
                    && _previousThumbnail != null && ImageHash.ChangedArea(_previousThumbnail, thumbnail) < newArea;
        if (same)
        {
            _stable++;
        }
        else
        {
            _stable = 1;
            _savedThisStill = false;
        }
        _previous = dHash;
        _previousThumbnail = thumbnail;

        if (_stable < stableFrames || _savedThisStill) return SlideDecision.None;
        _savedThisStill = true;
        if (LastSaved is { } last && ImageHash.Distance(last, pHash) < newDistance
            && _lastThumbnail != null && ImageHash.ChangedArea(_lastThumbnail, thumbnail) < newArea) return SlideDecision.None;
        LastSaved = pHash;
        _lastThumbnail = thumbnail;
        Saved++;
        return SlideDecision.NewSlide;
    }

    public void Reset()
    {
        _previous = null;
        _previousThumbnail = null;
        _stable = 0;
        _savedThisStill = false;
        _protectedReported = false;
        LastSaved = null;
        _lastThumbnail = null;
        Saved = 0;
    }
}
