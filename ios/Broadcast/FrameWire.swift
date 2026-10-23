import Foundation

enum FrameWire {
    static let service = "72D00001-7B56-4A5E-9F12-8C454E50424C"
    static let frame = "72D00002-7B56-4A5E-9F12-8C454E50424C"
    static let info = "72D00003-7B56-4A5E-9F12-8C454E50424C"
    static let headerSize = 12
    static let maxFrameSize = 256 * 1024

    static func append32(_ value: UInt32, to data: inout Data) {
        for shift in stride(from: 0, to: 32, by: 8) { data.append(UInt8(truncatingIfNeeded: value >> shift)) }
    }
    static func crc32(_ data: Data) -> UInt32 {
        var crc = UInt32.max
        for byte in data {
            crc ^= UInt32(byte)
            for _ in 0..<8 { crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0 : 0xedb88320) }
        }
        return ~crc
    }
    static func packet(kind: UInt8, id: UInt32, offset: UInt32, body: Data = Data()) -> Data {
        var data = Data([0x4c, 0x50, 1, kind])
        append32(id, to: &data)
        append32(offset, to: &data)
        data.append(body)
        return data
    }
    static func start(id: UInt32, jpeg: Data) -> Data {
        var body = Data()
        append32(UInt32(jpeg.count), to: &body)
        append32(crc32(jpeg), to: &body)
        return packet(kind: 1, id: id, offset: 0, body: body)
    }
}
