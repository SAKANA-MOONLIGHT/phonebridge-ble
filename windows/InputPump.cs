using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace PhoneBridge {
    public sealed class InputPump : IDisposable {
        private sealed class Item { public bool Keyboard; public byte[] Value; public int Generation; public long ExpiresAt; }
        private readonly ConcurrentQueue<Item> queue=new ConcurrentQueue<Item>();
        private readonly AutoResetEvent wake=new AutoResetEvent(false);
        private readonly BluetoothHost host;
        private readonly Task worker;
        private readonly object motionGate=new object();
        private readonly MotionAccumulator motion=new MotionAccumulator();
        private readonly Stopwatch clock=Stopwatch.StartNew();
        private int mouseButtons;
        private volatile bool closing;
        private int generation, count;
        public event Action Overflow;
        public InputPump(BluetoothHost host) { this.host=host; worker=Task.Run((Func<Task>)Run); }
        public void Enqueue(bool keyboard,byte[] value) {
            if(closing) return;
            if(Interlocked.Increment(ref count)>64) { Interlocked.Decrement(ref count); var handler=Overflow; if(handler!=null) handler(); return; }
            queue.Enqueue(new Item {Keyboard=keyboard,Value=value,Generation=Volatile.Read(ref generation)}); wake.Set();
        }
        public void Move(double x,double y,int scroll) { lock(motionGate) motion.Add(x,y,scroll,clock.ElapsedMilliseconds); wake.Set(); }
        public void SetButtons(byte buttons) {
            int x,y,w;
            lock(motionGate) {
                if(motion.Take(clock.ElapsedMilliseconds,out x,out y,out w)) Enqueue(false,Hid.Mouse((byte)mouseButtons,x,y,w));
                mouseButtons=buttons;
                Enqueue(false,Hid.Mouse(buttons,0,0,0));
            }
        }
        private async Task Run() {
            while(!closing) {
                Item item;
                if(queue.TryDequeue(out item)) Interlocked.Decrement(ref count);
                else {
                    int x,y,w;
                    lock(motionGate) {
                        if(motion.Take(clock.ElapsedMilliseconds,out x,out y,out w)) item=new Item {Keyboard=false,Value=Hid.Mouse((byte)mouseButtons,x,y,w),Generation=Volatile.Read(ref generation),ExpiresAt=clock.ElapsedMilliseconds+120};
                    }
                    if(item==null) { wake.WaitOne(20); continue; }
                }
                if(item.Generation!=Volatile.Read(ref generation)) continue;
                int expected=item.Generation;
                await host.SendToAsync(host.TargetId,item.Keyboard,item.Value,true,delegate { return expected==Volatile.Read(ref generation) && (item.ExpiresAt==0 || clock.ElapsedMilliseconds<=item.ExpiresAt); });
            }
        }
        public async Task ReleaseAsync() {
            string target=host.TargetId;
            Interlocked.Increment(ref generation);
            lock(motionGate) { motion.Clear();mouseButtons=0; }
            Item discarded; while(queue.TryDequeue(out discarded)) Interlocked.Decrement(ref count);
            await host.SendToAsync(target,true,new byte[8],false,null);
            await host.SendToAsync(target,false,new byte[4],false,null);
        }
        public void Dispose() { closing=true; wake.Set(); }
    }

    public sealed class KeyboardHook : IDisposable {
        private delegate IntPtr HookProc(int code,IntPtr wParam,IntPtr lParam);
        private readonly HookProc callback;
        private IntPtr hook;
        public Func<int,int,bool,bool,bool> OnKey;
        public KeyboardHook() { callback=Handle; hook=SetWindowsHookEx(13,callback,GetModuleHandle(null),0); if(hook==IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()); }
        private IntPtr Handle(int code,IntPtr wParam,IntPtr lParam) {
            if(code>=0 && OnKey!=null) {
                int kind=wParam.ToInt32(); bool down=kind==0x100 || kind==0x104, up=kind==0x101 || kind==0x105;
                if(down || up) {
                    int vk=Marshal.ReadInt32(lParam),scan=Marshal.ReadInt32(lParam,4),flags=Marshal.ReadInt32(lParam,8);
                    if((flags & 0x10)==0 && OnKey(vk,scan,(flags & 1)!=0,down)) return new IntPtr(1);
                }
            }
            return CallNextHookEx(hook,code,wParam,lParam);
        }
        public static bool ControlAlt { get { return (GetAsyncKeyState(0x11)&0x8000)!=0 && (GetAsyncKeyState(0x12)&0x8000)!=0; } }
        public static bool IsForeground(IntPtr hwnd) { return GetForegroundWindow()==hwnd; }
        public void Dispose() { if(hook!=IntPtr.Zero) { UnhookWindowsHookEx(hook); hook=IntPtr.Zero; } }
        [DllImport("user32.dll",SetLastError=true)] private static extern IntPtr SetWindowsHookEx(int type,HookProc callback,IntPtr module,uint thread);
        [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook,int code,IntPtr wParam,IntPtr lParam);
        [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("kernel32.dll",CharSet=CharSet.Auto)] private static extern IntPtr GetModuleHandle(string name);
    }
}
