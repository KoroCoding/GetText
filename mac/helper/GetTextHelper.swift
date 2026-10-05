// GetText の Mac 版の補助プログラム。GetText.app (C# / Avalonia) から起動され、標準入出力の JSON (1 行 1 件) で
// Mac の機能を受け持つ:
//   ・アプリの音 / それ以外のすべての音の取り込み (ScreenCaptureKit) とマイク (AVAudioEngine) → 16kHz・モノラル・16bit
//   ・画面の一部の取り込みと文字認識 (ScreenCaptureKit + Vision)
//   ・ウィンドウの一覧と、音を出しているアプリ (Core Audio)
//   ・他のアプリの操作中でも効くショートカット (Carbon)
//   ・DeepL キーの保管 (キーチェーン)
//   ・発言の音声の再生 (AVPlayer)
//   ・画面の録画 (ScreenCaptureKit + AVAssetWriter。ウィンドウ・画面全体を、その音も入れて MP4 に)
// 要求: {"id": 1, "cmd": "…", …} → 応答: {"id": 1, "ok": true, …} または {"id": 1, "ok": false, "error": "…"}
// 通知: {"event": "audio", "stream": "win", "data": "<base64>"} / {"event": "hotkey", "id": 1} など
// 標準入力が閉じられたら (GetText が終わったら) 終わる。

import AppKit
import AVFoundation
import Carbon
import CoreAudio
import CoreGraphics
import CoreMedia
import Foundation
import ScreenCaptureKit
import Security
import Vision

// MARK: - 入出力

let outputLock = NSLock()

func send(_ object: [String: Any]) {
    guard let data = try? JSONSerialization.data(withJSONObject: object, options: []) else { return }
    outputLock.lock()
    // (GetText が落ちて受け取る側が無くなっていても、例外で止まらない。録画を書き終えるまで動き続ける)
    try? FileHandle.standardOutput.write(contentsOf: data + Data([0x0A]))
    outputLock.unlock()
}

func reply(_ id: Any?, _ object: [String: Any] = [:]) {
    var o = object
    o["id"] = id ?? NSNull()
    if o["ok"] == nil { o["ok"] = true }
    send(o)
}

func fail(_ id: Any?, _ message: String) {
    reply(id, ["ok": false, "error": message])
}

struct HelperError: Error, CustomStringConvertible {
    let description: String
    init(_ description: String) { self.description = description }
}

func message(of error: Error) -> String {
    if let e = error as? HelperError { return e.description }
    return (error as NSError).localizedDescription
}

func log(_ text: String) {
    FileHandle.standardError.write((text + "\n").data(using: .utf8)!)
}

// MARK: - プロセス

/// 親プロセスの ID (sysctl)。分からなければ 0。
func parentPid(_ pid: pid_t) -> pid_t {
    var info = kinfo_proc()
    var size = MemoryLayout<kinfo_proc>.stride
    var mib: [Int32] = [CTL_KERN, KERN_PROC, KERN_PROC_PID, pid]
    if sysctl(&mib, 4, &info, &size, nil, 0) != 0 || size == 0 { return 0 }
    return info.kp_eproc.e_ppid
}

/// pid が ancestor の子孫 (または同じ) か。
func isDescendant(_ pid: pid_t, of ancestor: pid_t) -> Bool {
    var p = pid
    for _ in 0..<16 {
        if p == ancestor { return true }
        if p <= 1 { return false }
        p = parentPid(p)
    }
    return false
}

/// GetText (この補助プログラムを起動したアプリ) のプロセス ID。
let hostPid: pid_t = getppid()

// MARK: - 音の取り込み

/// 取り込んだ音を 0.1 秒ずつ GetText に送る。音は録られた時刻 (タイムスタンプ) の位置に置き、
/// 音が届かない間も「送ったサンプル数 = 経過時間」になるよう無音を補う (発言の時刻が取り込みの遅れでずれないように)。
final class PcmSender {
    static let rate = 16000
    let stream: String
    private var buffer: [Int16] = []
    private var delivered = 0
    private let started = Date()
    private let startedHost = CMClockGetTime(CMClockGetHostTimeClock()).seconds
    private var lastArrival = Date()
    private var useTimestamps = true
    private let lock = NSLock()
    private var timer: DispatchSourceTimer?

    init(stream: String) {
        self.stream = stream
        let t = DispatchSource.makeTimerSource(queue: DispatchQueue.global(qos: .userInitiated))
        t.schedule(deadline: .now() + 0.1, repeating: 0.1)
        t.setEventHandler { [weak self] in self?.padSilence() }
        t.resume()
        timer = t
    }

    func stop() {
        timer?.cancel()
        timer = nil
    }

    /// at: 音の先頭が録られた時刻 (ホストの時計の秒)。分からなければ nil (届いた順につなげる)。
    func add(_ samples: [Int16], at: Double? = nil) {
        lock.lock()
        lastArrival = Date()
        if useTimestamps, let at {
            let position = Int((at - startedHost) * Double(PcmSender.rate))
            let have = delivered + buffer.count
            if abs(position - have) > PcmSender.rate * 30 {
                useTimestamps = false // 時計が合わない (想定外) ときは、届いた順につなげる
            } else if position > have {
                buffer.append(contentsOf: [Int16](repeating: 0, count: position - have)) // 間の無音
            }
        }
        buffer.append(contentsOf: samples)
        flush(final: false)
        lock.unlock()
    }

    private func padSilence() {
        lock.lock()
        // 音が 0.6 秒以上届いていないときだけ、今より 0.5 秒前までを無音で埋める
        // (届くのが少し遅れている音の場所を、先に無音で埋めてしまわないように)
        if Date().timeIntervalSince(lastArrival) > 0.6 {
            let expected = Int((Date().timeIntervalSince(started) - 0.5) * Double(PcmSender.rate))
            let missing = expected - delivered - buffer.count
            if missing > 0 {
                buffer.append(contentsOf: [Int16](repeating: 0, count: missing))
                flush(final: false)
            }
        }
        lock.unlock()
    }

    private func flush(final: Bool) {
        let chunk = PcmSender.rate / 10
        while buffer.count >= chunk {
            let part = Array(buffer[0..<chunk])
            buffer.removeFirst(chunk)
            delivered += part.count
            let data = part.withUnsafeBufferPointer { Data(buffer: $0) }
            send(["event": "audio", "stream": stream, "data": data.base64EncodedString()])
        }
    }
}

