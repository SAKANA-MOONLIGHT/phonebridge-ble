using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace PhoneBridge {
    public sealed class ScreenPanel : Panel {
        public Image Frame;
        public bool Controlling;
        public string EmptyText="等待 iPhone 画面\n\n先开启连接，并在右侧选择手机。\n手机安装配套 App 后，开始屏幕广播。\n\n仅完成蓝牙配对不会自动出现画面。";
        public ScreenPanel() { DoubleBuffered=true; TabStop=true; BackColor=Color.FromArgb(24,44,64); }
        protected override void OnPaint(PaintEventArgs e) {
            base.OnPaint(e); e.Graphics.InterpolationMode=InterpolationMode.HighQualityBicubic;
            if(Frame!=null) {
                float scale=Math.Min((float)ClientSize.Width/Frame.Width,(float)ClientSize.Height/Frame.Height);
                int w=(int)(Frame.Width*scale),h=(int)(Frame.Height*scale);
                e.Graphics.DrawImage(Frame,(ClientSize.Width-w)/2,(ClientSize.Height-h)/2,w,h);
            } else {
                using(var brush=new SolidBrush(Color.FromArgb(216,229,241))) using(var format=new StringFormat {Alignment=StringAlignment.Center,LineAlignment=StringAlignment.Center})
                    e.Graphics.DrawString(EmptyText,Font,brush,new RectangleF(24,24,Width-48,Height-48),format);
            }
            if(Controlling) using(var pen=new Pen(Color.FromArgb(81,181,236),4)) e.Graphics.DrawRectangle(pen,2,2,Width-5,Height-5);
        }
        public void Replace(Image frame) { var old=Frame; Frame=frame; if(old!=null) old.Dispose(); Invalidate(); }
        protected override void Dispose(bool disposing) { if(disposing && Frame!=null) Frame.Dispose(); base.Dispose(disposing); }
    }

    public sealed class MainForm : Form {
        private readonly BluetoothHost host=new BluetoothHost();
        private readonly KeyboardState keys=new KeyboardState();
        private readonly InputPump pump;
        private KeyboardHook hook;
        private readonly ScreenPanel viewer=new ScreenPanel();
        private readonly ComboBox peers=new ComboBox {DropDownStyle=ComboBoxStyle.DropDownList,Dock=DockStyle.Top};
        private readonly TextBox logs=new TextBox {Multiline=true,ReadOnly=true,ScrollBars=ScrollBars.Vertical,Dock=DockStyle.Fill,BackColor=Color.White,BorderStyle=BorderStyle.FixedSingle};
        private readonly Label status=new Label {Dock=DockStyle.Top,AutoSize=false,Height=64};
        private readonly Label frameStatus=new Label {Dock=DockStyle.Bottom,Height=34,Text="尚未收到手机画面",TextAlign=ContentAlignment.MiddleLeft};
        private readonly Button start=new Button(),control=new Button();
        private readonly TrackBar speed=new TrackBar {Minimum=10,Maximum=100,Value=35,TickFrequency=10,SmallChange=5,LargeChange=10,Dock=DockStyle.Fill};
        private readonly Label speedLabel=new Label {Text="指针速度：35%",Dock=DockStyle.Fill};
        private readonly Timer inputTimer=new Timer {Interval=20},stateTimer=new Timer {Interval=1500};
        private bool capturing,updatingPeers,closing;
        private Point? lastMouse;
        private byte buttons;
        private readonly object frameGate=new object();
        private byte[] pendingFrame;
        private DateTime lastFrame=DateTime.MinValue;
        private int frameCount;

        public MainForm(bool preview) {
            // Visual tokens: paper #F3F6FA, ink #1C2E40, action #245D8E,
            // screen #182C40, rule #D5DFEA. Screen-first layout, clear actions.
            AutoScaleDimensions=new SizeF(96,96); AutoScaleMode=AutoScaleMode.Dpi;
            Text="手机连接 · PhoneBridge"; Font=new Font("Microsoft YaHei UI",10); ClientSize=new Size(1040,750); MinimumSize=new Size(860,650);
            BackColor=Color.FromArgb(243,246,250); ForeColor=Color.FromArgb(28,46,64); StartPosition=FormStartPosition.CenterScreen;
            var layout=new TableLayoutPanel {Dock=DockStyle.Fill,Padding=new Padding(20),ColumnCount=2,RowCount=2};
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100)); layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,310));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute,66)); layout.RowStyles.Add(new RowStyle(SizeType.Percent,100));
            var title=new Label {Text="手机连接",Font=new Font(Font.FontFamily,22,FontStyle.Bold),Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleLeft};
            layout.Controls.Add(title,0,0);
            var hint=new Label {Text="电脑："+Environment.MachineName,Dock=DockStyle.Fill,TextAlign=ContentAlignment.MiddleRight}; layout.Controls.Add(hint,1,0);
            var screenContainer=new Panel {Dock=DockStyle.Fill,Padding=new Padding(0,0,20,0)};
            viewer.Dock=DockStyle.Fill; screenContainer.Controls.Add(viewer); screenContainer.Controls.Add(frameStatus); layout.Controls.Add(screenContainer,0,1);
            var side=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=1,RowCount=12};
            int[] heights={48,48,58,28,40,52,26,44,94,36,30};
            foreach(int height in heights) side.RowStyles.Add(new RowStyle(SizeType.Absolute,height)); side.RowStyles.Add(new RowStyle(SizeType.Percent,100));
            ConfigureButton(start,"开启蓝牙连接",true); start.Click+=async delegate { await ToggleConnection(); }; side.Controls.Add(start,0,0);
            var diagnose=new Button(); ConfigureButton(diagnose,"检查这台电脑",false); diagnose.Click+=async delegate { await Diagnose(); }; side.Controls.Add(diagnose,0,1);
            status.Text="连接未开启\r\n请先检查电脑，再开启连接。"; side.Controls.Add(status,0,2);
            side.Controls.Add(new Label {Text="选择你的手机",Dock=DockStyle.Fill},0,3); side.Controls.Add(peers,0,4);
            ConfigureButton(control,"开始鼠标、键盘控制",true); control.Enabled=false; control.Click+=delegate { SetCapture(!capturing); }; side.Controls.Add(control,0,5);
            side.Controls.Add(speedLabel,0,6);side.Controls.Add(speed,0,7);speed.ValueChanged+=delegate { speedLabel.Text="指针速度："+speed.Value+"%"; };
            side.Controls.Add(new Label {Text="手机：辅助触控 → 设备 → 蓝牙设备。\r\n左侧按触控板方式操作。\r\nCtrl+Alt+Q 停止控制。",Dock=DockStyle.Fill},0,8);
            var settings=new LinkLabel {Text="打开蓝牙设置",Dock=DockStyle.Fill}; settings.LinkClicked+=delegate { Open("ms-settings:bluetooth"); }; side.Controls.Add(settings,0,9);
            side.Controls.Add(new Label {Text="连接记录",Dock=DockStyle.Fill},0,10); side.Controls.Add(logs,0,11);
            layout.Controls.Add(side,1,1); Controls.Add(layout);
            pump=new InputPump(host); pump.Overflow+=delegate { Ui(delegate { SetCapture(false); Log.Write("输入队列过长，已停止控制。 "); }); };
            host.Changed+=delegate { Ui(RefreshPeers); };
            host.InputLost+=delegate { Ui(delegate { if(capturing) SetCapture(false); }); };
            host.FrameReceived+=delegate(byte[] frame) { lock(frameGate) pendingFrame=frame; };
            Log.Message+=OnLog;
            peers.SelectedIndexChanged+=async delegate { if(updatingPeers) return; SetCapture(false); var peer=peers.SelectedItem as Peer; host.TargetId=peer==null ? null : peer.Id; lock(frameGate) pendingFrame=null; viewer.Replace(null); lastFrame=DateTime.MinValue; RefreshState(); await host.ReadTargetParametersAsync(); };
            viewer.MouseDown+=delegate(object sender,MouseEventArgs e) { viewer.Focus(); if(!capturing) return; viewer.Capture=true; buttons |= ButtonMask(e.Button); lastMouse=e.Location; pump.SetButtons(buttons); };
            viewer.MouseUp+=delegate(object sender,MouseEventArgs e) { if(!capturing) return; buttons &= (byte)~ButtonMask(e.Button); pump.SetButtons(buttons); if(buttons==0) viewer.Capture=false; };
            viewer.MouseMove+=delegate(object sender,MouseEventArgs e) { if(capturing && viewer.Focused && lastMouse.HasValue) { double gain=(speed.Value/100.0)*96.0/CurrentAutoScaleDimensions.Width; pump.Move((e.X-lastMouse.Value.X)*gain,(e.Y-lastMouse.Value.Y)*gain,0); } lastMouse=e.Location; };
            viewer.MouseLeave+=delegate { lastMouse=null; };
            viewer.MouseWheel+=delegate(object sender,MouseEventArgs e) { if(capturing) pump.Move(0,0,e.Delta/SystemInformation.MouseWheelScrollDelta); };
            viewer.LostFocus+=delegate { if(capturing) SetCapture(false); };
            Deactivate+=delegate { if(capturing) SetCapture(false); };
            inputTimer.Tick+=delegate { DisplayFrame(); };
            stateTimer.Tick+=delegate { RefreshPeers(); if(lastFrame!=DateTime.MinValue && (DateTime.UtcNow-lastFrame).TotalSeconds>5) frameStatus.Text="画面已暂停，请检查手机屏幕广播和蓝牙连接"; };
            FormClosing+=OnClosing;
            Shown+=delegate {
                if(preview) return;
                var area=Screen.FromControl(this).WorkingArea; if(Height>area.Height-30) Height=area.Height-30; if(Width>area.Width-30) Width=area.Width-30;
                try { hook=new KeyboardHook(); hook.OnKey=HandleKey; } catch(Exception ex) { Log.Write("键盘捕获暂不可用："+ex.Message); }
                inputTimer.Start(); stateTimer.Start();
                Log.Write("程序已启动。尚未验证当前电脑与 iPhone 的实际兼容性。 ");
            };
            if(preview) { status.Text="界面预览\r\n没有连接真实手机"; viewer.EmptyText="界面预览\n\n这里将显示手机的实时画面。\n当前没有连接手机。"; }
            AutoScaleDimensions=new SizeF(96,96);
            PerformAutoScale();
            layout.ColumnStyles[1].Width=310*CurrentAutoScaleDimensions.Width/96f;
            layout.PerformLayout();
        }
        private void ConfigureButton(Button button,string text,bool primary) {
            button.Text=text; button.Dock=DockStyle.Fill; button.Margin=new Padding(0,0,0,10); button.FlatStyle=FlatStyle.Flat;
            button.FlatAppearance.BorderColor=Color.FromArgb(213,223,234); button.BackColor=primary ? Color.FromArgb(36,93,142) : Color.White; button.ForeColor=primary ? Color.White : ForeColor;
        }
        private static byte ButtonMask(MouseButtons button) { return (byte)(button==MouseButtons.Left ? 1 : button==MouseButtons.Right ? 2 : button==MouseButtons.Middle ? 4 : 0); }
        private void Ui(Action action) { if(IsDisposed || closing) return; if(InvokeRequired) { try { BeginInvoke(action); } catch(InvalidOperationException) { } } else action(); }
        private void OnLog(string line) { Ui(delegate { logs.AppendText(line+Environment.NewLine); if(logs.TextLength>16000) logs.Text=logs.Text.Substring(logs.TextLength-12000); logs.SelectionStart=logs.TextLength; logs.ScrollToCaret(); }); }
        private async Task Diagnose() {
            try { string result=await BluetoothHost.DiagnoseAsync(); MessageBox.Show(this,result,"电脑检查",MessageBoxButtons.OK,MessageBoxIcon.Information); Log.Write(result.Replace("\r\n"," / ")); }
            catch(Exception ex) { Error(ex); }
        }
        private async Task ToggleConnection() {
            start.Enabled=false;
            try {
                SetCapture(false);
                if(host.Started) { await pump.ReleaseAsync(); host.Stop(); start.Text="开启蓝牙连接"; }
                else { await host.StartAsync(); start.Text="关闭蓝牙连接"; }
                RefreshPeers();
            } catch(Exception ex) { Error(ex); } finally { start.Enabled=true; }
        }
        private void Error(Exception ex) { Log.Write(ex.ToString()); MessageBox.Show(this,ex.Message,"连接提示",MessageBoxButtons.OK,MessageBoxIcon.Information); }
        private void RefreshPeers() {
            if(closing) return;
            try {
                var list=host.Peers(); string selected=host.TargetId;
                string old=string.Join("|",peers.Items.Cast<Peer>().Select(p=>p.Id)); string fresh=string.Join("|",list.Select(p=>p.Id));
                if(old!=fresh) {
                    if(capturing) SetCapture(false);
                    updatingPeers=true; peers.Items.Clear(); foreach(var peer in list) peers.Items.Add(peer);
                    int index=list.FindIndex(p=>p.Id==selected); peers.SelectedIndex=index; updatingPeers=false;
                    if(index<0) { host.TargetId=null; viewer.Replace(null); lastFrame=DateTime.MinValue; }
                    if(list.Count==1 && index<0) { peers.SelectedIndex=0; host.TargetId=list[0].Id; }
                }
                RefreshState();
            } catch(Exception ex) { Log.Write("刷新设备失败："+ex.Message); }
        }
        private void RefreshState() {
            bool connected=host.TargetConnected; control.Enabled=connected; peers.Enabled=!capturing;
            status.Text=!host.Started ? "连接未开启\r\n请先检查电脑，再开启连接。" : !host.Ready ? "蓝牙广播尚未就绪\r\n请稍候，或关闭连接后重试。" : connected ? "手机已订阅输入服务\r\n实际操作效果需要在手机上确认。" : "等待手机配对并订阅\r\n请在 iPhone 辅助触控中选择此电脑。";
        }
        private async void SetCapture(bool enabled) {
            if(enabled && !host.TargetConnected) return;
            if(capturing==enabled) return;
            capturing=enabled; viewer.Controlling=enabled; viewer.Invalidate();
            control.Text=enabled ? "停止控制  Ctrl+Alt+Q" : "开始鼠标、键盘控制"; peers.Enabled=!enabled;
            keys.Clear(); buttons=0; lastMouse=null;
            if(enabled) { viewer.Focus(); Log.Write("已开启控制。仅左侧区域接收鼠标和键盘；Ctrl+Alt+Q 停止。 "); }
            else { viewer.Capture=false; control.Enabled=false; await pump.ReleaseAsync(); if(!closing) RefreshState(); Log.Write("已停止控制，并请求释放手机上的按键。 "); }
        }
        private bool HandleKey(int vk,int scan,bool extended,bool down) {
            if(!capturing || !KeyboardHook.IsForeground(Handle) || !viewer.Focused) return false;
            if(vk==0x51 && KeyboardHook.ControlAlt) { if(down) SetCapture(false); return true; }
            if(vk==0x10) vk=scan==0x36 ? 0xa1 : 0xa0;
            if(vk==0x11) vk=extended ? 0xa3 : 0xa2;
            if(vk==0x12) vk=extended ? 0xa5 : 0xa4;
            byte usage=Hid.Usage(vk,scan,extended); if(usage==0) return false;
            pump.Enqueue(true,keys.Update(usage,down)); return true;
        }
        private void DisplayFrame() {
            byte[] frame; lock(frameGate) { frame=pendingFrame; pendingFrame=null; }
            if(frame==null) return;
            try {
                JpegGuard.Validate(frame);
                using(var stream=new MemoryStream(frame)) using(var image=Image.FromStream(stream,true,true)) viewer.Replace(new Bitmap(image));
                lastFrame=DateTime.UtcNow; frameCount++; frameStatus.Text="已收到手机画面  ·  第 "+frameCount+" 帧  ·  "+frame.Length/1024+" KB";
            } catch(Exception ex) { Log.Write("画面解码失败："+ex.Message); }
        }
        private async void OnClosing(object sender,FormClosingEventArgs e) {
            if(closing) return; e.Cancel=true; closing=true; capturing=false; inputTimer.Stop(); stateTimer.Stop();
            if(hook!=null) hook.Dispose();
            try { await Task.WhenAny(pump.ReleaseAsync(),Task.Delay(1500)); } catch { }
            pump.Dispose(); host.Dispose(); Log.Message-=OnLog; Close();
        }
        private static void Open(string path) { try { Process.Start(new ProcessStartInfo(path) {UseShellExecute=true}); } catch(Exception ex) { MessageBox.Show(ex.Message); } }
        public void SavePreview(string path) { CreateControl(); Show(); Application.DoEvents(); using(var bitmap=new Bitmap(Width,Height)) { DrawToBitmap(bitmap,new Rectangle(0,0,Width,Height)); bitmap.Save(path,System.Drawing.Imaging.ImageFormat.Png); } closing=true; Close(); }
    }

    public static class JpegGuard {
        public static void Validate(byte[] bytes) {
            if(bytes.Length<4 || bytes[0]!=0xff || bytes[1]!=0xd8) throw new InvalidDataException("Not a JPEG image");
            int p=2;
            while(p+3<bytes.Length) {
                if(bytes[p++]!=0xff) throw new InvalidDataException("Invalid JPEG marker");
                while(p<bytes.Length && bytes[p]==0xff) p++;
                if(p>=bytes.Length) break; int marker=bytes[p++];
                if(marker==0xd9 || marker==0xda) break;
                if(marker==0x01 || (marker>=0xd0 && marker<=0xd7)) continue;
                if(p+1>=bytes.Length) break; int length=(bytes[p]<<8)|bytes[p+1];
                if(length<2 || p+length>bytes.Length) throw new InvalidDataException("Invalid JPEG segment");
                if(marker==0xc0 || marker==0xc1 || marker==0xc2) {
                    if(length<8) break; int h=(bytes[p+3]<<8)|bytes[p+4],w=(bytes[p+5]<<8)|bytes[p+6];
                    if(w<1 || h<1 || w>1920 || h>1920 || w*h>1920*1080) throw new InvalidDataException("Image dimensions exceed limit"); return;
                }
                p+=length;
            }
            throw new InvalidDataException("Missing JPEG dimensions");
        }
    }
}
