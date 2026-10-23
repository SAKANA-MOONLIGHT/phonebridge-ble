using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text;

namespace PhoneBridge {
    public static class SelfTests {
        private static int checks;
        private static void Check(bool ok,string message) { checks++; if(!ok) throw new Exception("FAIL: "+message); }
        private static void Reject(Action action,string message) { bool rejected=false; try { action(); } catch(InvalidDataException) { rejected=true; } Check(rejected,message); }
        private static byte[] Start(byte[] payload,uint id) { byte[] body=new byte[8]; Wire.Put32(body,0,(uint)payload.Length); Wire.Put32(body,4,Wire.Crc32(payload)); return Wire.Packet(1,id,0,body); }
        public static string Run() {
            checks=0;
            Check(Wire.Crc32(Encoding.ASCII.GetBytes("123456789"))==0xcbf43926,"CRC32 golden vector");
            Check(BitConverter.ToString(Start(new byte[] {1,2,3},42)).Replace("-","").ToLowerInvariant()=="4c5001012a00000000000000030000001d80bc55","shared Swift/Windows start vector");
            foreach(int mtu in new[] {20,185,512}) {
                var payload=Enumerable.Range(0,32000).Select(n=>(byte)(n*17)).ToArray(); var assembler=new FrameAssembler();
                Check(assembler.Accept(Start(payload,42))==null,"start does not publish incomplete frame");
                for(int offset=0;offset<payload.Length;) { int count=Math.Min(mtu-Wire.Header,payload.Length-offset); assembler.Accept(Wire.Packet(2,42,(uint)offset,payload.Skip(offset).Take(count).ToArray())); offset+=count; }
                Check(payload.SequenceEqual(assembler.Accept(Wire.Packet(3,42,(uint)payload.Length,new byte[0]))),"frame roundtrip at MTU "+mtu);
            }
            var a=new FrameAssembler(); var small=new byte[] {1,2,3};
            Reject(delegate { a.Accept(new byte[] {1,2}); },"short packet rejected");
            a.Accept(Start(small,7)); Reject(delegate { a.Accept(Wire.Packet(2,7,1,small)); },"missing first chunk rejected");
            a.Accept(Start(small,7)); a.Accept(Wire.Packet(2,7,0,new byte[] {3,2,1})); Reject(delegate { a.Accept(Wire.Packet(3,7,3,new byte[0])); },"CRC corruption rejected");
            byte[] huge=Start(small,8); Wire.Put32(huge,12,0xffffffff); Reject(delegate { a.Accept(huge); },"oversized allocation rejected");
            a.Accept(Start(small,9)); Reject(delegate { a.Accept(Wire.Packet(2,10,0,small)); },"mixed frame IDs rejected");
            a.Accept(Start(small,9)); Reject(delegate { a.Accept(Wire.Packet(3,9,0,new byte[0])); },"truncated frame rejected");
            var keys=new KeyboardState(); Check(keys.Update(0xe1,true).SequenceEqual(new byte[] {2,0,0,0,0,0,0,0}),"left Shift report");
            Check(keys.Update(4,true).SequenceEqual(new byte[] {2,0,4,0,0,0,0,0}),"Shift+A report");
            Check(keys.Update(4,false)[2]==0,"key release"); keys.Clear(); Check(keys.Report().All(x=>x==0),"all keys released");
            Check(Hid.Usage(0x41,0,false)==4 && Hid.Usage(0x0d,0,true)==0x58,"key mapping");
            Check(Hid.Mouse(1,-999,999,-1).SequenceEqual(new byte[] {1,129,127,255}),"signed mouse limits");
            var motion=new MotionAccumulator();int mx,my,mw;
            for(int i=0;i<1000;i++) motion.Add(10,0,0,i/100);
            Check(motion.Take(10,out mx,out my,out mw) && mx==127,"movement backlog is bounded");
            Check(!motion.Take(11,out mx,out my,out mw),"no delayed movement tail");
            motion.Add(50,40,0,20);Check(!motion.Take(200,out mx,out my,out mw),"stale movement dropped after a slow link");
            motion.Add(0.3,0,0,201);motion.Add(0.3,0,0,202);motion.Add(0.4,0,0,203);Check(motion.Take(204,out mx,out my,out mw) && mx==1,"fractional slow movement retained");
            Reject(delegate { JpegGuard.Validate(new byte[] {0xff,0xd8,0xff,0xd9}); },"missing image size rejected");
            using(var bitmap=new Bitmap(120,240)) using(var stream=new MemoryStream()) { bitmap.Save(stream,ImageFormat.Jpeg); JpegGuard.Validate(stream.ToArray()); Check(true,"generated JPEG accepted (synthetic test only)"); }
            return "PASS: "+checks+" checks. Protocol integrity, bounded frames, keyboard release and JPEG validation.\r\nNo real iPhone connection was exercised.\r\n";
        }
    }
}