/// CMSampleBuffer (Float32 / Int16、チャンネル数は問わない) を 16bit モノラルにする。
func monoInt16(from sampleBuffer: CMSampleBuffer) -> [Int16] {
    guard let description = CMSampleBufferGetFormatDescription(sampleBuffer),
          let asbdPointer = CMAudioFormatDescriptionGetStreamBasicDescription(description) else { return [] }
    let asbd = asbdPointer.pointee
    var bufferListSize = 0
    CMSampleBufferGetAudioBufferListWithRetainedBlockBuffer(
        sampleBuffer, bufferListSizeNeededOut: &bufferListSize, bufferListOut: nil, bufferListSize: 0,
        blockBufferAllocator: nil, blockBufferMemoryAllocator: nil, flags: 0, blockBufferOut: nil)
    guard bufferListSize > 0 else { return [] }
    let raw = UnsafeMutableRawPointer.allocate(byteCount: bufferListSize, alignment: MemoryLayout<AudioBufferList>.alignment)
    defer { raw.deallocate() }
    let list = raw.bindMemory(to: AudioBufferList.self, capacity: 1)
    var block: CMBlockBuffer?
    let status = CMSampleBufferGetAudioBufferListWithRetainedBlockBuffer(
        sampleBuffer, bufferListSizeNeededOut: nil, bufferListOut: list, bufferListSize: bufferListSize,
        blockBufferAllocator: nil, blockBufferMemoryAllocator: nil,
        flags: kCMSampleBufferFlag_AudioBufferList_Assure16ByteAlignment, blockBufferOut: &block)
    guard status == noErr else { return [] }
    let buffers = UnsafeMutableAudioBufferListPointer(list)
    let frames = CMSampleBufferGetNumSamples(sampleBuffer)
    let isFloat = (asbd.mFormatFlags & kAudioFormatFlagIsFloat) != 0
    let nonInterleaved = (asbd.mFormatFlags & kAudioFormatFlagIsNonInterleaved) != 0
    let channels = max(1, Int(asbd.mChannelsPerFrame))
    var out = [Int16](repeating: 0, count: frames)
    for i in 0..<frames {
        var sum: Float = 0
        for c in 0..<channels {
            let bufferIndex = nonInterleaved ? min(c, buffers.count - 1) : 0
            let sampleIndex = nonInterleaved ? i : i * channels + c
            guard let data = buffers[bufferIndex].mData else { continue }
            if isFloat {
                sum += data.assumingMemoryBound(to: Float.self)[sampleIndex]
            } else {
                sum += Float(data.assumingMemoryBound(to: Int16.self)[sampleIndex]) / 32768
            }
        }
        let v = max(-1, min(1, sum / Float(channels)))
        out[i] = Int16(v * 32767)
    }
    return out
}

/// 16kHz 以外で届いた音を 16kHz にする (線形補間。ScreenCaptureKit には 16kHz を頼むので通常は使わない)。
func resample(_ samples: [Int16], from rate: Double) -> [Int16] {
    if abs(rate - Double(PcmSender.rate)) < 1 || samples.isEmpty { return samples }
    let ratio = rate / Double(PcmSender.rate)
    let count = Int(Double(samples.count) / ratio)
    var out = [Int16](repeating: 0, count: count)
    for i in 0..<count {
        let x = Double(i) * ratio
        let j = Int(x)
        let f = x - Double(j)
        let a = Double(samples[min(j, samples.count - 1)])
        let b = Double(samples[min(j + 1, samples.count - 1)])
        out[i] = Int16(a + (b - a) * f)
    }
    return out
}

/// アプリの音 (またはそれ以外のすべての音) を ScreenCaptureKit で取り込む。
final class AppAudioCapture: NSObject, SCStreamOutput, SCStreamDelegate {
    private var scStream: SCStream?
    private let sender: PcmSender
    private let queue = DispatchQueue(label: "gettext.audio")

    init(name: String) {
        sender = PcmSender(stream: name)
    }

    func start(pid: pid_t, exclude: Bool) async throws {
        let content = try await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: false)
        guard let display = content.displays.first else { throw HelperError("ディスプレイが見つかりません") }
        let filter: SCContentFilter
        if exclude {
            // 指定したアプリ (GetText) 以外のすべての音
            let excluded = content.applications.filter { isDescendant($0.processID, of: pid) }
            filter = SCContentFilter(display: display, excludingApplications: excluded, exceptingWindows: [])
        } else {
            // 指定したアプリと、その中で音を出す補助のプロセス (ブラウザの Helper など)
            let main = content.applications.first { $0.processID == pid }
            let prefix = main.map { $0.bundleIdentifier + "." } ?? "\u{0}"
            let apps = content.applications.filter {
                $0.processID == pid || isDescendant($0.processID, of: pid) || $0.bundleIdentifier.hasPrefix(prefix)
            }
            if apps.isEmpty { throw HelperError("選んだアプリが見つかりません (終了した可能性があります)") }
            filter = SCContentFilter(display: display, including: apps, exceptingWindows: [])
        }
        let config = SCStreamConfiguration()
        config.capturesAudio = true
        config.sampleRate = PcmSender.rate
        config.channelCount = 1
        config.excludesCurrentProcessAudio = true
        // 画面は使わないので、いちばん小さく・少なくする
        config.width = 2
        config.height = 2
        config.minimumFrameInterval = CMTime(value: 1, timescale: 1)
        config.queueDepth = 3
        let s = SCStream(filter: filter, configuration: config, delegate: self)
        try s.addStreamOutput(self, type: .audio, sampleHandlerQueue: queue)
        try s.addStreamOutput(self, type: .screen, sampleHandlerQueue: queue)
        try await s.startCapture()
        scStream = s
    }

    func stop() {
        sender.stop()
        let s = scStream
        scStream = nil
        s?.stopCapture { _ in }
    }

    func stream(_ stream: SCStream, didOutputSampleBuffer sampleBuffer: CMSampleBuffer, of type: SCStreamOutputType) {
        guard type == .audio, sampleBuffer.isValid else { return }
        var samples = monoInt16(from: sampleBuffer)
        if let d = CMSampleBufferGetFormatDescription(sampleBuffer),
           let asbd = CMAudioFormatDescriptionGetStreamBasicDescription(d)?.pointee {
            samples = resample(samples, from: asbd.mSampleRate)
        }
        let pts = CMSampleBufferGetPresentationTimeStamp(sampleBuffer)
        sender.add(samples, at: pts.isValid ? pts.seconds : nil)
    }

    func stream(_ stream: SCStream, didStopWithError error: Error) {
        send(["event": "audio_error", "stream": sender.stream, "message": message(of: error)])
    }
}

/// マイク (既定の入力) を AVAudioEngine で取り込む。
final class MicCapture {
    private let engine = AVAudioEngine()
    private let sender: PcmSender
    private var converter: AVAudioConverter?
    private let target = AVAudioFormat(commonFormat: .pcmFormatInt16, sampleRate: Double(PcmSender.rate), channels: 1, interleaved: true)!

    private var configObserver: NSObjectProtocol?
    private var stopped = false
    private let converterLock = NSLock() // 変換器は、音の取り込みのスレッドとつなぎ直し (メイン) の両方から使う

    init(name: String) {
        sender = PcmSender(stream: name)
    }

    func start() throws {
        try installAndStart()
        // 入力の機器が変わる (AirPods をつなぐ・USB マイクを抜く・既定の入力を変える) と AVAudioEngine は止まるので、つなぎ直す
        configObserver = NotificationCenter.default.addObserver(
            forName: .AVAudioEngineConfigurationChange, object: engine, queue: .main) { [weak self] _ in
            self?.restart()
        }
    }

    private func installAndStart() throws {
        let input = engine.inputNode
        let format = input.outputFormat(forBus: 0)
        if format.sampleRate == 0 || format.channelCount == 0 { throw HelperError("マイクが見つかりません") }
        let made = AVAudioConverter(from: format, to: target)
        converterLock.lock()
        converter = made
        converterLock.unlock()
        input.installTap(onBus: 0, bufferSize: 4096, format: format) { [weak self] buffer, when in
            self?.convert(buffer, at: when.isHostTimeValid ? AVAudioTime.seconds(forHostTime: when.hostTime) : nil)
        }
        engine.prepare()
        try engine.start()
    }

    private func restart(attempt: Int = 0) {
        if stopped { return }
        engine.inputNode.removeTap(onBus: 0)
        engine.stop()
        do {
            try installAndStart()
        } catch {
            // 切り替えの途中は、新しい機器がまだ 0 Hz のことがあるので、少し待ってやり直す
            if attempt < 3 {
                DispatchQueue.main.asyncAfter(deadline: .now() + 1) { [weak self] in self?.restart(attempt: attempt + 1) }
                return
            }
            send(["event": "audio_error", "stream": sender.stream,
                  "message": "マイクが切り替わったあと、マイクの音を取り込めませんでした (\(message(of: error)))"])
        }
    }

