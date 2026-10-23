using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

namespace PhoneBridge {
    public sealed class Peer {
        public string Id;
        public string Name;
        public override string ToString() { return Name; }
    }

    public sealed class BluetoothHost : IDisposable {
        private GattServiceProvider hidProvider, screenProvider;
        private GattLocalCharacteristic keyboard, mouse;
        private readonly Dictionary<string,FrameAssembler> assemblers = new Dictionary<string,FrameAssembler>();
        private readonly object frameGate = new object();
        private readonly SemaphoreSlim sendGate = new SemaphoreSlim(1,1);
        private readonly Stopwatch inputClock=Stopwatch.StartNew();
        private long lastNotification=-1000,lastSlowLog;
        private BluetoothLEDevice targetDevice;
        private int inputInterval=30;
        private bool disposed;
        private string hidError="None",screenError="None";
        public bool Started { get; private set; }
        public string TargetId { get; set; }
        public event Action Changed;
        public event Action<byte[]> FrameReceived;
        public event Action InputLost;
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(8);

        private static async Task<T> Bounded<T>(Task<T> task) {
            if(await Task.WhenAny(task, Task.Delay(Timeout)) != task) throw new TimeoutException("Bluetooth operation timed out");
            return await task;
        }

        public static async Task<string> DiagnoseAsync() {
            BluetoothAdapter adapter=await Bounded(BluetoothAdapter.GetDefaultAsync().AsTask());
            if(adapter==null) return "没有找到蓝牙适配器。请确认电脑支持蓝牙且已启用。";
            return "低功耗蓝牙："+(adapter.IsLowEnergySupported ? "支持" : "不支持") + "\r\n蓝牙外设角色："+(adapter.IsPeripheralRoleSupported ? "支持" : "不支持") + "\r\n蓝牙中心角色："+(adapter.IsCentralRoleSupported ? "支持" : "不支持") + "\r\n这些是硬件条件，仍需实际连接手机验证。";
        }

        public async Task StartAsync() {
            if(disposed) throw new ObjectDisposedException("BluetoothHost");
            if(Started) return;
            BluetoothAdapter adapter=await Bounded(BluetoothAdapter.GetDefaultAsync().AsTask());
            if(adapter==null || !adapter.IsLowEnergySupported || !adapter.IsPeripheralRoleSupported) throw new InvalidOperationException("这台电脑不支持所需的蓝牙外设功能。请检查蓝牙适配器或驱动。 ");
            try {
                hidProvider=await Provider(Hid.Uuid(0x1812));
                await Constant(hidProvider,Hid.Uuid(0x2a4a),new byte[] {0x11,0x01,0x00,0x02});
                await Constant(hidProvider,Hid.Uuid(0x2a4b),Hid.ReportMap);
                keyboard=await Report(1,8);
                mouse=await Report(2,4);
                GattLocalCharacteristic output=await Characteristic(hidProvider,Hid.Uuid(0x2a4d),GattCharacteristicProperties.Read | GattCharacteristicProperties.Write | GattCharacteristicProperties.WriteWithoutResponse);
                await Reference(output,1,2);
                Readable(output,delegate { return new byte[1]; });
                Writeable(output,delegate(byte[] value,string peer) { if(value.Length!=1) throw new InvalidDataException("Invalid keyboard LED report"); });
                GattLocalCharacteristic control=await Characteristic(hidProvider,Hid.Uuid(0x2a4c),GattCharacteristicProperties.WriteWithoutResponse);
                Writeable(control,delegate(byte[] value,string peer) { if(value.Length!=1) throw new InvalidDataException("Invalid HID control"); });
                GattLocalCharacteristic mode=await Characteristic(hidProvider,Hid.Uuid(0x2a4e),GattCharacteristicProperties.Read | GattCharacteristicProperties.WriteWithoutResponse);
                Readable(mode,delegate { return new byte[] {1}; });
                Writeable(mode,delegate(byte[] value,string peer) { if(value.Length!=1 || value[0]!=1) throw new InvalidDataException("Only report protocol is supported"); });

                screenProvider=await Provider(Wire.Service);
                GattLocalCharacteristic info=await Characteristic(screenProvider,Wire.Info,GattCharacteristicProperties.Read);
                Readable(info,delegate { return Encoding.UTF8.GetBytes("PhoneBridge/1 " + Environment.MachineName); });
                GattLocalCharacteristic frame=await Characteristic(screenProvider,Wire.Frame,GattCharacteristicProperties.Write);
                Writeable(frame,AcceptFrame);

                hidProvider.AdvertisementStatusChanged += AdvertisementChanged;
                screenProvider.AdvertisementStatusChanged += AdvertisementChanged;
                var parameters=new GattServiceProviderAdvertisingParameters { IsDiscoverable=true, IsConnectable=true };
                hidProvider.StartAdvertising(parameters);
                screenProvider.StartAdvertising(parameters);
                Started=true;
                Log.Write("已请求发布键盘、鼠标和画面接收服务。请在 iPhone 上确认配对。 ");
            } catch { Stop(); throw; }
        }

