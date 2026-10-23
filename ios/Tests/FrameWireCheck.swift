import Foundation

@main
struct FrameWireCheck {
    static func main() {
        precondition(FrameWire.crc32(Data("123456789".utf8)) == 0xcbf43926, "CRC32 golden vector")
        let payload = Data([1, 2, 3])
        let start = FrameWire.start(id: 42, jpeg: payload).map { String(format: "%02x", $0) }.joined()
        precondition(start == "4c5001012a00000000000000030000001d80bc55", "Windows-compatible frame header")
        let end = FrameWire.packet(kind: 3, id: 42, offset: 3).map { String(format: "%02x", $0) }.joined()
        precondition(end == "4c5001032a00000003000000", "Windows-compatible end packet")
        let gate = CaptureGate()
        precondition(gate.reserve(now: 0) == nil, "Do not encode before the encrypted connection is verified")
        gate.markReady()
        let first = gate.reserve(now: 0)!
        precondition(gate.canSend(first) && gate.reserve(now: 0) == nil, "Only one in-flight frame")
        gate.setPaused(true)
        gate.setPaused(false)
        precondition(!gate.canSend(first), "A pre-pause frame cannot resume after unpausing")
        precondition(gate.reserve(now: 1) == nil, "Wait for the old write to drain")
        gate.release(first, now: 1)
        precondition(gate.reserve(now: 1.1) == nil, "Respect frame pacing")
        let second = gate.reserve(now: 1.6)!
        gate.release(first, now: 100)
        precondition(gate.canSend(second), "An old completion cannot release the newer frame")
        gate.stop()
        precondition(!gate.canSend(second) && gate.reserve(now: 2) == nil, "Stop immediately prevents new frames")
        print("PASS: Swift protocol vectors and frame lifecycle; no Bluetooth or iPhone exercised")
    }
}