    private func convert(_ buffer: AVAudioPCMBuffer, at: Double?) {
        converterLock.lock()
        let current = converter
        converterLock.unlock()
        guard let converter = current else { return }
        let capacity = AVAudioFrameCount(Double(buffer.frameLength) * target.sampleRate / buffer.format.sampleRate) + 16
        guard let out = AVAudioPCMBuffer(pcmFormat: target, frameCapacity: capacity) else { return }
        var consumed = false
        var error: NSError?
        converter.convert(to: out, error: &error) { _, status in
            if consumed {
                status.pointee = .noDataNow
                return nil
            }
            consumed = true
            status.pointee = .haveData
            return buffer
        }
        guard error == nil, let data = out.int16ChannelData else { return }
        sender.add(Array(UnsafeBufferPointer(start: data[0], count: Int(out.frameLength))), at: at)
    }

    func stop() {
        // つなぎ直し (メインのスレッド) と重ならないよう、メインのスレッドで止める
        let body = {
            self.stopped = true
            if let o = self.configObserver { NotificationCenter.default.removeObserver(o) }
            self.configObserver = nil
            self.sender.stop()
            self.engine.inputNode.removeTap(onBus: 0)
            self.engine.stop()
        }
        if Thread.isMainThread { body() } else { DispatchQueue.main.sync(execute: body) }
    }
}

var appCaptures: [String: AppAudioCapture] = [:]
var micCaptures: [String: MicCapture] = [:]
let captureLock = NSLock()
// 取り込みの名前ごとの「止めた」回数 (始めている途中に止められたら、始め終わったときにすぐ止める)
var captureGeneration: [String: Int] = [:]

/// 始め終わったアプリの音の取り込みを登録する。始めている間に止められていたら登録せずに false。
func registerAppCapture(_ capture: AppAudioCapture, name: String, generation: Int) -> Bool {
    captureLock.lock()
    defer { captureLock.unlock() }
    if captureGeneration[name, default: 0] != generation { return false }
    appCaptures[name] = capture
    return true
}

func stopAudio(_ name: String) {
    captureLock.lock()
    captureGeneration[name, default: 0] += 1
    let app = appCaptures.removeValue(forKey: name)
    let mic = micCaptures.removeValue(forKey: name)
    captureLock.unlock()
    app?.stop()
    mic?.stop()
}

// MARK: - ウィンドウの一覧・音を出しているアプリ

/// いま音を出しているプロセス (macOS 14.4 以降の Core Audio のプロセスの一覧)。
func playingPids() -> Set<pid_t> {
    var result = Set<pid_t>()
    if #available(macOS 14.4, *) {
        var address = AudioObjectPropertyAddress(
            mSelector: kAudioHardwarePropertyProcessObjectList,
            mScope: kAudioObjectPropertyScopeGlobal,
            mElement: kAudioObjectPropertyElementMain)
        var size: UInt32 = 0
        let system = AudioObjectID(kAudioObjectSystemObject)
        guard AudioObjectGetPropertyDataSize(system, &address, 0, nil, &size) == noErr, size > 0 else { return result }
        var objects = [AudioObjectID](repeating: 0, count: Int(size) / MemoryLayout<AudioObjectID>.size)
        guard AudioObjectGetPropertyData(system, &address, 0, nil, &size, &objects) == noErr else { return result }
        for object in objects {
            var running: UInt32 = 0
            var runningSize = UInt32(MemoryLayout<UInt32>.size)
            var runningAddress = AudioObjectPropertyAddress(
                mSelector: kAudioProcessPropertyIsRunningOutput,
                mScope: kAudioObjectPropertyScopeGlobal,
                mElement: kAudioObjectPropertyElementMain)
            guard AudioObjectGetPropertyData(object, &runningAddress, 0, nil, &runningSize, &running) == noErr, running != 0 else { continue }
            var pid: pid_t = 0
            var pidSize = UInt32(MemoryLayout<pid_t>.size)
            var pidAddress = AudioObjectPropertyAddress(
                mSelector: kAudioProcessPropertyPID,
                mScope: kAudioObjectPropertyScopeGlobal,
                mElement: kAudioObjectPropertyElementMain)
            if AudioObjectGetPropertyData(object, &pidAddress, 0, nil, &pidSize, &pid) == noErr, pid > 0 {
                // 音を出すのが補助のプロセスでも、親のアプリを「音を出している」とする
                var p = pid
                for _ in 0..<8 where p > 1 {
                    result.insert(p)
                    p = parentPid(p)
                }
            }
        }
    }
    return result
}

/// 表示中のウィンドウ (アプリごとに、いちばん大きいもの 1 つ)。
func listWindows() -> [[String: Any]] {
    let playing = playingPids()
    guard let infos = CGWindowListCopyWindowInfo([.optionOnScreenOnly, .excludeDesktopElements], kCGNullWindowID) as? [[String: Any]] else { return [] }
    var best: [pid_t: (area: Double, item: [String: Any])] = [:]
    for info in infos {
        guard (info[kCGWindowLayer as String] as? Int) == 0,
              let pid = info[kCGWindowOwnerPID as String] as? pid_t, pid != hostPid, pid != getpid() else { continue }
        let owner = info[kCGWindowOwnerName as String] as? String ?? ""
        let title = info[kCGWindowName as String] as? String ?? ""
        var area = 0.0
        if let b = info[kCGWindowBounds as String] as? [String: Any],
           let rect = CGRect(dictionaryRepresentation: b as CFDictionary) {
            area = rect.width * rect.height
            if rect.width < 80 || rect.height < 50 { continue }
        }
        let item: [String: Any] = [
            "id": info[kCGWindowNumber as String] as? Int ?? 0,
            "pid": Int(pid),
            "app": owner,
            "title": title.isEmpty ? owner : title,
            "playing": playing.contains(pid),
            "area": Int(area),
        ]
        if area > (best[pid]?.area ?? -1) { best[pid] = (area, item) }
    }
    return best.values.map { $0.item }
}

// MARK: - 画面の取り込みと文字認識

/// ウィンドウの位置 (グローバルのポイント座標、左上が原点)。
func windowBounds(_ number: Int) -> CGRect? {
    guard let infos = CGWindowListCopyWindowInfo([.optionIncludingWindow], CGWindowID(number)) as? [[String: Any]],
          let info = infos.first, let b = info[kCGWindowBounds as String] as? [String: Any] else { return nil }
    return CGRect(dictionaryRepresentation: b as CFDictionary)
}

/// rect (グローバルのポイント座標) を取り込む。GetText 自身の窓は写さない。scale は 1 ポイントあたりのピクセル数。
func captureRect(_ rect: CGRect, scale: CGFloat) async throws -> CGImage {
    guard #available(macOS 14.0, *) else { throw HelperError("画面の取り込みには macOS 14 以降が必要です") }
    let content = try await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: true)
    let center = CGPoint(x: rect.midX, y: rect.midY)
    guard let display = content.displays.first(where: { $0.frame.contains(center) }) ?? content.displays.first else {
        throw HelperError("ディスプレイが見つかりません")
    }
    let own = content.applications.filter { isDescendant($0.processID, of: hostPid) || $0.processID == getpid() }
    let filter = SCContentFilter(display: display, excludingApplications: own, exceptingWindows: [])
    let config = SCStreamConfiguration()
    let local = rect.offsetBy(dx: -display.frame.minX, dy: -display.frame.minY).intersection(CGRect(origin: .zero, size: display.frame.size))
    if local.isEmpty { throw HelperError("取り込む範囲が画面の外です") }
    config.sourceRect = local
    config.width = max(1, Int(local.width * scale))
    config.height = max(1, Int(local.height * scale))
    config.showsCursor = false
    config.captureResolution = .best
    return try await SCScreenshotManager.captureImage(contentFilter: filter, configuration: config)
}

