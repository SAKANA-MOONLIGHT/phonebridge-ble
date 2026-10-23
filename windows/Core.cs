using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace PhoneBridge {
    public static class Log {
        private static readonly object Gate = new object();
        public static event Action<string> Message;
        public static string PathName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PhoneBridge", "session.log");
        public static void Write(string message) {
            string line = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz") + "  " + message;
            lock (Gate) { try { Directory.CreateDirectory(Path.GetDirectoryName(PathName)); File.AppendAllText(PathName, line + Environment.NewLine, Encoding.UTF8); } catch { } }
            var handler = Message; if (handler != null) handler(line);
        }
    }

    public static class Wire {
        public static readonly Guid Service = new Guid("72d00001-7b56-4a5e-9f12-8c454e50424c");
        public static readonly Guid Frame = new Guid("72d00002-7b56-4a5e-9f12-8c454e50424c");
        public static readonly Guid Info = new Guid("72d00003-7b56-4a5e-9f12-8c454e50424c");
        public const int Header = 12;
        public const int MaxFrame = 256 * 1024;
        public static uint U32(byte[] bytes, int offset) { return (uint)(bytes[offset] | bytes[offset+1] << 8 | bytes[offset+2] << 16 | bytes[offset+3] << 24); }
        public static void Put32(byte[] b, int o, uint v) { for(int i=0;i<4;i++) b[o+i] = (byte)(v >> (i*8)); }
        public static uint Crc32(byte[] bytes) {
            uint crc = 0xffffffff;
            foreach (byte value in bytes) { crc ^= value; for (int i=0;i<8;i++) crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0u : 0xedb88320u); }
            return ~crc;
        }
        public static byte[] Packet(byte kind, uint id, uint offset, byte[] body) {
            byte[] b = new byte[Header + body.Length]; b[0]=0x4c; b[1]=0x50; b[2]=1; b[3]=kind; Put32(b,4,id); Put32(b,8,offset); Buffer.BlockCopy(body,0,b,Header,body.Length); return b;
        }
    }

    // A separate bounded assembler is created for each authenticated BLE writer.
    public sealed class FrameAssembler {
        private byte[] buffer;
        private uint frameId, crc;
        private int received;
        private readonly Stopwatch timer = new Stopwatch();
        public void Reset() { buffer = null; received=0; timer.Reset(); }
        public byte[] Accept(byte[] packet) {
            if (packet == null || packet.Length < Wire.Header || packet[0]!=0x4c || packet[1]!=0x50 || packet[2]!=1) throw new InvalidDataException("Invalid frame packet");
            byte kind=packet[3]; uint id=Wire.U32(packet,4), offset=Wire.U32(packet,8);
            if(kind==1) {
                if(packet.Length!=20 || offset!=0) throw new InvalidDataException("Invalid frame start");
                uint size=Wire.U32(packet,12); if(size==0 || size>Wire.MaxFrame) { Reset(); throw new InvalidDataException("Frame size exceeds limit"); }
                buffer=new byte[(int)size]; received=0; frameId=id; crc=Wire.U32(packet,16); timer.Restart(); return null;
            }
            if(buffer==null || id!=frameId) throw new InvalidDataException("Frame has no matching start");
            if(timer.ElapsedMilliseconds>30000) { Reset(); throw new InvalidDataException("Frame expired"); }
            if(offset!=(uint)received) { Reset(); throw new InvalidDataException("Missing or reordered frame chunk"); }
            if(kind==2) {
                int size=packet.Length-Wire.Header;
                if(size==0 || size>buffer.Length-received) { Reset(); throw new InvalidDataException("Invalid frame chunk size"); }
                Buffer.BlockCopy(packet,Wire.Header,buffer,received,size); received+=size; return null;
            }
            if(kind==3 && packet.Length==Wire.Header) {
                byte[] result=buffer; bool ok=received==buffer.Length && Wire.Crc32(result)==crc; Reset();
                if(!ok) throw new InvalidDataException("Incomplete frame or checksum mismatch");
                return result;
            }
            Reset(); throw new InvalidDataException("Unknown frame packet");
        }
    }

    public static class Hid {
        public static Guid Uuid(int value) { return new Guid(value.ToString("x8") + "-0000-1000-8000-00805f9b34fb"); }
        // Report IDs are expressed through HOGP Report Reference descriptors, not in characteristic values.
        public static readonly byte[] ReportMap = new byte[] {
            0x05,0x01,0x09,0x06,0xa1,0x01,0x85,0x01,
            0x05,0x07,0x19,0xe0,0x29,0xe7,0x15,0x00,0x25,0x01,0x75,0x01,0x95,0x08,0x81,0x02,
            0x95,0x01,0x75,0x08,0x81,0x01,
            0x95,0x05,0x75,0x01,0x05,0x08,0x19,0x01,0x29,0x05,0x91,0x02,
            0x95,0x01,0x75,0x03,0x91,0x01,
            0x95,0x06,0x75,0x08,0x15,0x00,0x25,0x65,0x05,0x07,0x19,0x00,0x29,0x65,0x81,0x00,0xc0,
            0x05,0x01,0x09,0x02,0xa1,0x01,0x85,0x02,0x09,0x01,0xa1,0x00,
            0x05,0x09,0x19,0x01,0x29,0x03,0x15,0x00,0x25,0x01,0x95,0x03,0x75,0x01,0x81,0x02,
            0x95,0x01,0x75,0x05,0x81,0x01,
            0x05,0x01,0x09,0x30,0x09,0x31,0x09,0x38,0x15,0x81,0x25,0x7f,0x75,0x08,0x95,0x03,0x81,0x06,0xc0,0xc0
        };
        public static byte[] Mouse(byte buttons, int x, int y, int wheel) { return new byte[] { buttons, unchecked((byte)(sbyte)Clamp(x)), unchecked((byte)(sbyte)Clamp(y)), unchecked((byte)(sbyte)Clamp(wheel)) }; }
        public static int Clamp(int n) { return Math.Max(-127,Math.Min(127,n)); }
        public static byte Usage(int vk, int scanCode, bool extended) {
            if(vk>=0x41 && vk<=0x5a) return (byte)(4+vk-0x41);
            if(vk>=0x31 && vk<=0x39) return (byte)(0x1e+vk-0x31);
            if(vk==0x30) return 0x27;
            if(vk>=0x70 && vk<=0x7b) return (byte)(0x3a+vk-0x70);
            if(vk>=0x61 && vk<=0x69) return (byte)(0x59+vk-0x61);
            switch(vk) {
                case 0x0d:return (byte)(extended ? 0x58 : 0x28); case 0x1b:return 0x29; case 0x08:return 0x2a; case 0x09:return 0x2b; case 0x20:return 0x2c;
                case 0xbd:return 0x2d; case 0xbb:return 0x2e; case 0xdb:return 0x2f; case 0xdd:return 0x30; case 0xdc:return 0x31; case 0xba:return 0x33;
                case 0xde:return 0x34; case 0xc0:return 0x35; case 0xbc:return 0x36; case 0xbe:return 0x37; case 0xbf:return 0x38; case 0x14:return 0x39;
                case 0x2d:return 0x49; case 0x24:return 0x4a; case 0x21:return 0x4b; case 0x2e:return 0x4c; case 0x23:return 0x4d; case 0x22:return 0x4e;
                case 0x27:return 0x4f; case 0x25:return 0x50; case 0x28:return 0x51; case 0x26:return 0x52;
                case 0x90:return 0x53; case 0x6f:return 0x54; case 0x6a:return 0x55; case 0x6d:return 0x56; case 0x6b:return 0x57; case 0x60:return 0x62; case 0x6e:return 0x63;
                case 0xa2:return 0xe0; case 0xa0:return 0xe1; case 0xa4:return 0xe2; case 0x5b:return 0xe3;
                case 0xa3:return 0xe4; case 0xa1:return 0xe5; case 0xa5:return 0xe6; case 0x5c:return 0xe7;
            }
            return 0;
        }
    }

    public sealed class KeyboardState {
        private readonly HashSet<byte> pressed = new HashSet<byte>();
        public byte[] Update(byte usage, bool down) { if(usage!=0) { if(down) pressed.Add(usage); else pressed.Remove(usage); } return Report(); }
        public byte[] Report() {
            byte[] bytes=new byte[8]; List<byte> keys=new List<byte>();
            foreach(byte key in pressed) { if(key>=0xe0 && key<=0xe7) bytes[0] |= (byte)(1 << (key-0xe0)); else keys.Add(key); }
            keys.Sort(); if(keys.Count>6) for(int i=2;i<8;i++) bytes[i]=1; else for(int i=0;i<keys.Count;i++) bytes[2+i]=keys[i]; return bytes;
        }
        public void Clear() { pressed.Clear(); }
    }

    // A bounded, short-lived motion slot; old movement never becomes a long FIFO.
    public sealed class MotionAccumulator {
        private double x,y;
        private int wheel;
        private long lastUpdate;
        public void Add(double dx,double dy,int scroll,long now) {
            if(now-lastUpdate>120) Clear();
            x=Math.Max(-127,Math.Min(127,x+dx)); y=Math.Max(-127,Math.Min(127,y+dy));
            wheel=Hid.Clamp(wheel+scroll); lastUpdate=now;
        }
        public bool Take(long now,out int dx,out int dy,out int scroll) {
            if(now-lastUpdate>120) Clear();
            dx=(int)x;dy=(int)y;scroll=wheel; x-=dx;y-=dy;wheel=0;
            return dx!=0 || dy!=0 || scroll!=0;
        }
        public void Clear() { x=y=0;wheel=0; }
    }
}
