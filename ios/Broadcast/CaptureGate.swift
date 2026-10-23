import Foundation

struct FrameLease {
    let id: UInt64
    let generation: UInt64
}

// Shared by the ReplayKit callback, encoder queue and Bluetooth delegate queue.
// A cancelled frame cannot resume after pause, or release a newer frame's slot.
final class CaptureGate {
    private let lock = NSLock()
    private var ready = false
    private var paused = false
    private var stopped = false
    private var nextID: UInt64 = 0
    private var generation: UInt64 = 0
    private var busyID: UInt64?
    private var nextFrameAt: TimeInterval = 0

    func markReady() {
        lock.lock(); defer { lock.unlock() }
        if !stopped { ready = true }
    }
    func reserve(now: TimeInterval = ProcessInfo.processInfo.systemUptime) -> FrameLease? {
        lock.lock(); defer { lock.unlock() }
        guard ready, !paused, !stopped, busyID == nil, now >= nextFrameAt else { return nil }
        nextID &+= 1
        busyID = nextID
        return FrameLease(id: nextID, generation: generation)
    }
    func canSend(_ lease: FrameLease) -> Bool {
        lock.lock(); defer { lock.unlock() }
        return ready && !paused && !stopped && busyID == lease.id && generation == lease.generation
    }
    func release(_ lease: FrameLease, now: TimeInterval = ProcessInfo.processInfo.systemUptime) {
        lock.lock(); defer { lock.unlock() }
        guard busyID == lease.id else { return }
        busyID = nil
        nextFrameAt = now + 0.5
    }
    func setPaused(_ value: Bool) {
        lock.lock(); defer { lock.unlock() }
        if value && !paused { generation &+= 1 }
        paused = value
    }
    func stop() {
        lock.lock(); defer { lock.unlock() }
        stopped = true; ready = false; busyID = nil; generation &+= 1
    }
}