/// 画像を小さくした指紋 (前回と比べて、画面が変わったかを判断する)。
func fingerprint(_ image: CGImage) -> [UInt8] {
    let w = 64, h = max(1, Int(64.0 * Double(image.height) / Double(max(1, image.width))))
    var pixels = [UInt8](repeating: 0, count: w * h)
    guard let context = CGContext(data: &pixels, width: w, height: h, bitsPerComponent: 8, bytesPerRow: w,
                                  space: CGColorSpaceCreateDeviceGray(), bitmapInfo: CGImageAlphaInfo.none.rawValue) else { return [] }
    context.interpolationQuality = .medium
    context.draw(image, in: CGRect(x: 0, y: 0, width: w, height: h))
    return pixels
}

var lastFingerprint: [UInt8] = []
let ocrLock = NSLock()
var ocrRunning = false

// (ロックは非同期の処理の中から直接使えないので、同期の関数にまとめる)
func finishOcr() {
    ocrLock.lock()
    ocrRunning = false
    ocrLock.unlock()
}

func changed(_ a: [UInt8], _ b: [UInt8]) -> Bool {
    if a.count != b.count { return true }
    var differing = 0
    for i in 0..<a.count where abs(Int(a[i]) - Int(b[i])) > 6 {
        differing += 1
        if differing > 2 { return true }
    }
    return false
}

/// 画像の文字を読む (Vision)。座標は画像のピクセル (左上が原点)。
func recognize(_ image: CGImage, languages: [String]) throws -> [[String: Any]] {
    let request = VNRecognizeTextRequest()
    request.recognitionLevel = .accurate
    request.usesLanguageCorrection = true
    request.recognitionLanguages = languages
    if #available(macOS 13.0, *) { request.automaticallyDetectsLanguage = languages.count > 1 }
    let handler = VNImageRequestHandler(cgImage: image, options: [:])
    try handler.perform([request])
    let w = Double(image.width), h = Double(image.height)
    var lines: [[String: Any]] = []
    for observation in request.results ?? [] {
        guard let candidate = observation.topCandidates(1).first else { continue }
        let b = observation.boundingBox // 左下が原点の 0〜1
        lines.append([
            "text": candidate.string,
            "left": b.minX * w,
            "top": (1 - b.maxY) * h,
            "right": b.maxX * w,
            "bottom": (1 - b.minY) * h,
            "confidence": candidate.confidence,
        ])
    }
    return lines.sorted { ($0["top"] as! Double, $0["left"] as! Double) < ($1["top"] as! Double, $1["left"] as! Double) }
}

func bgra(_ image: CGImage) -> Data {
    let w = image.width, h = image.height
    var data = Data(count: w * h * 4)
    data.withUnsafeMutableBytes { buffer in
        guard let context = CGContext(data: buffer.baseAddress, width: w, height: h, bitsPerComponent: 8, bytesPerRow: w * 4,
                                      space: CGColorSpaceCreateDeviceRGB(),
                                      bitmapInfo: CGImageAlphaInfo.premultipliedFirst.rawValue | CGBitmapInfo.byteOrder32Little.rawValue) else { return }
        context.draw(image, in: CGRect(x: 0, y: 0, width: w, height: h))
    }
    return data
}

// MARK: - ショートカット (Carbon)

var hotKeyRefs: [UInt32: EventHotKeyRef] = [:]
var hotKeyHandlerInstalled = false

func installHotKeyHandler() {
    if hotKeyHandlerInstalled { return }
    hotKeyHandlerInstalled = true
    var type = EventTypeSpec(eventClass: OSType(kEventClassKeyboard), eventKind: UInt32(kEventHotKeyPressed))
    InstallEventHandler(GetApplicationEventTarget(), { _, event, _ -> OSStatus in
        var id = EventHotKeyID()
        GetEventParameter(event, EventParamName(kEventParamDirectObject), EventParamType(typeEventHotKeyID), nil,
                          MemoryLayout<EventHotKeyID>.size, nil, &id)
        send(["event": "hotkey", "id": Int(id.id)])
        return noErr
    }, 1, &type, nil, nil)
}

/// mods: "ctrl" "alt" (option) "shift" "cmd" の組み合わせ。key: Mac の仮想キーコード。
func registerHotKey(id: UInt32, key: UInt32, mods: [String]) -> Bool {
    installHotKeyHandler()
    if let old = hotKeyRefs.removeValue(forKey: id) { UnregisterEventHotKey(old) }
    var flags: UInt32 = 0
    if mods.contains("ctrl") { flags |= UInt32(controlKey) }
    if mods.contains("alt") { flags |= UInt32(optionKey) }
    if mods.contains("shift") { flags |= UInt32(shiftKey) }
    if mods.contains("cmd") { flags |= UInt32(cmdKey) }
    var ref: EventHotKeyRef?
    let status = RegisterEventHotKey(key, flags, EventHotKeyID(signature: OSType(0x4754_5854), id: id),
                                     GetApplicationEventTarget(), 0, &ref)
    guard status == noErr, let ref else { return false }
    hotKeyRefs[id] = ref
    return true
}

func unregisterHotKeys() {
    for ref in hotKeyRefs.values { UnregisterEventHotKey(ref) }
    hotKeyRefs.removeAll()
}

// MARK: - キーチェーン

func secretQuery(_ name: String, service: String) -> [String: Any] {
    [kSecClass as String: kSecClassGenericPassword, kSecAttrService as String: service, kSecAttrAccount as String: name]
}

func secretGet(_ name: String, service: String) -> String? {
    var query = secretQuery(name, service: service)
    query[kSecReturnData as String] = true
    query[kSecMatchLimit as String] = kSecMatchLimitOne
    var item: CFTypeRef?
    guard SecItemCopyMatching(query as CFDictionary, &item) == errSecSuccess, let data = item as? Data else { return nil }
    return String(data: data, encoding: .utf8)
}

func secretSet(_ name: String, value: String, service: String) throws {
    let data = value.data(using: .utf8)!
    let query = secretQuery(name, service: service)
    let status = SecItemUpdate(query as CFDictionary, [kSecValueData as String: data] as CFDictionary)
    if status == errSecItemNotFound {
        var add = query
        add[kSecValueData as String] = data
        let s = SecItemAdd(add as CFDictionary, nil)
        if s != errSecSuccess { throw HelperError("キーチェーンに保存できませんでした (\(s))") }
    } else if status != errSecSuccess {
        throw HelperError("キーチェーンに保存できませんでした (\(status))")
    }
}

func secretDelete(_ name: String, service: String) {
    SecItemDelete(secretQuery(name, service: service) as CFDictionary)
}

// MARK: - 画面の録画

/// 選んだウィンドウ (または画面全体) を、その音も入れて MP4 (H.264 + AAC) に録画する (ScreenCaptureKit + AVAssetWriter)。
/// 会議アプリには何も伝わらない (会議アプリ自身のレコーディングとは別)。取り込みを禁止したウィンドウは ScreenCaptureKit が渡さない。
final class ScreenRecording: NSObject, SCStreamOutput, SCStreamDelegate {
    private var stream: SCStream?
    private var audioStream: SCStream? // ウィンドウを録画するときの音 (アプリと、その中で音を出す補助のプロセス)
    private let audioMarkLock = NSLock()
    private var audioMark: ObjectIdentifier? // 音だけの取り込み (始める前から印を付ける)

    private func setAudioMark(_ s: SCStream?) {
        audioMarkLock.lock()
        audioMark = s.map { ObjectIdentifier($0) }
        audioMarkLock.unlock()
    }

