import SwiftUI
import ReplayKit
import CoreBluetooth

@main
struct PhoneBridgeApp: App {
    var body: some Scene { WindowGroup { ConnectionView() } }
}

final class BluetoothPermission: NSObject, ObservableObject, CBCentralManagerDelegate {
    @Published var message = "尚未检查蓝牙权限"
    private var manager: CBCentralManager?
    func request() { manager = CBCentralManager(delegate: self, queue: .main) }
    func centralManagerDidUpdateState(_ central: CBCentralManager) {
        switch central.state {
        case .poweredOn: message = "蓝牙已开启，可以开始屏幕广播"
        case .unauthorized: message = "请在设置中允许本 App 使用蓝牙"
        case .poweredOff: message = "请先开启手机蓝牙"
        case .unsupported: message = "此设备不支持所需的蓝牙功能"
        default: message = "正在检查蓝牙状态"
        }
    }
}

struct ConnectionView: View {
    @StateObject private var bluetooth = BluetoothPermission()
    var body: some View {
        NavigationStack {
            ScrollView {
                VStack(alignment: .leading, spacing: 24) {
                    Text("把手机画面传到电脑")
                        .font(.largeTitle.bold())
                    Text("实验版通过蓝牙发送低帧率画面。实际清晰度和延迟需要用你的设备验证。")
                        .foregroundStyle(.secondary)
                    VStack(alignment: .leading, spacing: 16) {
                        Label("电脑打开 PhoneBridge，开启蓝牙连接。", systemImage: "laptopcomputer")
                        Label("在辅助触控的蓝牙设备中配对此电脑，并在电脑端选择手机。", systemImage: "cursorarrow")
                        Label("点击下面的广播按钮，再点“开始直播”。", systemImage: "record.circle")
                    }
                    Button("检查并授权蓝牙") { bluetooth.request() }
                        .buttonStyle(.borderedProminent)
                    Text(bluetooth.message).font(.callout).foregroundStyle(.secondary)
                    HStack(spacing: 16) {
                        BroadcastPicker().frame(width: 60, height: 60)
                        Text("开始屏幕广播\n使用系统弹窗确认后，才会发送画面。")
                            .font(.callout)
                    }
                    Divider()
                    Text("一次只开启一台电脑的接收功能。广播开始后可以切换到其他 App；点击系统屏幕录制指示可停止。此版本不传输音频。")
                        .font(.callout).foregroundStyle(.secondary)
                }
                .padding(24)
            }
            .navigationTitle("手机连接")
            .navigationBarTitleDisplayMode(.inline)
        }
    }
}

struct BroadcastPicker: UIViewRepresentable {
    func makeUIView(context: Context) -> RPSystemBroadcastPickerView {
        let picker = RPSystemBroadcastPickerView(frame: CGRect(x: 0, y: 0, width: 60, height: 60))
        if let folder = Bundle.main.builtInPlugInsURL,
           let plugins = try? FileManager.default.contentsOfDirectory(at: folder, includingPropertiesForKeys: nil),
           let plugin = plugins.first(where: { $0.pathExtension == "appex" }),
           let identifier = Bundle(url: plugin)?.bundleIdentifier {
            picker.preferredExtension = identifier
        }
        picker.showsMicrophoneButton = false
        return picker
    }
    func updateUIView(_ uiView: RPSystemBroadcastPickerView, context: Context) {}
}
