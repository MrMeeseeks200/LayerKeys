using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace MidiKeyboardEmulator
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new EmulatorForm());
        }
    }

    static class MidiOut
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        public struct Caps
        {
            public ushort manufacturer, product;
            public uint version;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string name;
            public ushort technology, voices, notes, channelMask;
            public uint support;
        }
        [DllImport("winmm.dll")] public static extern uint midiOutGetNumDevs();
        [DllImport("winmm.dll", CharSet = CharSet.Auto)] public static extern uint midiOutGetDevCaps(uint id, out Caps caps, uint size);
        [DllImport("winmm.dll")] public static extern uint midiOutOpen(out IntPtr handle, uint id, IntPtr callback, IntPtr instance, uint flags);
        [DllImport("winmm.dll")] public static extern uint midiOutShortMsg(IntPtr handle, uint message);
        [DllImport("winmm.dll")] public static extern uint midiOutReset(IntPtr handle);
        [DllImport("winmm.dll")] public static extern uint midiOutClose(IntPtr handle);
        public const uint Mapper = 0xFFFFFFFF;
        public static uint Message(int status, int d1, int d2) { return (uint)(status | (d1 << 8) | (d2 << 16)); }
    }

    sealed class Piano : Control
    {
        public event Action<int, bool> NoteChanged;
        public readonly HashSet<int> Active = new HashSet<int>();
        int pointerNote = -1;
        readonly int[] whites = { 0,2,4,5,7,9,11,12,14,16,17,19,21,23,24,26,28,29,31,33,35,36 };
        readonly HashSet<int> blackPitch = new HashSet<int>(new int[] {1,3,6,8,10});
        public int BaseNote = 48;

        public Piano()
        {
            DoubleBuffered = true; SetStyle(ControlStyles.ResizeRedraw, true);
            MouseDown += Down; MouseUp += Up; MouseLeave += HandleMouseLeave;
        }
        int Hit(Point p)
        {
            float w = (Width - 8f) / whites.Length;
            if (p.Y < Height * .62f)
            {
                for (int octave = 0; octave < 4; octave++)
                    foreach (int pitch in blackPitch)
                    {
                        int semitone = octave * 12 + pitch;
                        int previousWhite = 0;
                        for (int i = 0; i < whites.Length; i++) if (whites[i] < semitone) previousWhite = i; else break;
                        float x = 4 + (previousWhite + 1) * w - w * .31f;
                        if (p.X >= x && p.X <= x + w * .62f) return BaseNote + semitone;
                    }
            }
            int wi = Math.Max(0, Math.Min(whites.Length - 1, (int)((p.X - 4) / w)));
            return BaseNote + whites[wi];
        }
        void Down(object sender, MouseEventArgs e) { if (e.Button != MouseButtons.Left) return; pointerNote = Hit(e.Location); if (NoteChanged != null) NoteChanged(pointerNote, true); }
        void Up(object sender, MouseEventArgs e) { Release(); }
        void HandleMouseLeave(object sender, EventArgs e) { if (Control.MouseButtons == MouseButtons.None) Release(); }
        void Release() { if (pointerNote >= 0 && NoteChanged != null) NoteChanged(pointerNote, false); pointerNote = -1; }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.Clear(Color.FromArgb(28, 30, 28));
            float w = (Width - 8f) / whites.Length;
            for (int i = 0; i < whites.Length; i++)
            {
                int note = BaseNote + whites[i]; RectangleF r = new RectangleF(4 + i*w, 4, w-1.5f, Height-11);
                using (SolidBrush b = new SolidBrush(Active.Contains(note) ? Color.FromArgb(242,170,70) : Color.FromArgb(252,249,239))) e.Graphics.FillRectangle(b, r);
                using (Pen p = new Pen(Color.FromArgb(150,145,134))) e.Graphics.DrawRectangle(p, r.X,r.Y,r.Width,r.Height);
            }
            for (int octave = 0; octave < 4; octave++)
                foreach (int pitch in blackPitch)
                {
                    int semitone = octave*12+pitch; if (semitone > whites[whites.Length-1]) continue;
                    int previousWhite=0; for(int i=0;i<whites.Length;i++) if(whites[i]<semitone) previousWhite=i; else break;
                    float x=4+(previousWhite+1)*w-w*.31f; int note=BaseNote+semitone;
                    RectangleF r=new RectangleF(x,4,w*.62f,Height*.61f);
                    using(SolidBrush b=new SolidBrush(Active.Contains(note)?Color.FromArgb(45,142,129):Color.FromArgb(29,30,28))) e.Graphics.FillRectangle(b,r);
                }
        }
    }

    sealed class EmulatorForm : Form
    {
        readonly Color Cream=Color.FromArgb(244,239,226), Ink=Color.FromArgb(31,33,30), Teal=Color.FromArgb(44,116,106), Orange=Color.FromArgb(232,105,69), Dark=Color.FromArgb(36,42,39), Gold=Color.FromArgb(242,170,70);
        readonly Dictionary<Keys,int> keyMap = new Dictionary<Keys,int>();
        readonly HashSet<Keys> keysDown = new HashSet<Keys>();
        readonly Dictionary<Keys,int> sounding = new Dictionary<Keys,int>();
        readonly HashSet<int> sustained = new HashSet<int>();
        readonly List<uint> outputIds = new List<uint>();
        IntPtr output=IntPtr.Zero;
        int octave=0, velocity=100, program=0;
        bool sustain;
        Piano piano;
        Label octaveValue, velocityValue, noteLabel, sustainLabel, connectionLabel;
        ComboBox soundBox, outputBox;
        TrackBar velocityBar;
        readonly Timer portTimer = new Timer();
        int lastOutputCount=-1;

        public EmulatorForm()
        {
            Text="MIDI Keyboard Emulator"; Icon=SystemIcons.Application; StartPosition=FormStartPosition.CenterScreen;
            ClientSize=new Size(980,650); MinimumSize=new Size(820,600); BackColor=Cream; Font=new Font("Segoe UI",9); KeyPreview=true;
            BuildKeyMap(); BuildUi(); EnsureLoopMidi(); LoadOutputs();
            portTimer.Interval=1500;portTimer.Tick+=delegate{if(output==IntPtr.Zero||lastOutputCount!=(int)MidiOut.midiOutGetNumDevs())LoadOutputs();};portTimer.Start();
            KeyDown+=OnKeyDown; KeyUp+=OnKeyUp; FormClosing+=OnClosing; Deactivate+=delegate { ReleaseAll(); };
        }

        void BuildKeyMap()
        {
            Keys[] keys={Keys.A,Keys.W,Keys.S,Keys.E,Keys.D,Keys.F,Keys.T,Keys.G,Keys.Y,Keys.H,Keys.U,Keys.J,Keys.K,Keys.O,Keys.L,Keys.P,Keys.OemSemicolon};
            for(int i=0;i<keys.Length;i++) keyMap[keys[i]]=i;
        }

        Label Label(string text,float size,FontStyle style,Color color)
        { return new Label{Text=text,AutoSize=true,Font=new Font("Segoe UI",size,style),ForeColor=color,BackColor=Color.Transparent}; }
        Button Button(string text,Color back,Color fore,int w,int h)
        { Button b=new Button{Text=text,BackColor=back,ForeColor=fore,Width=w,Height=h,FlatStyle=FlatStyle.Flat,Font=new Font("Segoe UI",9,FontStyle.Bold),Cursor=Cursors.Hand,TabStop=false};b.FlatAppearance.BorderColor=Color.FromArgb(185,178,163);return b; }

        void BuildUi()
        {
            Label brand=Label("◼  MIDI KEYBOARD EMULATOR",11,FontStyle.Bold,Ink);brand.Location=new Point(30,25);Controls.Add(brand);
            connectionLabel=Label("●  CONNECTING TO LOOPMIDI…",9,FontStyle.Bold,Color.FromArgb(120,115,105));connectionLabel.Location=new Point(730,27);connectionLabel.Anchor=AnchorStyles.Top|AnchorStyles.Right;Controls.Add(connectionLabel);

            Panel hero=new Panel{Location=new Point(24,69),Size=new Size(932,532),Anchor=AnchorStyles.Top|AnchorStyles.Left|AnchorStyles.Right|AnchorStyles.Bottom,BackColor=Color.FromArgb(255,253,247),Padding=new Padding(30)};Controls.Add(hero);
            Label eye=Label("PLAY MIDI WITH YOUR COMPUTER KEYS",9,FontStyle.Bold,Teal);eye.Location=new Point(31,27);
            Label title=Label("Your keyboard is the keyboard.",30,FontStyle.Regular,Ink);title.Font=new Font("Georgia",30);title.Location=new Point(28,47);
            Label sub=Label("Use A–L for notes, W/E/T/Y/U for the black keys, and Space for sustain.",10,FontStyle.Regular,Color.FromArgb(105,106,99));sub.Location=new Point(33,98);
            hero.Controls.AddRange(new Control[]{eye,title,sub});

            Panel settings=new Panel{Location=new Point(33,137),Height=75,Width=hero.Width-66,Anchor=AnchorStyles.Top|AnchorStyles.Left|AnchorStyles.Right,BackColor=Color.FromArgb(247,242,230)};hero.Controls.Add(settings);
            Label outCap=Label("VIRTUAL MIDI DEVICE",8,FontStyle.Bold,Color.DimGray);outCap.Location=new Point(15,10);
            outputBox=new ComboBox{Location=new Point(14,31),Width=210,DropDownStyle=ComboBoxStyle.DropDownList,FlatStyle=FlatStyle.Flat};outputBox.SelectedIndexChanged+=OutputChanged;
            Label soundCap=Label("MIDI PROGRAM",8,FontStyle.Bold,Color.DimGray);soundCap.Location=new Point(245,10);
            soundBox=new ComboBox{Location=new Point(244,31),Width=165,DropDownStyle=ComboBoxStyle.DropDownList,FlatStyle=FlatStyle.Flat};
            soundBox.Items.AddRange(new object[]{"Grand Piano","Electric Piano","Vibraphone","Warm Organ","Strings","Synth Lead","Synth Pad","Round Bass"});soundBox.SelectedIndex=0;
            soundBox.SelectedIndexChanged+=delegate{int[] p={0,4,11,16,48,80,89,33};program=p[soundBox.SelectedIndex];Send(0xC0,program,0);};
            Label velCap=Label("VELOCITY",8,FontStyle.Bold,Color.DimGray);velCap.Location=new Point(430,10);
            velocityBar=new TrackBar{Location=new Point(424,28),Width=190,Minimum=20,Maximum=127,Value=100,TickStyle=TickStyle.None};
            velocityValue=Label("100",10,FontStyle.Bold,Ink);velocityValue.Location=new Point(610,33);velocityBar.ValueChanged+=delegate{velocity=velocityBar.Value;velocityValue.Text=velocity.ToString();};
            Button down=Button("−  OCTAVE",Color.White,Ink,100,45);down.Location=new Point(settings.Width-226,15);down.Anchor=AnchorStyles.Top|AnchorStyles.Right;down.Click+=delegate{ChangeOctave(-1);};
            octaveValue=Label("0",23,FontStyle.Regular,Teal);octaveValue.Font=new Font("Georgia",23);octaveValue.Size=new Size(45,45);octaveValue.TextAlign=ContentAlignment.MiddleCenter;octaveValue.Location=new Point(settings.Width-123,15);octaveValue.Anchor=AnchorStyles.Top|AnchorStyles.Right;
            Button up=Button("+  OCTAVE",Color.White,Ink,100,45);up.Location=new Point(settings.Width-101,15);up.Anchor=AnchorStyles.Top|AnchorStyles.Right;up.Click+=delegate{ChangeOctave(1);};
            settings.Controls.AddRange(new Control[]{outCap,outputBox,soundCap,soundBox,velCap,velocityBar,velocityValue,down,octaveValue,up});

            Panel map=new Panel{Location=new Point(33,226),Width=hero.Width-66,Height=63,Anchor=AnchorStyles.Top|AnchorStyles.Left|AnchorStyles.Right,BackColor=Dark};hero.Controls.Add(map);
            string[] labels={"A\nC","W\nC♯","S\nD","E\nD♯","D\nE","F\nF","T\nF♯","G\nG","Y\nG♯","H\nA","U\nA♯","J\nB","K\nC"};
            for(int i=0;i<labels.Length;i++){Label k=Label(labels[i],8,FontStyle.Bold,(i==1||i==3||i==6||i==8||i==10)?Gold:Color.White);k.TextAlign=ContentAlignment.MiddleCenter;k.AutoSize=false;k.Size=new Size(49,45);k.Location=new Point(12+i*52,9);k.BorderStyle=BorderStyle.FixedSingle;map.Controls.Add(k);}
            sustainLabel=Label("SPACE  ·  SUSTAIN",9,FontStyle.Bold,Color.White);sustainLabel.Location=new Point(map.Width-161,23);sustainLabel.Anchor=AnchorStyles.Top|AnchorStyles.Right;map.Controls.Add(sustainLabel);

            piano=new Piano{Location=new Point(33,303),Width=hero.Width-66,Height=170,Anchor=AnchorStyles.Top|AnchorStyles.Left|AnchorStyles.Right};piano.NoteChanged+=MouseNote;hero.Controls.Add(piano);
            noteLabel=Label("Press a key to play",10,FontStyle.Bold,Color.FromArgb(105,106,99));noteLabel.Location=new Point(35,488);hero.Controls.Add(noteLabel);
            Label help=Label("Z / X  OCTAVE     C / V  VELOCITY     SPACE  SUSTAIN",8,FontStyle.Bold,Color.FromArgb(135,132,122));help.Location=new Point(hero.Width-help.PreferredWidth-35,489);help.Anchor=AnchorStyles.Top|AnchorStyles.Right;hero.Controls.Add(help);
        }

        void EnsureLoopMidi()
        {
            try
            {
                if(Process.GetProcessesByName("loopMIDI").Length>0)return;
                string path=Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)+"\\Tobias Erichsen\\loopMIDI\\loopMIDI.exe";
                if(System.IO.File.Exists(path))Process.Start(path);
            }catch{}
        }
        void LoadOutputs()
        {
            int target=-1;outputBox.SelectedIndexChanged-=OutputChanged;outputBox.Items.Clear();outputIds.Clear();
            uint count=MidiOut.midiOutGetNumDevs();lastOutputCount=(int)count;for(uint i=0;i<count;i++)
            {
                MidiOut.Caps c;if(MidiOut.midiOutGetDevCaps(i,out c,(uint)Marshal.SizeOf(typeof(MidiOut.Caps)))!=0)continue;
                if(c.name.IndexOf("Microsoft GS Wavetable",StringComparison.OrdinalIgnoreCase)>=0)continue;
                outputBox.Items.Add(c.name);outputIds.Add(i);
                if(string.Equals(c.name,"MIDI Keyboard Emulator",StringComparison.OrdinalIgnoreCase))target=outputBox.Items.Count-1;
            }
            if(target<0){outputBox.Items.Insert(0,"Waiting for MIDI Keyboard Emulator…");outputIds.Insert(0,0xFFFFFFFE);target=0;}
            outputBox.SelectedIndexChanged+=OutputChanged;outputBox.SelectedIndex=target;OpenOutput(target);
        }
        void OutputChanged(object sender,EventArgs e){OpenOutput(outputBox.SelectedIndex);}
        void OpenOutput(int index)
        {
            ReleaseAll();if(output!=IntPtr.Zero){MidiOut.midiOutReset(output);MidiOut.midiOutClose(output);output=IntPtr.Zero;}
            if(index>=0&&index<outputIds.Count&&outputIds[index]!=0xFFFFFFFE)MidiOut.midiOutOpen(out output,outputIds[index],IntPtr.Zero,IntPtr.Zero,0);
            if(output!=IntPtr.Zero){connectionLabel.Text="●  VIRTUAL DEVICE CONNECTED";connectionLabel.ForeColor=Teal;Send(0xC0,program,0);}
            else{connectionLabel.Text="●  WAITING FOR LOOPMIDI";connectionLabel.ForeColor=Orange;}
        }
        void Send(int status,int d1,int d2){if(output!=IntPtr.Zero)MidiOut.midiOutShortMsg(output,MidiOut.Message(status,d1,d2));}
        int Adjust(int semitone){return Math.Max(0,Math.Min(127,60+octave*12+semitone));}
        string NoteName(int note){string[] n={"C","C♯","D","D♯","E","F","F♯","G","G♯","A","A♯","B"};return n[note%12]+(note/12-1);}

        void OnKeyDown(object sender,KeyEventArgs e)
        {
            if(e.KeyCode==Keys.Z&&!keysDown.Contains(e.KeyCode)){keysDown.Add(e.KeyCode);ChangeOctave(-1);e.Handled=true;return;}
            if(e.KeyCode==Keys.X&&!keysDown.Contains(e.KeyCode)){keysDown.Add(e.KeyCode);ChangeOctave(1);e.Handled=true;return;}
            if(e.KeyCode==Keys.C&&!keysDown.Contains(e.KeyCode)){keysDown.Add(e.KeyCode);velocityBar.Value=Math.Max(20,velocityBar.Value-10);e.Handled=true;return;}
            if(e.KeyCode==Keys.V&&!keysDown.Contains(e.KeyCode)){keysDown.Add(e.KeyCode);velocityBar.Value=Math.Min(127,velocityBar.Value+10);e.Handled=true;return;}
            if(e.KeyCode==Keys.Space&&!keysDown.Contains(e.KeyCode)){keysDown.Add(e.KeyCode);sustain=true;Send(0xB0,64,127);sustainLabel.ForeColor=Gold;e.Handled=true;return;}
            if(keyMap.ContainsKey(e.KeyCode)&&!keysDown.Contains(e.KeyCode))
            {keysDown.Add(e.KeyCode);int note=Adjust(keyMap[e.KeyCode]);sounding[e.KeyCode]=note;StartNote(note);e.Handled=true;}
        }
        void OnKeyUp(object sender,KeyEventArgs e)
        {
            keysDown.Remove(e.KeyCode);
            if(e.KeyCode==Keys.Space){sustain=false;Send(0xB0,64,0);foreach(int n in sustained)StopNote(n);sustained.Clear();sustainLabel.ForeColor=Color.White;e.Handled=true;return;}
            if(sounding.ContainsKey(e.KeyCode)){int note=sounding[e.KeyCode];sounding.Remove(e.KeyCode);if(sustain)sustained.Add(note);else StopNote(note);e.Handled=true;}
        }
        void MouseNote(int absolute,bool on){int note=Math.Max(0,Math.Min(127,absolute+octave*12));if(on)StartNote(note);else if(sustain)sustained.Add(note);else StopNote(note);}
        void StartNote(int note){Send(0x90,note,velocity);piano.Active.Add(Normalize(note));piano.Invalidate();noteLabel.Text="Playing  "+NoteName(note)+"   ·   velocity "+velocity;}
        void StopNote(int note){Send(0x80,note,0);piano.Active.Remove(Normalize(note));piano.Invalidate();if(sounding.Count==0)noteLabel.Text="Press a key to play";}
        int Normalize(int note){while(note<piano.BaseNote)note+=12;while(note>piano.BaseNote+36)note-=12;return note;}
        void ChangeOctave(int delta){ReleaseAll();octave=Math.Max(-3,Math.Min(3,octave+delta));octaveValue.Text=octave>0?"+"+octave:octave.ToString();}
        void ReleaseAll(){Send(0xB0,123,0);Send(0xB0,64,0);keysDown.Clear();sounding.Clear();sustained.Clear();sustain=false;if(piano!=null){piano.Active.Clear();piano.Invalidate();}if(noteLabel!=null)noteLabel.Text="Press a key to play";}
        void OnClosing(object sender,FormClosingEventArgs e){portTimer.Stop();ReleaseAll();if(output!=IntPtr.Zero){MidiOut.midiOutReset(output);MidiOut.midiOutClose(output);output=IntPtr.Zero;}}
    }
}