    private func isAudioStream(_ s: SCStream) -> Bool {
        audioMarkLock.lock()
        defer { audioMarkLock.unlock() }
        return audioMark == ObjectIdentifier(s)
    }
    private var cancelled = false      // 始めている途中に止められた (queue で読み書きする)
    private var failureSent = false
    private var writer: AVAssetWriter?
    private var videoInput: AVAssetWriterInput?
    private var audioInput: AVAssetWriterInput?
    private let queue = DispatchQueue(label: "gettext.record")
    private var started = false
    private var firstTime = CMTime.invalid
    private var lastTime = CMTime.invalid
    private var lastFrame: CMSampleBuffer?
    private var stopping = false
    let path: String
    var token = 0 // 録画の番号 (知らせに付けて、どの画面の録画かを分かるようにする)
    private(set) var width = 0
    private(set) var height = 0

    init(path: String) {
        self.path = path
    }

    /// window: ウィンドウの番号 (0 なら display の画面全体)。audio: そのアプリ (画面全体なら GetText 以外のすべて) の音も入れる。
    func start(window: Int, display: Int, audio: Bool, fps: Int, highQuality: Bool) async throws {
        let content = try await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: false)
        let filter: SCContentFilter
        var size: CGSize
        var scale: CGFloat = 2
        if window != 0 {
            guard let w = content.windows.first(where: { Int($0.windowID) == window }) else {
                throw HelperError("録画するウィンドウが見つかりません (閉じた可能性があります)")
            }
            filter = SCContentFilter(desktopIndependentWindow: w)
            size = w.frame.size
            if let screen = NSScreen.screens.first(where: { $0.frame.intersects(w.frame) }) { scale = screen.backingScaleFactor }
        } else {
            guard let d = content.displays.first(where: { Int($0.displayID) == display }) ?? content.displays.first else {
                throw HelperError("ディスプレイが見つかりません")
            }
            let me = content.applications.filter { $0.processID == getpid() || $0.processID == hostPid }
            filter = SCContentFilter(display: d, excludingApplications: me, exceptingWindows: [])
            size = CGSize(width: d.width, height: d.height)
            if let screen = NSScreen.screens.first(where: { ($0.deviceDescription[NSDeviceDescriptionKey("NSScreenNumber")] as? NSNumber)?.uint32Value == d.displayID }) {
                scale = screen.backingScaleFactor
            }
        }
        // H.264 は縦横が偶数。4K までにする
        var w = Int(size.width * scale), h = Int(size.height * scale)
        let shrink = min(1.0, min(3840.0 / Double(max(w, 1)), 2160.0 / Double(max(h, 1))))
        w = max(64, Int(Double(w) * shrink) & ~1)
        h = max(64, Int(Double(h) * shrink) & ~1)
        width = w
        height = h

        let config = SCStreamConfiguration()
        config.width = w
        config.height = h
        config.pixelFormat = kCVPixelFormatType_32BGRA
        config.minimumFrameInterval = CMTime(value: 1, timescale: CMTimeScale(max(1, fps)))
        config.showsCursor = true
        config.queueDepth = 6
        // ウィンドウだけを録画するフィルターの音は、そのウィンドウのアプリの音だけになり、ブラウザ (音は補助のプロセスが
        // 出す) の会議では音が入らない。ウィンドウのときは、議事録の取り込みと同じく補助のプロセスも含めた音を別に取り込む
        var audioFilter: SCContentFilter?
        if audio, window != 0, let w = content.windows.first(where: { Int($0.windowID) == window }),
           let owner = w.owningApplication, let display = content.displays.first {
            let prefix = owner.bundleIdentifier + "."
            let apps = content.applications.filter {
                $0.processID == owner.processID || isDescendant($0.processID, of: owner.processID) || $0.bundleIdentifier.hasPrefix(prefix)
            }
            audioFilter = SCContentFilter(display: display, including: apps, exceptingWindows: [])
        }
        if audio && audioFilter == nil {
            config.capturesAudio = true
            config.sampleRate = 48000
            config.channelCount = 2
            config.excludesCurrentProcessAudio = true
        }

        let url = URL(fileURLWithPath: path)
        try? FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        try? FileManager.default.removeItem(at: url)
        let writer = try AVAssetWriter(outputURL: url, fileType: .mp4)
        let bitrate = min(50_000_000, max(1_000_000, Int(Double(w * h * max(1, fps)) * (highQuality ? 0.16 : 0.08))))
        let video = AVAssetWriterInput(mediaType: .video, outputSettings: [
            AVVideoCodecKey: AVVideoCodecType.h264,
            AVVideoWidthKey: w,
            AVVideoHeightKey: h,
            AVVideoCompressionPropertiesKey: [AVVideoAverageBitRateKey: bitrate],
        ])
        video.expectsMediaDataInRealTime = true
        writer.add(video)
        if audio {
            let a = AVAssetWriterInput(mediaType: .audio, outputSettings: [
                AVFormatIDKey: kAudioFormatMPEG4AAC,
                AVSampleRateKey: 48000,
                AVNumberOfChannelsKey: 2,
                AVEncoderBitRateKey: 192_000,
            ])
            a.expectsMediaDataInRealTime = true
            writer.add(a)
            audioInput = a
        }
        self.writer = writer
        videoInput = video