        private static async Task<GattServiceProvider> Provider(Guid uuid) {
            var result=await Bounded(GattServiceProvider.CreateAsync(uuid).AsTask());
            if(result.Error!=BluetoothError.Success) throw new InvalidOperationException("蓝牙服务创建失败："+result.Error+"。系统策略或驱动可能不支持此服务。");
            return result.ServiceProvider;
        }
        private static async Task<GattLocalCharacteristic> Characteristic(GattServiceProvider provider,Guid uuid,GattCharacteristicProperties properties) {
            var p=new GattLocalCharacteristicParameters { CharacteristicProperties=properties };
            if((properties & GattCharacteristicProperties.Read)!=0) p.ReadProtectionLevel=GattProtectionLevel.EncryptionRequired;
            if((properties & (GattCharacteristicProperties.Write | GattCharacteristicProperties.WriteWithoutResponse))!=0) p.WriteProtectionLevel=GattProtectionLevel.EncryptionRequired;
            var result=await Bounded(provider.Service.CreateCharacteristicAsync(uuid,p).AsTask());
            if(result.Error!=BluetoothError.Success) throw new InvalidOperationException("蓝牙特征创建失败："+result.Error);
            return result.Characteristic;
        }
        private static async Task Constant(GattServiceProvider provider,Guid uuid,byte[] value) {
            var result=await Bounded(provider.Service.CreateCharacteristicAsync(uuid,new GattLocalCharacteristicParameters { CharacteristicProperties=GattCharacteristicProperties.Read,ReadProtectionLevel=GattProtectionLevel.EncryptionRequired,StaticValue=value.AsBuffer() }).AsTask());
            if(result.Error!=BluetoothError.Success) throw new InvalidOperationException("蓝牙只读特征创建失败："+result.Error);
        }
        private async Task<GattLocalCharacteristic> Report(byte id,int length) {
            var report=await Characteristic(hidProvider,Hid.Uuid(0x2a4d),GattCharacteristicProperties.Read | GattCharacteristicProperties.Notify);
            await Reference(report,id,1);
            Readable(report,delegate { return new byte[length]; });
            report.SubscribedClientsChanged += delegate { Log.Write("手机输入订阅发生变化。 "); var handler=Changed; if(handler!=null) handler(); };
            return report;
        }
        private static async Task Reference(GattLocalCharacteristic report,byte id,byte kind) {
            var result=await Bounded(report.CreateDescriptorAsync(Hid.Uuid(0x2908),new GattLocalDescriptorParameters { ReadProtectionLevel=GattProtectionLevel.EncryptionRequired,StaticValue=new byte[] {id,kind}.AsBuffer() }).AsTask());
            if(result.Error!=BluetoothError.Success) throw new InvalidOperationException("蓝牙报告描述符创建失败："+result.Error);
        }
        private static void Readable(GattLocalCharacteristic characteristic,Func<byte[]> get) {
            characteristic.ReadRequested += async delegate(GattLocalCharacteristic sender,GattReadRequestedEventArgs args) {
                var deferral=args.GetDeferral();
                try {
                    var request=await args.GetRequestAsync(); if(request==null) return;
                    byte[] bytes=get();
                    if(request.Offset>bytes.Length) request.RespondWithProtocolError(0x07);
                    else request.RespondWithValue(bytes.AsBuffer((int)request.Offset,bytes.Length-(int)request.Offset));
                } catch(Exception ex) { Log.Write("蓝牙读取失败："+ex.Message); }
                finally { deferral.Complete(); }
            };
        }
        private static void Writeable(GattLocalCharacteristic characteristic,Action<byte[],string> put) {
            characteristic.WriteRequested += async delegate(GattLocalCharacteristic sender,GattWriteRequestedEventArgs args) {
                var deferral=args.GetDeferral();
                try {
                    var request=await args.GetRequestAsync(); if(request==null) return;
                    try {
                        if(request.Offset!=0) throw new InvalidDataException("Long writes are not supported");
                        byte[] bytes=new byte[request.Value.Length]; using(var reader=DataReader.FromBuffer(request.Value)) reader.ReadBytes(bytes);
                        put(bytes,args.Session.DeviceId.Id);
                        if(request.Option==GattWriteOption.WriteWithResponse) request.Respond();
                    } catch(Exception ex) { if(request.Option==GattWriteOption.WriteWithResponse) request.RespondWithProtocolError(0x80); Log.Write("未接收蓝牙数据："+ex.Message); }
                } catch(Exception ex) { Log.Write("蓝牙写入失败："+ex.Message); }
                finally { deferral.Complete(); }
            };
        }
        private void AdvertisementChanged(GattServiceProvider sender,GattServiceProviderAdvertisementStatusChangedEventArgs args) {
            if(sender==hidProvider) hidError=args.Error.ToString(); else screenError=args.Error.ToString();
            Log.Write("蓝牙服务广播："+args.Status+"，"+args.Error);
            var handler=Changed; if(handler!=null) handler();
        }
        private void AcceptFrame(byte[] packet,string id) {
            if(string.IsNullOrEmpty(TargetId) || !string.Equals(TargetId,id,StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("请先在电脑中选择发送画面的手机。 ");
            byte[] complete;
            lock(frameGate) {
                FrameAssembler assembler;
                if(!assemblers.TryGetValue(id,out assembler)) { assemblers.Clear(); assembler=new FrameAssembler(); assemblers.Add(id,assembler); }
                complete=assembler.Accept(packet);
            }
            if(complete!=null) { var handler=FrameReceived; if(handler!=null) handler(complete); }
        }
        public List<Peer> Peers() {
            var peers=new Dictionary<string,Peer>();
            foreach(var report in new[] {keyboard,mouse}) {
                if(report==null) continue;
                foreach(var client in report.SubscribedClients) {
                    if(client.Session.SessionStatus!=GattSessionStatus.Active) continue;
                    string id=client.Session.DeviceId.Id;
                    peers[id]=new Peer {Id=id,Name="已连接设备 · "+id.Substring(Math.Max(0,id.Length-12))};
                }
            }
            return peers.Values.OrderBy(p=>p.Id).ToList();
        }
        public bool TargetConnected {
            get { return !string.IsNullOrEmpty(TargetId) && Peers().Any(p=>p.Id==TargetId); }
        }
        public bool Ready { get { return hidProvider!=null && screenProvider!=null && Advertising(hidProvider) && Advertising(screenProvider); } }
        private static bool Advertising(GattServiceProvider provider) { string value=provider.AdvertisementStatus.ToString(); return value=="Started" || value=="StartedWithoutAllAdvertisementData"; }
        public string PublicationSummary() { return "HID="+(hidProvider==null ? "Stopped" : hidProvider.AdvertisementStatus.ToString())+" ("+hidError+"); Screen="+(screenProvider==null ? "Stopped" : screenProvider.AdvertisementStatus.ToString())+" ("+screenError+")"; }
        public async Task ReadTargetParametersAsync() {
            string target=TargetId;
            try {
                if(targetDevice!=null) { targetDevice.Dispose();targetDevice=null; }
                if(string.IsNullOrEmpty(target)) return;
                var device=await Bounded(BluetoothLEDevice.FromIdAsync(target).AsTask());
                if(device==null) return;
                if(target!=TargetId) { device.Dispose();return; }
                targetDevice=device;
                double interval=device.GetConnectionParameters().ConnectionInterval*1.25;
                inputInterval=interval>0 ? Math.Max(30,(int)Math.Ceiling(interval*1.5)) : 30;
                Log.Write("蓝牙输入发送间隔："+inputInterval+" ms。指针移动采用合并发送。 ");
            } catch(Exception ex) { inputInterval=30; Log.Write("使用默认蓝牙发送间隔 30 ms："+ex.Message); }
        }
        public async Task<bool> SendAsync(bool isKeyboard,byte[] value) {
            return await SendToAsync(TargetId,isKeyboard,value,true,null);
        }
        public async Task<bool> SendToAsync(string target,bool isKeyboard,byte[] value,bool requireCurrent,Func<bool> valid) {
            var report=isKeyboard ? keyboard : mouse;
            if(!Started || report==null || string.IsNullOrEmpty(target)) return false;
            await sendGate.WaitAsync();
            try {
                if((requireCurrent && target!=TargetId) || !Started || (valid!=null && !valid())) return false;
                int delay=inputInterval-(int)(inputClock.ElapsedMilliseconds-lastNotification);
                if(delay>0) await Task.Delay(delay);
                if((requireCurrent && target!=TargetId) || !Started || (valid!=null && !valid())) return false;
                var client=report.SubscribedClients.FirstOrDefault(c=>c.Session.DeviceId.Id==target && c.Session.SessionStatus==GattSessionStatus.Active);
                if(client==null) { Lost(); return false; }
                lastNotification=inputClock.ElapsedMilliseconds;
                GattClientNotificationResult results;
                using(var cancellation=new CancellationTokenSource(2000)) results=await report.NotifyValueAsync(value.AsBuffer(),client).AsTask(cancellation.Token);
                long elapsed=inputClock.ElapsedMilliseconds-lastNotification;
                if(elapsed>100 && inputClock.ElapsedMilliseconds-lastSlowLog>5000) { lastSlowLog=inputClock.ElapsedMilliseconds; Log.Write("蓝牙输入确认耗时 "+elapsed+" ms，旧的移动数据不会排队补发。 "); }
                if(results.Status!=GattCommunicationStatus.Success) { Lost(); return false; }
                return true;
            } catch(Exception ex) { Log.Write("发送输入失败："+ex.Message); Lost(); return false; }
            finally { sendGate.Release(); }
        }
        private void Lost() { var handler=InputLost; if(handler!=null) handler(); }
        public void Stop() {
            Started=false; TargetId=null;
            if(targetDevice!=null) { targetDevice.Dispose();targetDevice=null; }
            foreach(var provider in new[] {hidProvider,screenProvider}) if(provider!=null) { try { provider.StopAdvertising(); } catch { } }
            hidProvider=null; screenProvider=null; keyboard=null; mouse=null;
            lock(frameGate) assemblers.Clear();
            var handler=Changed; if(handler!=null) handler();
        }
        public void Dispose() { if(disposed) return; Stop(); disposed=true; }
    }
}
