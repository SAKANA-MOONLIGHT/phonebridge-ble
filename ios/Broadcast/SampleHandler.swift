import ReplayKit
import CoreImage
import ImageIO
import UniformTypeIdentifiers

final class SampleHandler: RPBroadcastSampleHandler {
    private let transport = BluetoothTransport()
    private let encodeQueue = DispatchQueue(label: "local.phonebridge.broadcast.encode", qos: .userInitiated)
    private let context = CIContext(options: [.cacheIntermediates: false])

    override func broadcastStarted(withSetupInfo setupInfo: [String: NSObject]?) {
        transport.onFailure = { [weak self] error in self?.finishBroadcastWithError(error) }
        transport.start()
    }
    override func broadcastPaused() { transport.setPaused(true) }
    override func broadcastResumed() { transport.setPaused(false) }
    override func broadcastFinished() { transport.stop() }

    override func processSampleBuffer(_ sampleBuffer: CMSampleBuffer, with sampleBufferType: RPSampleBufferType) {
        guard sampleBufferType == .video else { return }
        guard let lease = transport.reserveFrame() else { return }
        encodeQueue.async { [weak self] in
            guard let self = self else { return }
            autoreleasepool {
                guard let pixelBuffer = CMSampleBufferGetImageBuffer(sampleBuffer) else { self.transport.releaseFrame(lease); return }
                let orientation = (CMGetAttachment(sampleBuffer, key: RPVideoSampleOrientationKey as CFString, attachmentModeOut: nil) as? NSNumber)?.uint32Value ?? 1
                let image = CIImage(cvPixelBuffer: pixelBuffer).oriented(CGImagePropertyOrientation(rawValue: orientation) ?? .up)
                let scale = min(1, 640 / max(image.extent.width, image.extent.height))
                let thumbnail = image.transformed(by: CGAffineTransform(scaleX: scale, y: scale))
                guard let cgImage = self.context.createCGImage(thumbnail, from: thumbnail.extent) else { self.transport.releaseFrame(lease); return }
                // One JPEG and one retained sample at a time. No frame backlog.
                let output = NSMutableData()
                guard let destination = CGImageDestinationCreateWithData(output, UTType.jpeg.identifier as CFString, 1, nil) else { self.transport.releaseFrame(lease); return }
                CGImageDestinationAddImage(destination, cgImage, [kCGImageDestinationLossyCompressionQuality: 0.35] as CFDictionary)
                guard CGImageDestinationFinalize(destination), output.length <= FrameWire.maxFrameSize else { self.transport.releaseFrame(lease); return }
                self.transport.offer(output as Data, lease: lease)
            }
        }
    }
}