        var a: SCStream?
        if let audioFilter {
            let ac = SCStreamConfiguration()
            ac.capturesAudio = true
            ac.sampleRate = 48000
            ac.channelCount = 2
            ac.excludesCurrentProcessAudio = true
            ac.width = 2 // 画面は使わないので、いちばん小さく・少なくする
            ac.height = 2
            ac.minimumFrameInterval = CMTime(value: 1, timescale: 1)
            ac.queueDepth = 3
            let made = SCStream(filter: audioFilter, configuration: ac, delegate: self)
            setAudioMark(made)
            do {
                try made.addStreamOutput(self, type: .audio, sampleHandlerQueue: queue)
                try await made.startCapture()
                a = made
            } catch {
                // 始められなければ、ウィンドウのアプリの音だけを画面と一緒に取り込む
                a = nil
                setAudioMark(nil)
                config.capturesAudio = true
                config.sampleRate = 48000
                config.channelCount = 2
                config.excludesCurrentProcessAudio = true
            }
        }
        let s = SCStream(filter: filter, configuration: config, delegate: self)
        do {
            try s.addStreamOutput(self, type: .screen, sampleHandlerQueue: queue)
            if audio && a == nil { try s.addStreamOutput(self, type: .audio, sampleHandlerQueue: queue) }
            try await s.startCapture()
        } catch {
            // (始めた音だけの取り込みを残さない)
            if let a { try? await a.stopCapture() }
            throw error
        }
        // 始めている間に止められた (アプリが待ちきれずに止めた・アプリが終わった) ら、すぐ止める。
        // 止められたかの確認と、取り込みを覚えるのは同じ queue で行う (間に止める要求が来ても取りこぼさない)
        let wasCancelled = queue.sync { () -> Bool in
            if cancelled { return true }
            stream = s
            audioStream = a
            return false
        }
        if wasCancelled {
            try? await s.stopCapture()
            if let a { try? await a.stopCapture() }
            throw HelperError("録画を始める前に止められました")
        }
    }

    func stream(_ stream: SCStream, didOutputSampleBuffer sampleBuffer: CMSampleBuffer, of type: SCStreamOutputType) {
        guard sampleBuffer.isValid, !stopping, let writer else { return }
        let time = CMSampleBufferGetPresentationTimeStamp(sampleBuffer)
        if type == .screen {
            // 画面が変わっていないときなどは画像の無い通知が来るので飛ばす
            guard let attachments = CMSampleBufferGetSampleAttachmentsArray(sampleBuffer, createIfNecessary: false) as? [[SCStreamFrameInfo: Any]],
                  let raw = attachments.first?[.status] as? Int, SCFrameStatus(rawValue: raw) == .complete else { return }
            if !started {
                guard writer.startWriting() else { return }
                writer.startSession(atSourceTime: time)
                firstTime = time
                started = true
            }
            lastTime = time
            lastFrame = sampleBuffer
            if let input = videoInput, input.isReadyForMoreMediaData, !input.append(sampleBuffer) { writerFailed(writer) }
        } else if type == .audio, started, time >= firstTime {
            if let input = audioInput, input.isReadyForMoreMediaData, !input.append(sampleBuffer) { writerFailed(writer) }
        }
    }

    /// 書き出しが途中で失敗した (ディスクがいっぱいなど): 録画中にすぐ知らせる (1 回だけ)。
    private func writerFailed(_ writer: AVAssetWriter) {
        guard writer.status == .failed, !failureSent else { return }
        failureSent = true
        send(["event": "record_error", "token": token, "message": "録画を書き出せません: " + (writer.error.map { message(of: $0) } ?? "不明なエラー")])
    }

    func stream(_ stream: SCStream, didStopWithError error: Error) {
        // 音だけの取り込みが止まっても、画面の録画は続ける (音が途中から入らなくなるだけ)
        if isAudioStream(stream) { return } // (この知らせは queue の上で来ることもあるので、queue.sync は使わない)
        send(["event": "record_error", "token": token, "message": message(of: error)])
    }

    /// 録画した長さ (秒)。
    var seconds: Double {
        queue.sync { started && lastTime.isValid ? CMTimeGetSeconds(CMTimeSubtract(lastTime, firstTime)) : 0 }
    }

    /// 止めて書き終える。書き終えたら (長さ, エラー) を返す。
    func stop(completion: @escaping (Double, String?) -> Void) {
        // (始めている途中なら、始め終わったところで止める。取り込みは同じ queue で取り出す)
        let (s, a) = queue.sync { () -> (SCStream?, SCStream?) in
            cancelled = true
            let taken = (stream, audioStream)
            stream = nil
            audioStream = nil
            return taken
        }
        a?.stopCapture { _ in }
        let finish = {
            self.queue.async {
                self.stopping = true
                // 画面が変わらないと新しい画面は来ないので、止めた時刻に最後の画面をもう一度入れて、そこまでを録画にする
                // (入れないと、最後に画面が変わった所で動画も音も終わってしまう)
                let now = CMClockGetTime(CMClockGetHostTimeClock())
                if self.started, let last = self.lastFrame, now > self.lastTime, let input = self.videoInput, input.isReadyForMoreMediaData {
                    var timing = CMSampleTimingInfo(duration: .invalid, presentationTimeStamp: now, decodeTimeStamp: .invalid)
                    var copy: CMSampleBuffer?
                    if CMSampleBufferCreateCopyWithNewTiming(allocator: nil, sampleBuffer: last, sampleTimingEntryCount: 1,
                                                             sampleTimingArray: &timing, sampleBufferOut: &copy) == noErr, let copy {
                        input.append(copy)
                        self.lastTime = now
                    }
                }
                self.lastFrame = nil
                let length = self.started && self.lastTime.isValid ? CMTimeGetSeconds(CMTimeSubtract(self.lastTime, self.firstTime)) : 0
                if let writer = self.writer, writer.status == .failed {
                    completion(0, "録画を書き出せませんでした: " + (writer.error.map { message(of: $0) } ?? "不明なエラー"))
                    return
                }
                guard let writer = self.writer, self.started, writer.status == .writing else {
                    completion(0, "録画できた画面がありませんでした (ウィンドウが隠れていた・取り込みを禁止しているなど)")
                    return
                }
                self.videoInput?.markAsFinished()
                self.audioInput?.markAsFinished()
                writer.endSession(atSourceTime: self.lastTime)
                writer.finishWriting {
                    completion(length, writer.status == .completed ? nil : (writer.error.map { message(of: $0) } ?? "書き出せませんでした"))
                }
            }
        }
        if let s {
            s.stopCapture { _ in finish() }
        } else {
            finish()
        }
    }
}

var recording: ScreenRecording?
var recordingToken = 0                    // 録画ごとの番号 (画面の録画と議事録の録画を取り違えないように)
var finishingRecordings: [ScreenRecording] = [] // 書き終えている途中の録画 (GetText が終わるときに待つ)
let recordLock = NSLock()                 // recording などは、要求を読むスレッドと録画を始める処理の両方から使う

func withRecordLock<T>(_ body: () -> T) -> T {
    recordLock.lock()
    defer { recordLock.unlock() }
    return body()
}

/// 録画を止めて書き終える (書き終えるまでは finishingRecordings に入れておく)。
func finishRecording(_ r: ScreenRecording, completion: @escaping (Double, String?) -> Void) {
    withRecordLock { finishingRecordings.append(r) }
    r.stop { seconds, error in
        withRecordLock { finishingRecordings.removeAll { $0 === r } }
        completion(seconds, error)
    }
}

/// 録画の画面のプレビュー: 選んだウィンドウ (または画面全体) を小さな JPEG にする (録画中でも使える)。
func recordPreview(window: Int, display: Int, maxWidth: Int) async throws -> [String: Any] {
    let content = try await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: false)
    let filter: SCContentFilter
    var size: CGSize
    if window != 0 {
        guard let w = content.windows.first(where: { Int($0.windowID) == window }) else { throw HelperError("ウィンドウが見つかりません") }
        filter = SCContentFilter(desktopIndependentWindow: w)
        size = w.frame.size
    } else {
        guard let d = content.displays.first(where: { Int($0.displayID) == display }) ?? content.displays.first else { throw HelperError("ディスプレイが見つかりません") }
        let me = content.applications.filter { $0.processID == getpid() || $0.processID == hostPid }
        filter = SCContentFilter(display: d, excludingApplications: me, exceptingWindows: [])
        size = CGSize(width: d.width, height: d.height)
    }
    let scale = min(1.0, Double(maxWidth) / Double(max(1, size.width)))
    let config = SCStreamConfiguration()
    config.width = max(16, Int(size.width * scale))
    config.height = max(16, Int(size.height * scale))
    config.showsCursor = false
    let image = try await SCScreenshotManager.captureImage(contentFilter: filter, configuration: config)
    let rep = NSBitmapImageRep(cgImage: image)
    guard let jpeg = rep.representation(using: .jpeg, properties: [.compressionFactor: 0.7]) else { throw HelperError("プレビューを作れません") }
    return ["jpeg": jpeg.base64EncodedString(), "w": image.width, "h": image.height]
}

/// 録画に選べるもの: ウィンドウ (1 つずつ) と画面 (ディスプレイ)。
func recordTargets() async throws -> [String: Any] {
    let content = try await SCShareableContent.excludingDesktopWindows(true, onScreenWindowsOnly: true)
    let playing = playingPids()
    var windows: [[String: Any]] = []
    for w in content.windows {
        guard w.windowLayer == 0, w.frame.width >= 80, w.frame.height >= 50, let app = w.owningApplication,
              app.processID != getpid(), app.processID != hostPid else { continue }
        let title = (w.title ?? "").isEmpty ? app.applicationName : (w.title ?? "")
        windows.append([
            "id": Int(w.windowID), "pid": Int(app.processID), "app": app.applicationName, "title": title,
            "playing": playing.contains(app.processID), "area": Int(w.frame.width * w.frame.height),
        ])
    }
    let displays = content.displays.map { ["id": Int($0.displayID), "w": $0.width, "h": $0.height, "main": $0.displayID == CGMainDisplayID()] as [String: Any] }
    return ["windows": windows, "displays": displays]
}

// MARK: - 再生

var player: AVPlayer?
var playerPath: String?
var playEndObserver: Any?
var itemEndObserver: NSObjectProtocol?

