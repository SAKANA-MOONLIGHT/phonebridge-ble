using System;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace PhoneBridge {
    internal static class Program {
        [STAThread]
        private static int Main(string[] args) {
            if(args.Length>0) {
                string output=args.Length>1 ? args[1] : Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"diagnostics.txt");
                Log.PathName=Path.Combine(Path.GetDirectoryName(Path.GetFullPath(output)),"probe.log");
                try {
                    if(args[0]=="--self-test") { File.WriteAllText(output,SelfTests.Run(),Encoding.UTF8); return 0; }
                    if(args[0]=="--diagnose" || args[0]=="--probe") { File.WriteAllText(output,Probe(args[0]=="--probe").GetAwaiter().GetResult(),Encoding.UTF8); return 0; }
                    if(args[0]=="--render") { Application.EnableVisualStyles(); using(var form=new MainForm(true)) form.SavePreview(output); return 0; }
                    throw new ArgumentException("Unknown option");
                } catch(Exception ex) { File.WriteAllText(output,ex.ToString(),Encoding.UTF8); return 1; }
            }
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            Application.ThreadException+=delegate(object sender,System.Threading.ThreadExceptionEventArgs e) { Log.Write(e.Exception.ToString()); MessageBox.Show(e.Exception.Message,"程序提示"); };
            Application.Run(new MainForm(false)); return 0;
        }
        private static async Task<string> Probe(bool publish) {
            var text=new StringBuilder(); text.AppendLine("PhoneBridge " + DateTimeOffset.Now.ToString("o"));
            text.AppendLine("OS="+Environment.OSVersion); text.AppendLine("64Bit="+Environment.Is64BitProcess);
            using(var identity=WindowsIdentity.GetCurrent()) text.AppendLine("Elevated="+new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator));
            text.AppendLine(await BluetoothHost.DiagnoseAsync());
            if(publish) using(var host=new BluetoothHost()) { await host.StartAsync(); await Task.Delay(3000); text.AppendLine("ServiceCreation=Success"); text.AppendLine(host.PublicationSummary()); text.AppendLine("InputSubscribers="+host.Peers().Count); text.AppendLine("This does not prove iPhone input or screen mirroring."); if(!host.Ready) throw new InvalidOperationException(text+"\r\nAdvertising failed; service creation alone is not a successful probe."); }
            return text.ToString();
        }
    }
}
