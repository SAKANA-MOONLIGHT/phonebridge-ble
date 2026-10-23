import Foundation
import CoreBluetooth

// The broadcast extension owns its BLE connection. The foreground app is not
// required to remain running, and no App Group capability is needed.
final class BluetoothTransport: NSObject, CBCentralManagerDelegate, CBPeripheralDelegate {
    let queue = DispatchQueue(label: "local.phonebridge.broadcast.bluetooth")
    var onFailure: ((Error) -> Void)?
    private var central: CBCentralManager!
    private var peripheral: CBPeripheral?
    private var characteristic: CBCharacteristic?
    private var candidates: [UUID: CBPeripheral] = [:]
    private var scanDeadline: DispatchWorkItem?
    private var watchdog: DispatchSourceTimer?
    private var isStopped = false
    private var connectedAt = Date()
    private var lastWriteAt: Date?
    private var jpeg: Data?
    private var frameID: UInt32 = 0
    private var offset = 0
    private var stage = 0
    private let captureGate = CaptureGate()
    private var currentLease: FrameLease?
    private var started = false

    func start() {
        queue.async {
            guard !self.started, !self.isStopped else { return }
            self.started = true
            self.central = CBCentralManager(delegate: self, queue: self.queue)
            let timer = DispatchSource.makeTimerSource(queue: self.queue)
            timer.schedule(deadline: .now() + 1, repeating: 1)
            timer.setEventHandler { [weak self] in self?.checkTimeout() }
            timer.resume()
            self.watchdog = timer
        }
    }
    func stop() {
        captureGate.stop()
        queue.async {
            self.isStopped = true
            self.scanDeadline?.cancel()
            self.central?.stopScan()
            if let peripheral = self.peripheral { peripheral.delegate = nil; self.central?.cancelPeripheralConnection(peripheral) }
            self.central?.delegate = nil
            self.watchdog?.cancel()
            self.watchdog = nil
            self.characteristic = nil
            self.jpeg = nil
            self.currentLease = nil
            self.lastWriteAt = nil
        }
    }
    func setPaused(_ value: Bool) { captureGate.setPaused(value) }
    func reserveFrame() -> FrameLease? { captureGate.reserve() }
    func releaseFrame(_ lease: FrameLease) { captureGate.release(lease) }
    func offer(_ data: Data, lease: FrameLease) {
        queue.async {
            guard !self.isStopped, self.captureGate.canSend(lease), self.characteristic != nil, data.count <= FrameWire.maxFrameSize else { self.releaseFrame(lease); return }
            self.frameID &+= 1
            self.currentLease = lease
            self.jpeg = data; self.offset = 0; self.stage = 0
            self.writeNext()
        }
    }
    private func fail(_ message: String) {
        guard !isStopped else { return }
        isStopped = true
        onFailure?(NSError(domain: "PhoneBridge", code: 1, userInfo: [NSLocalizedDescriptionKey: message]))
        stop()
    }
    private func checkTimeout() {
        guard !isStopped else { return }
        if let sent = lastWriteAt, Date().timeIntervalSince(sent) > 20 { fail("电脑没有确认蓝牙数据。请检查电脑是否已选择这部手机，然后重新开始广播。") }
        if !verified && Date().timeIntervalSince(connectedAt) > 30 { fail("未能连接电脑。请先在电脑开启接收，并完成辅助触控蓝牙配对。") }
    }
    func centralManagerDidUpdateState(_ central: CBCentralManager) {
        guard !isStopped else { return }
        switch central.state {
        case .poweredOn:
            guard peripheral == nil, scanDeadline == nil else { return }
            candidates.removeAll()
            for item in central.retrieveConnectedPeripherals(withServices: [CBUUID(string: FrameWire.service)]) { candidates[item.identifier] = item }
            central.scanForPeripherals(withServices: [CBUUID(string: FrameWire.service)])
            let deadline = DispatchWorkItem { [weak self] in self?.chooseComputer() }
            scanDeadline = deadline; queue.asyncAfter(deadline: .now() + 3, execute: deadline)
        case .unauthorized: fail("请先在手机设置中允许“手机连接”使用蓝牙。")
        case .poweredOff: fail("手机蓝牙未开启。")
        case .unsupported: fail("手机不支持所需的蓝牙功能。")
        case .resetting, .unknown: break
        @unknown default: fail("蓝牙状态不可用。")
        }
    }
    func centralManager(_ central: CBCentralManager, didDiscover peripheral: CBPeripheral, advertisementData: [String: Any], rssi RSSI: NSNumber) {
        guard !isStopped else { return }
        candidates[peripheral.identifier] = peripheral
    }
    private func chooseComputer() {
        guard !isStopped else { return }
        central.stopScan()
        guard candidates.count == 1, let candidate = candidates.values.first else {
            fail(candidates.isEmpty ? "没有发现正在接收的电脑。请在电脑打开 PhoneBridge 并开启连接。" : "发现多台电脑。请关闭其他电脑的 PhoneBridge 接收后再试。")
            return
        }
        peripheral = candidate; candidate.delegate = self; central.connect(candidate)
    }
    func centralManager(_ central: CBCentralManager, didConnect peripheral: CBPeripheral) {
        guard !isStopped else { return }
        peripheral.discoverServices([CBUUID(string: FrameWire.service)])
    }
    func centralManager(_ central: CBCentralManager, didFailToConnect peripheral: CBPeripheral, error: Error?) { fail("蓝牙连接失败：" + (error?.localizedDescription ?? "请重新配对后再试。")) }
    func centralManager(_ central: CBCentralManager, didDisconnectPeripheral peripheral: CBPeripheral, error: Error?) {
        characteristic = nil
        if !isStopped { fail("与电脑的蓝牙连接已断开。请检查连接后重新开始广播。") }
    }
    func peripheral(_ peripheral: CBPeripheral, didDiscoverServices error: Error?) {
        guard !isStopped else { return }
        guard error == nil, let service = peripheral.services?.first(where: { $0.uuid == CBUUID(string: FrameWire.service) }) else { fail("电脑没有提供画面接收服务。"); return }
        peripheral.discoverCharacteristics([CBUUID(string: FrameWire.frame), CBUUID(string: FrameWire.info)], for: service)
    }
    func peripheral(_ peripheral: CBPeripheral, didDiscoverCharacteristicsFor service: CBService, error: Error?) {
        guard !isStopped else { return }
        guard error == nil,
              let frame = service.characteristics?.first(where: { $0.uuid == CBUUID(string: FrameWire.frame) && $0.properties.contains(.write) }),
              let info = service.characteristics?.first(where: { $0.uuid == CBUUID(string: FrameWire.info) })
        else { fail("电脑的画面接收接口不兼容。"); return }
        characteristic = frame
        peripheral.readValue(for: info)
    }
    private var verified = false
    func peripheral(_ peripheral: CBPeripheral, didUpdateValueFor characteristic: CBCharacteristic, error: Error?) {
        guard !isStopped else { return }
        guard error == nil, let value = characteristic.value, let name = String(data: value, encoding: .utf8), name.hasPrefix("PhoneBridge/1 ") else { fail("无法确认电脑身份。请完成蓝牙配对，并检查程序版本。"); return }
        verified = true
        captureGate.markReady()
    }
    private func writeNext() {
        guard let lease = currentLease else { return }
        guard !isStopped, captureGate.canSend(lease), verified, let peripheral = peripheral, let characteristic = characteristic, let jpeg = jpeg else {
            self.jpeg = nil; currentLease = nil; releaseFrame(lease); return
        }
        let packet: Data
        switch stage {
        case 0:
            packet = FrameWire.start(id: frameID, jpeg: jpeg)
            stage = 1
        case 1:
            if offset < jpeg.count {
                // withResponse may report 512 bytes even when that would require
                // ATT prepared writes. Stay within the negotiated short-write MTU.
                let maximum = min(peripheral.maximumWriteValueLength(for: .withResponse), peripheral.maximumWriteValueLength(for: .withoutResponse))
                let size = min(maximum - FrameWire.headerSize, 500)
                guard size > 0 else { fail("蓝牙数据包大小不足。"); return }
                let end = min(offset + size, jpeg.count)
                packet = FrameWire.packet(kind: 2, id: frameID, offset: UInt32(offset), body: jpeg.subdata(in: offset..<end))
                offset = end
            } else {
                packet = FrameWire.packet(kind: 3, id: frameID, offset: UInt32(offset))
                stage = 2
            }
        default:
            self.jpeg = nil; currentLease = nil; releaseFrame(lease); return
        }
        lastWriteAt = Date()
        peripheral.writeValue(packet, for: characteristic, type: .withResponse)
    }
    func peripheral(_ peripheral: CBPeripheral, didWriteValueFor characteristic: CBCharacteristic, error: Error?) {
        guard !isStopped else { return }
        lastWriteAt = nil
        if let error = error { fail("电脑拒绝画面数据。请在电脑中选择这部手机。详细信息：" + error.localizedDescription); return }
        writeNext()
    }
}