func play(path: String, from: Double, until: Double) throws {
    if !FileManager.default.fileExists(atPath: path) { throw HelperError("ファイルが見つかりません: \(path)") }
    if playerPath != path && !AVURLAsset(url: URL(fileURLWithPath: path)).isPlayable {
        throw HelperError("この形式のファイルは Mac で再生できません (mkv・webm など)")
    }
    // 終わりの印は、付けたときの再生器から外す (別の再生器から外すと AVFoundation が例外で落ちる)
    if let o = playEndObserver {
        player?.removeTimeObserver(o)
        playEndObserver = nil
    }
    if playerPath != path {
        player?.pause()
        player = AVPlayer(url: URL(fileURLWithPath: path))
        playerPath = path
        // 最後の発言は「ここまで」がファイルの終わりより後になるので、ファイルの終わりでも「終わった」を送る
        if let o = itemEndObserver { NotificationCenter.default.removeObserver(o) }
        itemEndObserver = NotificationCenter.default.addObserver(
            forName: .AVPlayerItemDidPlayToEndTime, object: player?.currentItem, queue: .main) { _ in
            send(["event": "play_end"])
        }
    }
    guard let player else { return }
    playEndObserver = player.addBoundaryTimeObserver(
        forTimes: [NSValue(time: CMTime(seconds: max(from + 0.05, until), preferredTimescale: 600))], queue: .main) {
        player.pause()
        send(["event": "play_end"])
    }
    playToken += 1
    let token = playToken
    player.seek(to: CMTime(seconds: from, preferredTimescale: 600), toleranceBefore: .zero, toleranceAfter: .zero) { _ in
        DispatchQueue.main.async {
            if token == playToken { player.play() }
        }
    }
}

func stopPlayback() {
    playToken += 1 // (位置を合わせている途中なら、合わせ終わっても再生しない)
    player?.pause()
}

var playToken = 0

// MARK: - 動作確認 (CI 用)

/// 日本語と英語の文字を描いた画像を作る (文字認識の確認用)。
func renderText(_ lines: [String]) -> CGImage? {
    let w = 900, h = 70 * lines.count + 40
    guard let context = CGContext(data: nil, width: w, height: h, bitsPerComponent: 8, bytesPerRow: 0,
                                  space: CGColorSpaceCreateDeviceRGB(), bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else { return nil }
    context.setFillColor(CGColor(red: 1, green: 1, blue: 1, alpha: 1))
    context.fill(CGRect(x: 0, y: 0, width: w, height: h))
    let graphics = NSGraphicsContext(cgContext: context, flipped: false)
    NSGraphicsContext.saveGraphicsState()
    NSGraphicsContext.current = graphics
    let font = NSFont(name: "Hiragino Sans", size: 40) ?? NSFont.systemFont(ofSize: 40)
    for (i, line) in lines.enumerated() {
        let text = NSAttributedString(string: line, attributes: [.font: font, .foregroundColor: NSColor.black])
        text.draw(at: NSPoint(x: 30, y: h - 70 * (i + 1)))
    }
    NSGraphicsContext.restoreGraphicsState()
    return context.makeImage()
}

// MARK: - 要求の処理

func handle(_ request: [String: Any]) {
    let id = request["id"]
    let cmd = request["cmd"] as? String ?? ""
    switch cmd {
    case "hello":
        let v = ProcessInfo.processInfo.operatingSystemVersion
        reply(id, ["macos": "\(v.majorVersion).\(v.minorVersion).\(v.patchVersion)", "pid": Int(getpid()), "host": Int(hostPid)])

    case "permissions":
        let mic: String
        switch AVCaptureDevice.authorizationStatus(for: .audio) {
        case .authorized: mic = "granted"
        case .denied, .restricted: mic = "denied"
        default: mic = "undetermined"
        }
        reply(id, ["screen": CGPreflightScreenCaptureAccess(), "mic": mic])

    case "request_screen":
        reply(id, ["granted": CGRequestScreenCaptureAccess()])

    case "request_mic":
        AVCaptureDevice.requestAccess(for: .audio) { granted in reply(id, ["granted": granted]) }

    case "record_targets":
        Task {
            do { reply(id, try await recordTargets()) } catch { fail(id, message(of: error)) }
        }

    case "record_start":
        let r = ScreenRecording(path: request["path"] as? String ?? "")
        let wanted = request["token"] as? Int
        let token: Int? = withRecordLock {
            if recording != nil { return nil }
            recording = r
            recordingToken = wanted ?? (recordingToken + 1)
            r.token = recordingToken
            return recordingToken
        }
        guard let token else {
            fail(id, "もう録画しています (画面の録画と議事録の録画は同時にはできません)")
            return
        }
        Task {
            do {
                try await r.start(window: request["window"] as? Int ?? 0, display: request["display"] as? Int ?? 0,
                                  audio: request["audio"] as? Bool ?? true, fps: request["fps"] as? Int ?? 30,
                                  highQuality: request["hq"] as? Bool ?? false)
                reply(id, ["w": r.width, "h": r.height, "token": token])
            } catch {
                withRecordLock { if recording === r { recording = nil } }
                fail(id, "録画を始められません: " + message(of: error)
                     + (CGPreflightScreenCaptureAccess() ? "" : " (システム設定 → プライバシーとセキュリティ → 画面収録とシステムオーディオ録音 で GetText を許可してください)"))
            }
        }

    case "record_preview":
        Task {
            do {
                reply(id, try await recordPreview(window: request["window"] as? Int ?? 0, display: request["display"] as? Int ?? 0,
                                                  maxWidth: request["max"] as? Int ?? 640))
            } catch {
                fail(id, message(of: error))
            }
        }

    case "record_status":
        let r = withRecordLock { recording }
        reply(id, ["seconds": r?.seconds ?? 0, "recording": r != nil, "token": withRecordLock { recordingToken }])

    case "record_stop":
        // token を渡されたら、その録画のときだけ止める (ほかの画面が始めた録画を止めない)
        let wanted = request["token"] as? Int
        let r: ScreenRecording? = withRecordLock {
            guard let current = recording, wanted == nil || wanted == recordingToken else { return nil }
            recording = nil
            return current
        }
        guard let r else {
            fail(id, "録画していません")
            return
        }
        finishRecording(r) { seconds, error in
            if let error { fail(id, error) } else { reply(id, ["seconds": seconds, "path": r.path]) }
        }

    case "windows":
        reply(id, ["windows": listWindows()])

    case "playing":
        reply(id, ["pids": playingPids().map { Int($0) }])

    case "audio_start":
        let name = request["stream"] as? String ?? "win"
        stopAudio(name)
        if request["mic"] as? Bool == true {
            do {
                let mic = MicCapture(name: name)
                try mic.start()
                captureLock.lock(); micCaptures[name] = mic; captureLock.unlock()
                reply(id)
            } catch {
                fail(id, "マイクの音を取り込めません: " + message(of: error))
            }
            return
        }
        let pid = pid_t(request["pid"] as? Int ?? 0)
        let exclude = request["exclude"] as? Bool ?? false
        captureLock.lock(); let generation = captureGeneration[name, default: 0]; captureLock.unlock()
        Task {
            do {
                let capture = AppAudioCapture(name: name)
                try await capture.start(pid: pid, exclude: exclude)
                let cancelled = !registerAppCapture(capture, name: name, generation: generation)
                if cancelled {
                    // 始めている間に止められた (アプリが待ちきれずにあきらめた) ので、取り込みを残さない
                    capture.stop()
                    fail(id, "取り込みを始める前に止められました")
                    return
                }
                reply(id)
            } catch {
                fail(id, "アプリの音を取り込めません: " + message(of: error)
                     + (CGPreflightScreenCaptureAccess() ? "" : " (システム設定 → プライバシーとセキュリティ → 画面収録とシステムオーディオ録音 で GetText を許可してください)"))
            }
        }

    case "audio_stop":
        stopAudio(request["stream"] as? String ?? "win")
        reply(id)

    case "window_bounds":
        if let b = windowBounds(request["window"] as? Int ?? 0) {
            reply(id, ["x": b.minX, "y": b.minY, "w": b.width, "h": b.height])
        } else {
            fail(id, "窓が見つかりません")
        }

    case "capture_ocr", "capture":
        // window: 取り込み枠の窓の番号、inset: 枠の内側までの幅 (ポイント、左・上・右・下)。または rect: [x, y, w, h]
        var rect: CGRect
        if let number = request["window"] as? Int, let b = windowBounds(number) {
            let inset = (request["inset"] as? [Double]) ?? [0, 0, 0, 0]
            rect = CGRect(x: b.minX + inset[0], y: b.minY + inset[1],
                          width: b.width - inset[0] - inset[2], height: b.height - inset[1] - inset[3])
        } else if let r = request["rect"] as? [Double], r.count == 4 {
            rect = CGRect(x: r[0], y: r[1], width: r[2], height: r[3])
        } else {
            fail(id, "取り込む範囲がありません")
            return
        }
        let scale = CGFloat(request["scale"] as? Double ?? 2)
        let force = request["force"] as? Bool ?? false
        let languages = request["languages"] as? [String] ?? ["ja-JP", "en-US"]
        let reading = cmd == "capture_ocr"
        if reading {
            ocrLock.lock()
            let busy = ocrRunning
            if !busy { ocrRunning = true }
            ocrLock.unlock()
            if busy {
                // 前の読み取り (アプリの方は待ちきれずにあきらめたもの) がまだ終わっていない
                reply(id, ["unchanged": true])
                return
            }
        }
        Task {
            defer {
                if reading { finishOcr() }
            }
            do {
                let image = try await captureRect(rect, scale: scale)
                if cmd == "capture" {
                    reply(id, ["w": image.width, "h": image.height, "data": bgra(image).base64EncodedString()])
                    return
                }
                let print = fingerprint(image)
                if !force && !changed(print, lastFingerprint) {
                    reply(id, ["unchanged": true])
                    return
                }
                lastFingerprint = print
                let lines = try recognize(image, languages: languages)
                reply(id, ["w": image.width, "h": image.height, "lines": lines])
            } catch {
                fail(id, message(of: error))
            }
        }

    case "ocr_image":
        // data: BGRA の base64、w / h: 大きさ
        guard let b64 = request["data"] as? String, let data = Data(base64Encoded: b64),
              let w = request["w"] as? Int, let h = request["h"] as? Int,
              let provider = CGDataProvider(data: data as CFData),
              let image = CGImage(width: w, height: h, bitsPerComponent: 8, bitsPerPixel: 32, bytesPerRow: w * 4,
                                  space: CGColorSpaceCreateDeviceRGB(),
                                  bitmapInfo: CGBitmapInfo(rawValue: CGImageAlphaInfo.premultipliedFirst.rawValue | CGBitmapInfo.byteOrder32Little.rawValue),
                                  provider: provider, decode: nil, shouldInterpolate: false, intent: .defaultIntent) else {
            fail(id, "画像を読めません")
            return
        }
        do {
            reply(id, ["lines": try recognize(image, languages: request["languages"] as? [String] ?? ["ja-JP", "en-US"])])
        } catch {
            fail(id, message(of: error))
        }

    case "hotkeys":
        let list = request["keys"] as? [[String: Any]] ?? []
        DispatchQueue.main.async {
            unregisterHotKeys()
            var failed: [Int] = []
            for k in list {
                let hid = UInt32(k["id"] as? Int ?? 0)
                if !registerHotKey(id: hid, key: UInt32(k["key"] as? Int ?? 0), mods: k["mods"] as? [String] ?? []) {
                    failed.append(Int(hid))
                }
            }
            reply(id, ["failed": failed])
        }

    case "secret_get":
        DispatchQueue.global().async {
            let value = secretGet(request["name"] as? String ?? "", service: request["service"] as? String ?? "GetText")
            reply(id, ["value": value ?? NSNull()])
        }

    case "secret_set":
        DispatchQueue.global().async {
            do {
                try secretSet(request["name"] as? String ?? "", value: request["value"] as? String ?? "",
                              service: request["service"] as? String ?? "GetText")
                reply(id)
            } catch {
                fail(id, message(of: error))
            }
        }

    case "secret_delete":
        DispatchQueue.global().async {
            secretDelete(request["name"] as? String ?? "", service: request["service"] as? String ?? "GetText")
            reply(id)
        }

    case "play":
        let path = request["path"] as? String ?? ""
        let from = request["from"] as? Double ?? 0
        let until = request["until"] as? Double ?? from + 10
        DispatchQueue.main.async {
            do {
                try play(path: path, from: from, until: until)
                reply(id)
            } catch {
                fail(id, message(of: error))
            }
        }

    case "play_stop":
        DispatchQueue.main.async {
            stopPlayback()
            reply(id)
        }

    case "play_position":
        DispatchQueue.main.async {
            reply(id, ["seconds": player.map { CMTimeGetSeconds($0.currentTime()) } ?? 0])
        }

    case "selftest":
        // CI 用: 文字認識・キーチェーン・ショートカットの登録を確かめる (画面や音の取り込みは許可が要るので確かめない)
        var results: [String: Any] = [:]
        if let image = renderText(["議事録のテストです", "Hello GetText 2026"]),
           let lines = try? recognize(image, languages: ["ja-JP", "en-US"]) {
            results["ocr"] = lines.map { $0["text"] as? String ?? "" }
        }
        let service = "GetText-selftest"
        do {
            try secretSet("probe", value: "秘密-123", service: service)
            results["secret"] = secretGet("probe", service: service) ?? ""
            secretDelete("probe", service: service)
            results["secret_deleted"] = secretGet("probe", service: service) == nil
        } catch {
            results["secret_error"] = message(of: error)
        }
        if #available(macOS 14.4, *) { results["playing_supported"] = true } else { results["playing_supported"] = false }
        results["windows"] = listWindows().count
        DispatchQueue.main.async {
            results["hotkey"] = registerHotKey(id: 99, key: UInt32(kVK_ANSI_K), mods: ["ctrl", "alt", "shift"])
            unregisterHotKeys()
            reply(id, results)
        }

    default:
        fail(id, "知らない要求です: \(cmd)")
    }
}

// MARK: - 起動

// 画面には何も出さない (Dock にも出さない) が、ショートカットや再生のためにイベントループは回す
let application = NSApplication.shared
application.setActivationPolicy(.prohibited)

let reader = Thread {
    while let line = readLine(strippingNewline: true) {
        guard !line.isEmpty, let data = line.data(using: .utf8),
              let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else { continue }
        handle(object)
    }
    // GetText が終わった
    for name in Array(appCaptures.keys) + Array(micCaptures.keys) { stopAudio(name) }
    // 録画中・書き終えている途中なら、書き終えるまで待つ (最大 10 秒)
    let done = DispatchSemaphore(value: 0)
    if let r = withRecordLock({ () -> ScreenRecording? in let r = recording; recording = nil; return r }) {
        finishRecording(r) { _, _ in }
    }
    let deadline = Date().addingTimeInterval(10)
    while withRecordLock({ !finishingRecordings.isEmpty }) && Date() < deadline {
        _ = done.wait(timeout: .now() + 0.1)
    }
    exit(0)
}
// GetText が落ちて受け取る側が無くなっても、書き込み (SIGPIPE) で止まらず、録画を書き終えられるようにする
signal(SIGPIPE, SIG_IGN)
reader.start()
send(["event": "ready"])
application.run()
