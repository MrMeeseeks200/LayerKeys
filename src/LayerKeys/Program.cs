using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using NAudio.Wave;

namespace LayerKeys
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            AppDomain.CurrentDomain.AssemblyResolve += delegate(object sender, ResolveEventArgs args)
            {
                if (!args.Name.StartsWith("NAudio,")) return null;
                using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("NAudio.dll"))
                {
                    if (stream == null) return null;
                    byte[] bytes = new byte[stream.Length]; stream.Read(bytes, 0, bytes.Length); return Assembly.Load(bytes);
                }
            };
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }

    internal static class Midi
    {
        public delegate void MidiInProc(IntPtr handle, uint message, IntPtr instance, IntPtr param1, IntPtr param2);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        public struct MidiInCaps
        {
            public ushort manufacturerId, productId;
            public uint driverVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string name;
            public uint support;
        }

        [DllImport("winmm.dll")] public static extern uint midiInGetNumDevs();
        [DllImport("winmm.dll", CharSet = CharSet.Auto)] public static extern uint midiInGetDevCaps(uint id, out MidiInCaps caps, uint size);
        [DllImport("winmm.dll")] public static extern uint midiInOpen(out IntPtr handle, uint id, MidiInProc callback, IntPtr instance, uint flags);
        [DllImport("winmm.dll")] public static extern uint midiInStart(IntPtr handle);
        [DllImport("winmm.dll")] public static extern uint midiInStop(IntPtr handle);
        [DllImport("winmm.dll")] public static extern uint midiInReset(IntPtr handle);
        [DllImport("winmm.dll")] public static extern uint midiInClose(IntPtr handle);
        [DllImport("winmm.dll")] public static extern uint midiOutOpen(out IntPtr handle, uint id, IntPtr callback, IntPtr instance, uint flags);
        [DllImport("winmm.dll")] public static extern uint midiOutShortMsg(IntPtr handle, uint message);
        [DllImport("winmm.dll")] public static extern uint midiOutReset(IntPtr handle);
        [DllImport("winmm.dll")] public static extern uint midiOutClose(IntPtr handle);

        public const uint CallbackFunction = 0x00030000;
        public const uint MidiMapper = 0xFFFFFFFF;
        public const uint DataMessage = 0x3C3;

        public static uint Message(int status, int data1, int data2)
        {
            return (uint)(status | (data1 << 8) | (data2 << 16));
        }
    }

    internal sealed class NoteEvent
    {
        public int Note, Velocity, Start, Duration;
    }

    internal sealed class Track
    {
        public string Name;
        public int ColorIndex;
        public int StartBar;
        public int StartMs;
        public int LengthMs;
        public int TrimStartMs;
        public int TrimEndMs;
        public bool Muted;
        public readonly List<NoteEvent> Events = new List<NoteEvent>();
    }

    internal sealed class PendingNote
    {
        public int Note, Velocity, Start;
    }

    internal sealed class ScheduledOff
    {
        public int Note;
        public long Due;
    }

    internal sealed class MidiFileEvent
    {
        public long Tick;
        public bool NoteOn;
        public int Note, Velocity;
    }

    internal sealed class TimedMidiEvent
    {
        public int At, Note, Velocity;
        public bool NoteOn;
    }

    internal sealed class PianoControl : Control
    {
        public readonly HashSet<int> ActiveNotes = new HashSet<int>();
        public PianoControl()
        {
            DoubleBuffered = true;
            SetStyle(ControlStyles.ResizeRedraw, true);
            Height = 150;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.Clear(Color.FromArgb(35, 38, 35));
            int whites = 15;
            float w = (Width - 10f) / whites;
            int[] whiteNotes = {48,50,52,53,55,57,59,60,62,64,65,67,69,71,72};
            for (int i = 0; i < whites; i++)
            {
                RectangleF r = new RectangleF(5 + i * w, 4, w - 2, Height - 12);
                Color fill = ActiveNotes.Contains(whiteNotes[i]) ? Color.FromArgb(230, 119, 91) : Color.FromArgb(250, 247, 238);
                using (SolidBrush b = new SolidBrush(fill)) e.Graphics.FillRectangle(b, r);
                using (Pen p = new Pen(Color.FromArgb(170, 165, 153))) e.Graphics.DrawRectangle(p, r.X, r.Y, r.Width, r.Height);
            }
            int[] blackAfter = {0,1,3,4,5,7,8,10,11,12};
            int[] blackNotes = {49,51,54,56,58,61,63,66,68,70};
            for (int i = 0; i < blackAfter.Length; i++)
            {
                float x = 5 + (blackAfter[i] + 1) * w - w * .31f;
                RectangleF r = new RectangleF(x, 4, w * .62f, Height * .59f);
                Color fill = ActiveNotes.Contains(blackNotes[i]) ? Color.FromArgb(47, 116, 107) : Color.FromArgb(31, 32, 29);
                using (SolidBrush b = new SolidBrush(fill)) e.Graphics.FillRectangle(b, r);
            }
        }
    }

    internal sealed class ArrangementView : Control
    {
        public IList<Track> Tracks;
        public int DefaultDurationMs = 9600;
        public int BarDurationMs = 2400;
        public int TotalBars = 8;
        public double PlayheadBar;
        public Action<Track> TrackMoved;
        public Action<Track> ToggleMute;
        public Action<Track> DeleteTrack;
        public Action SelectionChanged;
        public Track SelectedTrack;
        public int SelectedTimeMs;
        Track dragTrack;
        double dragOffsetBars;
        const int HeaderWidth = 132;
        const int RulerHeight = 28;
        const int LaneHeight = 54;

        public ArrangementView()
        {
            DoubleBuffered = true;
            SetStyle(ControlStyles.ResizeRedraw, true);
            BackColor = Color.FromArgb(27, 35, 32);
            Cursor = Cursors.Default;
            MouseDown += Down;
            MouseMove += MoveClip;
            MouseUp += Up;
            MouseLeave += delegate { if (dragTrack == null) Cursor = Cursors.Default; };
        }

        float BarWidth { get { return Math.Max(18f, (Width - HeaderWidth - 12f) / Math.Max(8, TotalBars)); } }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.Clear(BackColor);
            int visibleBars = Math.Max(8, TotalBars);
            float bw = BarWidth;
            using (SolidBrush ruler = new SolidBrush(Color.FromArgb(34, 44, 40))) e.Graphics.FillRectangle(ruler, 0, 0, Width, RulerHeight);
            using (Pen grid = new Pen(Color.FromArgb(56, 69, 64)))
            using (SolidBrush tick = new SolidBrush(Color.FromArgb(153, 168, 160)))
            using (Font tiny = new Font("Segoe UI", 7, FontStyle.Bold))
            {
                for (int bar = 0; bar <= visibleBars; bar++)
                {
                    float x = HeaderWidth + bar * bw;
                    e.Graphics.DrawLine(grid, x, RulerHeight, x, Height);
                    if (bar < visibleBars) e.Graphics.DrawString((bar + 1).ToString(), tiny, tick, x + 4, 7);
                }
            }
            using (Pen lane = new Pen(Color.FromArgb(63, 75, 70)))
                for (int i = 0; i <= Math.Max(1, Tracks == null ? 0 : Tracks.Count); i++) e.Graphics.DrawLine(lane, 0, RulerHeight + i * LaneHeight, Width, RulerHeight + i * LaneHeight);

            if (Tracks == null || Tracks.Count == 0)
            {
                using (SolidBrush b = new SolidBrush(Color.FromArgb(151, 164, 157)))
                using (Font f = new Font("Segoe UI", 9)) e.Graphics.DrawString("♪   Record a take and its clip will appear here", f, b, HeaderWidth + 20, RulerHeight + 19);
                return;
            }

            Color[] colors = { Color.FromArgb(229,103,79), Color.FromArgb(46,114,106), Color.FromArgb(232,185,77), Color.FromArgb(129,106,168), Color.FromArgb(94,143,177) };
            using (Font nameFont = new Font("Segoe UI", 8, FontStyle.Bold))
            using (Font metaFont = new Font("Segoe UI", 7))
            {
                for (int i = 0; i < Tracks.Count; i++)
                {
                    Track track = Tracks[i]; int y = RulerHeight + i * LaneHeight;
                    using (SolidBrush head = new SolidBrush(Color.FromArgb(42, 53, 49))) e.Graphics.FillRectangle(head, 0, y + 1, HeaderWidth - 1, LaneHeight - 1);
                    using (SolidBrush text = new SolidBrush(Color.White)) e.Graphics.DrawString(track.Name, nameFont, text, 10, y + 8);
                    int trackDuration = track.LengthMs > 0 ? track.LengthMs : DefaultDurationMs;
                    using (SolidBrush meta = new SolidBrush(Color.FromArgb(153,168,160))) e.Graphics.DrawString("at " + (Math.Max(0, track.StartMs + track.TrimStartMs) / 1000.0).ToString("0.00") + "s", metaFont, meta, 10, y + 29);
                    DrawSmallButton(e.Graphics, new Rectangle(83, y + 14, 21, 23), track.Muted ? "ON" : "M", track.Muted ? Color.FromArgb(229,103,79) : Color.FromArgb(190,201,195));
                    DrawSmallButton(e.Graphics, new Rectangle(107, y + 14, 21, 23), "×", Color.FromArgb(190,201,195));

                    int trimEnd = track.TrimEndMs > 0 ? track.TrimEndMs : trackDuration;
                    float trimStartBars = track.TrimStartMs / (float)Math.Max(1, BarDurationMs);
                    float trimLengthBars = Math.Max(.05f, (trimEnd - track.TrimStartMs) / (float)Math.Max(1, BarDurationMs));
                    float startBars = track.StartMs / (float)Math.Max(1, BarDurationMs);
                    RectangleF clip = new RectangleF(HeaderWidth + (startBars + trimStartBars) * bw + 2, y + 7, Math.Max(8, trimLengthBars * bw - 4), LaneHeight - 14);
                    Color c = colors[track.ColorIndex % colors.Length];
                    using (SolidBrush fill = new SolidBrush(Color.FromArgb(track.Muted ? 75 : 185, c))) e.Graphics.FillRectangle(fill, clip);
                    using (Pen edge = new Pen(Color.FromArgb(220, c), 1.3f)) e.Graphics.DrawRectangle(edge, clip.X, clip.Y, clip.Width, clip.Height);
                    float middle = clip.Y + clip.Height / 2f;
                    using (Pen wave = new Pen(Color.FromArgb(track.Muted ? 100 : 235, Color.White), 1.4f))
                    {
                        foreach (NoteEvent note in track.Events)
                        {
                            float x = HeaderWidth + (startBars + note.Start / (float)Math.Max(1, BarDurationMs)) * bw;
                            float noteWidth = Math.Max(2, (float)note.Duration / Math.Max(1, BarDurationMs) * bw);
                            if (x < clip.Left || x > clip.Right) continue;
                            float amp = 3 + (note.Velocity / 127f) * (clip.Height * .38f);
                            e.Graphics.DrawLine(wave, x, middle - amp, x, middle + amp);
                            e.Graphics.DrawLine(wave, x, middle - amp, Math.Min(clip.Right - 1, x + noteWidth), middle - amp * .55f);
                            e.Graphics.DrawLine(wave, x, middle + amp, Math.Min(clip.Right - 1, x + noteWidth), middle + amp * .55f);
                        }
                    }
                    if (track == SelectedTrack)
                    {
                        float sx = HeaderWidth + (startBars + SelectedTimeMs / (float)Math.Max(1, BarDurationMs)) * bw;
                        using (Pen marker = new Pen(Color.White, 2)) e.Graphics.DrawLine(marker, sx, clip.Top - 3, sx, clip.Bottom + 3);
                        using (SolidBrush marker = new SolidBrush(Color.White)) e.Graphics.FillPolygon(marker, new PointF[] { new PointF(sx - 4, clip.Top - 4), new PointF(sx + 4, clip.Top - 4), new PointF(sx, clip.Top + 1) });
                    }
                }
            }
            if (PlayheadBar >= 0)
            {
                float px = HeaderWidth + (float)PlayheadBar * bw;
                using (Pen p = new Pen(Color.FromArgb(242,170,70), 2)) e.Graphics.DrawLine(p, px, 0, px, Height);
            }
        }

        void DrawSmallButton(Graphics g, Rectangle r, string text, Color color)
        {
            using (Pen p = new Pen(Color.FromArgb(85, 99, 93))) g.DrawRectangle(p, r);
            using (SolidBrush b = new SolidBrush(color))
            using (Font f = new Font("Segoe UI", 7, FontStyle.Bold))
            using (StringFormat sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center }) g.DrawString(text, f, b, r, sf);
        }

        void Down(object sender, MouseEventArgs e)
        {
            if (Tracks == null || e.Y < RulerHeight) return;
            int index = (e.Y - RulerHeight) / LaneHeight;
            if (index < 0 || index >= Tracks.Count) return;
            Track track = Tracks[index]; int y = RulerHeight + index * LaneHeight;
            if (new Rectangle(83, y + 14, 21, 23).Contains(e.Location)) { if (ToggleMute != null) ToggleMute(track); return; }
            if (new Rectangle(107, y + 14, 21, 23).Contains(e.Location)) { if (DeleteTrack != null) DeleteTrack(track); return; }
            float bw = BarWidth;
            int trackDuration = track.LengthMs > 0 ? track.LengthMs : DefaultDurationMs;
            int trimEnd = track.TrimEndMs > 0 ? track.TrimEndMs : trackDuration;
            float trimStartBars = track.TrimStartMs / (float)Math.Max(1, BarDurationMs);
            float trimLengthBars = Math.Max(.05f, (trimEnd - track.TrimStartMs) / (float)Math.Max(1, BarDurationMs));
            float startBars = track.StartMs / (float)Math.Max(1, BarDurationMs);
            RectangleF clip = new RectangleF(HeaderWidth + (startBars + trimStartBars) * bw + 2, y + 7, Math.Max(8, trimLengthBars * bw - 4), LaneHeight - 14);
            if (clip.Contains(e.Location))
            {
                SelectedTrack = track;
                SelectedTimeMs = Math.Max(track.TrimStartMs, Math.Min(trimEnd, (int)(((e.X - HeaderWidth) / bw - startBars) * BarDurationMs)));
                if (SelectionChanged != null) SelectionChanged();
                dragTrack = track; dragOffsetBars = (e.X - HeaderWidth) / bw - startBars; Cursor = Cursors.SizeWE; Capture = true; Invalidate();
            }
        }

        void MoveClip(object sender, MouseEventArgs e)
        {
            if (dragTrack == null) return;
            double newBars = (e.X - HeaderWidth) / BarWidth - dragOffsetBars;
            int newStartMs = Math.Max(-dragTrack.TrimStartMs, Math.Min(64 * BarDurationMs, (int)Math.Round(newBars * BarDurationMs)));
            if (newStartMs != dragTrack.StartMs)
            {
                dragTrack.StartMs = newStartMs;
                dragTrack.StartBar = (int)Math.Floor(newStartMs / (double)Math.Max(1, BarDurationMs));
                int visibleEnd = dragTrack.TrimEndMs > 0 ? dragTrack.TrimEndMs : (dragTrack.LengthMs > 0 ? dragTrack.LengthMs : DefaultDurationMs);
                TotalBars = Math.Max(TotalBars, (int)Math.Ceiling((newStartMs + visibleEnd) / (double)Math.Max(1, BarDurationMs)));
                Invalidate();
            }
        }

        void Up(object sender, MouseEventArgs e)
        {
            if (dragTrack == null) return;
            Track moved = dragTrack; dragTrack = null; Capture = false; Cursor = Cursors.Default;
            if (TrackMoved != null) TrackMoved(moved);
        }
    }

    internal sealed class RoundedPanel : Panel
    {
        public int Radius = 22;
        public Color BorderColor = Color.Transparent;
        public RoundedPanel() { DoubleBuffered = true; }
        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            using (GraphicsPath p = Path()) Region = new Region(p);
        }
        GraphicsPath Path()
        {
            int d = Radius * 2;
            GraphicsPath p = new GraphicsPath();
            p.AddArc(0, 0, d, d, 180, 90); p.AddArc(Width-d-1, 0, d, d, 270, 90);
            p.AddArc(Width-d-1, Height-d-1, d, d, 0, 90); p.AddArc(0, Height-d-1, d, d, 90, 90);
            p.CloseFigure(); return p;
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (BorderColor != Color.Transparent)
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using (GraphicsPath p = Path()) using (Pen pen = new Pen(BorderColor)) e.Graphics.DrawPath(pen, p);
            }
        }
    }

    internal sealed class MainForm : Form
    {
        readonly Color Cream = Color.FromArgb(243, 239, 229);
        readonly Color Paper = Color.FromArgb(255, 253, 247);
        readonly Color Ink = Color.FromArgb(32, 34, 31);
        readonly Color Muted = Color.FromArgb(111, 112, 105);
        readonly Color Coral = Color.FromArgb(229, 103, 79);
        readonly Color Teal = Color.FromArgb(46, 114, 106);
        readonly Color Dark = Color.FromArgb(41, 51, 48);
        readonly Color Mustard = Color.FromArgb(232, 185, 77);

        IntPtr midiIn = IntPtr.Zero, midiOut = IntPtr.Zero;
        Midi.MidiInProc midiCallback;
        string connectedDevice = "", midiDeviceSignature = "";
        int connectedDeviceIndex = -1, octave = 0, tempo = 100, bars = 0, program = 0;
        int recordingStartMs;
        bool playing, recording, refreshingMidiDevices;
        bool exporting, abortExportRequested;
        long transportStart, lastElapsed;
        readonly Stopwatch clock = Stopwatch.StartNew();
        readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        readonly System.Windows.Forms.Timer deviceTimer = new System.Windows.Forms.Timer();
        readonly List<Track> tracks = new List<Track>();
        readonly Dictionary<int, PendingNote> pending = new Dictionary<int, PendingNote>();
        readonly List<ScheduledOff> scheduledOffs = new List<ScheduledOff>();
        readonly HashSet<int> liveOutputNotes = new HashSet<int>();

        Label connectionLabel, octaveLabel, statusLabel, barLabel, hintLabel;
        Button recordButton, playButton, stopButton, splitButton, clearButton, saveButton, loadButton, exportWavButton, exportMp3Button, exportMidiButton;
        ComboBox soundBox, barsBox, recordModeBox, midiInputBox;
        NumericUpDown tempoBox;
        PianoControl piano;
        ArrangementView arrangement;
        Panel trackList, progressFill, headerPanel, scrollPanel, exportOverlay;
        Label exportInfoLabel;
        Button abortExportButton;

        public MainForm()
        {
            Text = "Layer Keys";
            Icon = SystemIcons.Application;
            StartPosition = FormStartPosition.CenterScreen;
            WindowState = FormWindowState.Maximized;
            MinimumSize = new Size(860, 690);
            ClientSize = new Size(1040, 790);
            BackColor = Cream;
            Font = new Font("Segoe UI", 9F);
            KeyPreview = true;
            AutoScaleMode = AutoScaleMode.Dpi;
            BuildInterface();

            midiCallback = MidiInput;
            Midi.midiOutOpen(out midiOut, Midi.MidiMapper, IntPtr.Zero, IntPtr.Zero, 0);
            SetProgram(program);
            RefreshMidiDevice();

            timer.Interval = 10;
            timer.Tick += TransportTick;
            timer.Start();
            deviceTimer.Interval = 1800;
            deviceTimer.Tick += delegate { RefreshMidiDevice(); };
            deviceTimer.Start();
            FormClosing += HandleFormClosing;
            KeyDown += FormKeyDown;
        }

        void BuildInterface()
        {
            Panel header = headerPanel = new Panel { Dock = DockStyle.Top, Height = 70, BackColor = Cream, Padding = new Padding(20, 0, 20, 0) };
            Label brand = new Label { Text = "▮▮▮  LAYER KEYS", AutoSize = true, Font = new Font("Segoe UI", 12, FontStyle.Bold), ForeColor = Ink, Location = new Point(23, 25) };
            connectionLabel = new Label { Text = "●  Looking for a keyboard…", AutoSize = true, Font = new Font("Segoe UI", 9, FontStyle.Bold), ForeColor = Muted, Anchor = AnchorStyles.Top | AnchorStyles.Right };
            header.Controls.Add(brand); header.Controls.Add(connectionLabel);
            header.Resize += delegate { connectionLabel.Location = new Point(header.Width - connectionLabel.Width - 24, 27); };
            Controls.Add(header);

            Panel scroll = scrollPanel = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(24, 0, 24, 32), BackColor = Cream };
            Controls.Add(scroll); scroll.BringToFront();
            Panel stack = new Panel { Width = 970, Height = 853, Margin = Padding.Empty };
            scroll.Controls.Add(stack);
            scroll.Resize += delegate { stack.Width = Math.Max(780, scroll.ClientSize.Width - 49); };

            RoundedPanel play = new RoundedPanel { Height = 385, Width = stack.Width, BackColor = Paper, BorderColor = Color.FromArgb(220, 213, 200), Margin = new Padding(0, 0, 0, 18), Padding = new Padding(34) };
            play.Location = new Point(0, 0);
            stack.Controls.Add(play);
            stack.SizeChanged += delegate { play.Width = stack.Width; };

            Label eyebrow = MakeLabel("YOUR KEYBOARD, READY WHEN YOU ARE", 9, FontStyle.Bold, Teal);
            eyebrow.Location = new Point(36, 30); eyebrow.AutoSize = true;
            Label title = MakeLabel("Plug in. Play.", 37, FontStyle.Regular, Ink);
            title.Font = new Font("Georgia", 37, FontStyle.Regular); title.Location = new Point(32, 51); title.AutoSize = true;
            hintLabel = MakeLabel("Connect a MIDI keyboard and start playing. No setup, no menus.", 10, FontStyle.Regular, Muted);
            hintLabel.Location = new Point(38, 112); hintLabel.AutoSize = true;

            Label soundCaption = MakeLabel("SOUND", 8, FontStyle.Bold, Muted); soundCaption.Location = new Point(40, 154);
            soundBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(39, 173), Width = 175, FlatStyle = FlatStyle.Flat };
            soundBox.Items.AddRange(new object[] { "Grand Piano", "Electric Piano", "Warm Organ", "Soft Strings", "Synth Pad", "Round Bass" }); soundBox.SelectedIndex = 0;
            soundBox.SelectedIndexChanged += delegate { int[] choices = {0,4,16,48,89,33}; program = choices[soundBox.SelectedIndex]; SetProgram(program); };
            Label tempoCaption = MakeLabel("TEMPO", 8, FontStyle.Bold, Muted); tempoCaption.Location = new Point(235, 154);
            tempoBox = new NumericUpDown { Minimum = 50, Maximum = 180, Value = 100, Location = new Point(234, 173), Width = 86, BorderStyle = BorderStyle.FixedSingle };
            tempoBox.ValueChanged += delegate { tempo = (int)tempoBox.Value; RestartIfPlaying(); };
            Label barsCaption = MakeLabel("LOOP", 8, FontStyle.Bold, Muted); barsCaption.Location = new Point(340, 154);
            barsBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(339, 173), Width = 100, FlatStyle = FlatStyle.Flat };
            barsBox.Items.AddRange(new object[] { "No loop", "2 bars", "4 bars", "8 bars" }); barsBox.SelectedIndex = 0;
            barsBox.SelectedIndexChanged += delegate { int[] vals = {0,2,4,8}; bars = vals[barsBox.SelectedIndex]; barLabel.Text = "1 / " + TimelineBars(); RestartIfPlaying(); DrawTracks(); };
            Label inputCaption = MakeLabel("MIDI INPUT", 8, FontStyle.Bold, Muted); inputCaption.Location = new Point(460, 154);
            midiInputBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(459, 173), Width = 245, FlatStyle = FlatStyle.Flat };
            midiInputBox.SelectedIndexChanged += delegate { if (!refreshingMidiDevices && midiInputBox.Enabled && midiInputBox.SelectedIndex >= 0) ConnectMidiDevice(midiInputBox.SelectedIndex); };

            Button minus = MakeButton("−   OCTAVE", Paper, Ink, 108, 55); minus.Location = new Point(play.Width - 306, 52); minus.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            Button plus = MakeButton("+   OCTAVE", Paper, Ink, 108, 55); plus.Location = new Point(play.Width - 138, 52); plus.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            octaveLabel = MakeLabel("0", 30, FontStyle.Regular, Teal); octaveLabel.Font = new Font("Georgia", 30); octaveLabel.TextAlign = ContentAlignment.MiddleCenter; octaveLabel.Location = new Point(play.Width - 196, 52); octaveLabel.Size = new Size(50, 53); octaveLabel.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            minus.Click += delegate { ChangeOctave(-1); }; plus.Click += delegate { ChangeOctave(1); };
            Label shortcuts = MakeLabel("Z / X keys", 8, FontStyle.Regular, Muted); shortcuts.Location = new Point(play.Width - 197, 108); shortcuts.Anchor = AnchorStyles.Top | AnchorStyles.Right;

            piano = new PianoControl { Location = new Point(38, 225), Width = play.Width - 76, Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top };
            play.Controls.AddRange(new Control[] { eyebrow, title, hintLabel, soundCaption, soundBox, tempoCaption, tempoBox, barsCaption, barsBox, inputCaption, midiInputBox, minus, octaveLabel, plus, shortcuts, piano });

            RoundedPanel studio = new RoundedPanel { Height = 450, Width = stack.Width, BackColor = Dark, Margin = Padding.Empty, Padding = new Padding(34), Location = new Point(0, 403) };
            stack.Controls.Add(studio); stack.SizeChanged += delegate { studio.Width = stack.Width; };
            Label loopEye = MakeLabel("SIMPLE LOOP RECORDER", 9, FontStyle.Bold, Coral); loopEye.Location = new Point(36, 25);
            Label studioTitle = MakeLabel("Layer your music", 28, FontStyle.Regular, Color.White); studioTitle.Font = new Font("Georgia", 28); studioTitle.Location = new Point(33, 45); studioTitle.AutoSize = true;
            Label studioHelp = MakeLabel("Record a take, then drag its clip anywhere on the arrangement.", 9, FontStyle.Regular, Color.FromArgb(186, 198, 191)); studioHelp.Location = new Point(38, 88);
            studio.Controls.AddRange(new Control[] { loopEye, studioTitle, studioHelp });

            Label modeCaption = MakeLabel("NEW TRACK", 8, FontStyle.Bold, Color.FromArgb(186, 198, 191)); modeCaption.Location = new Point(studio.Width - 230, 26); modeCaption.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            recordModeBox = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(studio.Width - 232, 47), Width = 194, FlatStyle = FlatStyle.Flat, Anchor = AnchorStyles.Top | AnchorStyles.Right };
            recordModeBox.Items.AddRange(new object[] { "Layer on top", "Record after existing" }); recordModeBox.SelectedIndex = 0;
            studio.Controls.AddRange(new Control[] { modeCaption, recordModeBox });

            recordButton = MakeButton("●  RECORD A TRACK", Coral, Color.White, 172, 48); recordButton.Location = new Point(38, 119); recordButton.Click += delegate { ToggleRecording(); };
            playButton = MakeButton("▶  PLAY", Color.FromArgb(57, 69, 65), Color.White, 95, 48); playButton.Location = new Point(220, 119); playButton.Click += delegate { if (playing) StopTransport(); else StartTransport(); };
            stopButton = MakeButton("■  STOP", Color.FromArgb(57, 69, 65), Color.White, 95, 48); stopButton.Location = new Point(325, 119); stopButton.Click += delegate { StopTransport(); };
            splitButton = MakeButton("✂  SPLIT", Color.FromArgb(57, 69, 65), Color.White, 92, 48); splitButton.Location = new Point(430, 119); splitButton.Click += delegate { SplitSelectedTrack(); };
            statusLabel = MakeLabel("READY", 8, FontStyle.Bold, Color.FromArgb(186, 198, 191)); statusLabel.Location = new Point(535, 120);
            barLabel = MakeLabel("1 / 1", 8, FontStyle.Bold, Color.FromArgb(186, 198, 191)); barLabel.TextAlign = ContentAlignment.TopRight; barLabel.Location = new Point(studio.Width - 90, 120); barLabel.Size = new Size(50, 17); barLabel.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            Panel progress = new Panel { BackColor = Color.FromArgb(17, 24, 21), Location = new Point(535, 146), Width = studio.Width - 575, Height = 7, Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top };
            progressFill = new Panel { BackColor = Mustard, Width = 0, Height = 7 }; progress.Controls.Add(progressFill);
            studio.Controls.AddRange(new Control[] { recordButton, playButton, stopButton, splitButton, statusLabel, barLabel, progress });

            Label arrangeCaption = MakeLabel("ARRANGEMENT  ·  DRAG CLIPS TO MOVE THEM", 8, FontStyle.Bold, Color.FromArgb(151, 164, 157)); arrangeCaption.Location = new Point(39, 177); studio.Controls.Add(arrangeCaption);
            trackList = new Panel { Location = new Point(38, 199), Width = studio.Width - 76, Height = 190, Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top | AnchorStyles.Bottom, AutoScroll = true, BackColor = Color.FromArgb(27,35,32) };
            arrangement = new ArrangementView { Dock = DockStyle.Top, Height = 188, Tracks = tracks };
            arrangement.TrackMoved = delegate { DrawTracks(); RestartIfPlaying(); };
            arrangement.SelectionChanged = delegate { splitButton.BackColor = Color.FromArgb(70, 91, 84); };
            arrangement.ToggleMute = delegate(Track t) { t.Muted = !t.Muted; DrawTracks(); };
            arrangement.DeleteTrack = delegate(Track t) { tracks.Remove(t); DrawTracks(); AllNotesOff(); RestartIfPlaying(); };
            trackList.Controls.Add(arrangement); studio.Controls.Add(trackList);
            clearButton = MakeButton("CLEAR ALL", Color.Transparent, Color.FromArgb(174, 187, 180), 90, 28); clearButton.FlatAppearance.BorderSize = 0; clearButton.Location = new Point(36, 406); clearButton.Anchor = AnchorStyles.Left | AnchorStyles.Bottom; clearButton.Enabled = false;
            clearButton.Click += delegate { if (MessageBox.Show(this, "Clear every recorded track?", "Layer Keys", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes) { tracks.Clear(); DrawTracks(); StopTransport(); } };
            saveButton = MakeButton("SAVE PROJECT", Color.Transparent, Color.FromArgb(205, 215, 209), 112, 28); saveButton.FlatAppearance.BorderSize = 0; saveButton.Location = new Point(145, 406); saveButton.Anchor = AnchorStyles.Left | AnchorStyles.Bottom; saveButton.Click += delegate { SaveProject(); };
            loadButton = MakeButton("LOAD PROJECT", Color.Transparent, Color.FromArgb(205, 215, 209), 112, 28); loadButton.FlatAppearance.BorderSize = 0; loadButton.Location = new Point(260, 406); loadButton.Anchor = AnchorStyles.Left | AnchorStyles.Bottom; loadButton.Click += delegate { LoadProject(); };
            exportWavButton = MakeButton("EXPORT WAV", Color.Transparent, Color.FromArgb(205, 215, 209), 105, 28); exportWavButton.FlatAppearance.BorderSize = 0; exportWavButton.Location = new Point(385, 406); exportWavButton.Anchor = AnchorStyles.Left | AnchorStyles.Bottom; exportWavButton.Click += delegate { ExportWav(); };
            exportMp3Button = MakeButton("EXPORT MP3", Color.Transparent, Color.FromArgb(205, 215, 209), 105, 28); exportMp3Button.FlatAppearance.BorderSize = 0; exportMp3Button.Location = new Point(493, 406); exportMp3Button.Anchor = AnchorStyles.Left | AnchorStyles.Bottom; exportMp3Button.Click += delegate { ExportMp3(); };
            exportMidiButton = MakeButton("EXPORT MIDI", Color.Transparent, Color.FromArgb(205, 215, 209), 105, 28); exportMidiButton.FlatAppearance.BorderSize = 0; exportMidiButton.Location = new Point(601, 406); exportMidiButton.Anchor = AnchorStyles.Left | AnchorStyles.Bottom; exportMidiButton.Click += delegate { ExportMidi(); };
            studio.Controls.AddRange(new Control[] { clearButton, saveButton, loadButton, exportWavButton, exportMp3Button, exportMidiButton });
            scroll.Resize += delegate
            {
                int studioHeight = Math.Max(450, scroll.ClientSize.Height - 403);
                studio.Height = studioHeight; stack.Height = 403 + studioHeight;
                trackList.Height = Math.Max(190, studioHeight - 260);
                clearButton.Top = saveButton.Top = loadButton.Top = exportWavButton.Top = exportMp3Button.Top = exportMidiButton.Top = studioHeight - 43;
                DrawTracks();
            };
        }

        Label MakeLabel(string text, float size, FontStyle style, Color color)
        {
            return new Label { Text = text, AutoSize = true, Font = new Font("Segoe UI", size, style), ForeColor = color, BackColor = Color.Transparent };
        }

        Button MakeButton(string text, Color back, Color fore, int width, int height)
        {
            Button b = new Button { Text = text, BackColor = back, ForeColor = fore, Width = width, Height = height, FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand, Font = new Font("Segoe UI", 9, FontStyle.Bold), TabStop = false };
            b.FlatAppearance.BorderColor = Color.FromArgb(190, 185, 174);
            return b;
        }

        void RefreshMidiDevice()
        {
            int count = (int)Midi.midiInGetNumDevs();
            List<string> names = new List<string>();
            for (int i = 0; i < count; i++)
            {
                Midi.MidiInCaps caps; string name = "MIDI input " + (i + 1);
                if (Midi.midiInGetDevCaps((uint)i, out caps, (uint)Marshal.SizeOf(typeof(Midi.MidiInCaps))) == 0) name = caps.name;
                names.Add(name);
            }
            string signature = string.Join("\u001f", names.ToArray());
            if (signature == midiDeviceSignature && (count == 0 || midiIn != IntPtr.Zero)) return;
            midiDeviceSignature = signature; CloseMidiInput();
            refreshingMidiDevices = true; midiInputBox.Items.Clear();
            if (count <= 0)
            {
                connectedDevice = ""; connectedDeviceIndex = -1;
                midiInputBox.Items.Add("No MIDI inputs detected"); midiInputBox.SelectedIndex = 0; midiInputBox.Enabled = false; refreshingMidiDevices = false;
                connectionLabel.Text = "●  Plug in a MIDI keyboard";
                connectionLabel.ForeColor = Muted;
                return;
            }
            foreach (string name in names) midiInputBox.Items.Add(name);
            midiInputBox.Enabled = true;
            int selected = -1;
            if (!IsVirtualMidiPort(connectedDevice)) selected = names.IndexOf(connectedDevice);
            if (selected < 0) for (int i = 0; i < names.Count; i++) if (!IsVirtualMidiPort(names[i])) { selected = i; break; }
            if (selected < 0) selected = Math.Max(0, names.IndexOf(connectedDevice));
            midiInputBox.SelectedIndex = selected; refreshingMidiDevices = false;
            ConnectMidiDevice(selected);
        }

        bool IsVirtualMidiPort(string name)
        {
            string value = (name ?? "").ToLowerInvariant();
            return value.Contains("loopmidi") || value.Contains("midi keyboard emulator");
        }

        void ConnectMidiDevice(int index)
        {
            int count = (int)Midi.midiInGetNumDevs(); if (index < 0 || index >= count) return;
            CloseMidiInput();
            Midi.MidiInCaps caps; string name = "MIDI input " + (index + 1);
            if (Midi.midiInGetDevCaps((uint)index, out caps, (uint)Marshal.SizeOf(typeof(Midi.MidiInCaps))) == 0) name = caps.name;
            if (Midi.midiInOpen(out midiIn, (uint)index, midiCallback, IntPtr.Zero, Midi.CallbackFunction) == 0)
            {
                Midi.midiInStart(midiIn); connectedDevice = name; connectedDeviceIndex = index;
                connectionLabel.Text = "●  " + name; connectionLabel.ForeColor = Teal;
                hintLabel.Text = "Your keyboard is connected. Play a note.";
            }
            else { midiIn = IntPtr.Zero; connectedDeviceIndex = -1; connectionLabel.Text = "●  " + name + " is busy"; connectionLabel.ForeColor = Coral; }
        }

        void MidiInput(IntPtr handle, uint message, IntPtr instance, IntPtr param1, IntPtr param2)
        {
            if (message != Midi.DataMessage || IsDisposed || exporting) return;
            int data = param1.ToInt32();
            int status = data & 0xF0, note = (data >> 8) & 0x7F, velocity = (data >> 16) & 0x7F;
            try { BeginInvoke((MethodInvoker)delegate { HandleMidi(status, note, velocity); }); } catch { }
        }

        void HandleMidi(int status, int inputNote, int velocity)
        {
            if (status == 0x90 && velocity > 0) NoteOn(inputNote, velocity);
            else if (status == 0x80 || (status == 0x90 && velocity == 0)) NoteOff(inputNote);
        }

        void NoteOn(int inputNote, int velocity)
        {
            int note = Math.Max(0, Math.Min(127, inputNote + octave * 12));
            Send(0x90, note, velocity); liveOutputNotes.Add(note);
            int visual = note;
            while (visual < 48) visual += 12; while (visual > 72) visual -= 12;
            piano.ActiveNotes.Add(visual); piano.Invalidate();
            if (recording)
            {
                int raw = (int)Math.Max(0, Elapsed() - recordingStartMs);
                int pos = bars == 0 ? raw : Math.Min(LoopMs() - 1, raw);
                pending[inputNote] = new PendingNote { Note = note, Velocity = velocity, Start = pos };
            }
        }

        void NoteOff(int inputNote)
        {
            int note = Math.Max(0, Math.Min(127, inputNote + octave * 12));
            if (pending.ContainsKey(inputNote)) note = pending[inputNote].Note;
            Send(0x80, note, 0); liveOutputNotes.Remove(note);
            int visual = note; while (visual < 48) visual += 12; while (visual > 72) visual -= 12;
            piano.ActiveNotes.Remove(visual); piano.Invalidate();
            if (recording && pending.ContainsKey(inputNote))
            {
                PendingNote p = pending[inputNote]; int raw = (int)Math.Max(0, Elapsed() - recordingStartMs); int pos = bars == 0 ? raw : Math.Min(LoopMs(), raw); int duration = pos - p.Start;
                if (duration <= 0) duration = 40;
                CurrentTrack().Events.Add(new NoteEvent { Note = p.Note, Velocity = p.Velocity, Start = p.Start, Duration = Math.Max(40, duration) });
                pending.Remove(inputNote);
            }
        }

        Track CurrentTrack() { return tracks[tracks.Count - 1]; }
        int BarMs() { return 4 * 60000 / tempo; }
        int LoopMs() { return (bars > 0 ? bars : 1) * BarMs(); }
        int TimelineBars()
        {
            int total = bars > 0 ? bars : 1;
            foreach (Track t in tracks)
            {
                int end = t.TrimEndMs > 0 ? t.TrimEndMs : (t.LengthMs > 0 ? t.LengthMs : LoopMs());
                total = Math.Max(total, (int)Math.Ceiling((t.StartMs + end) / (double)Math.Max(1, BarMs())));
            }
            return total;
        }
        int TimelineMs()
        {
            int total = bars > 0 ? bars * BarMs() : 0;
            foreach (Track t in tracks)
            {
                int end = t.TrimEndMs > 0 ? t.TrimEndMs : (t.LengthMs > 0 ? t.LengthMs : LoopMs());
                total = Math.Max(total, t.StartMs + end);
            }
            return Math.Max(BarMs() / 8, total);
        }
        long Elapsed() { return clock.ElapsedMilliseconds - transportStart; }

        void StartTransport() { StartTransportAt(0); }
        void StartTransportAt(int positionMs)
        {
            AllNotesOff(); playing = true; transportStart = clock.ElapsedMilliseconds + 80 - positionMs; lastElapsed = positionMs - 1;
            playButton.Text = "❚❚  PAUSE"; statusLabel.Text = recording ? "RECORDING" : "PLAYING";
        }

        void StopTransport()
        {
            if (recording) FinishRecording();
            playing = false; AllNotesOff(); scheduledOffs.Clear();
            playButton.Text = "▶  PLAY"; statusLabel.Text = "READY"; progressFill.Width = 0; barLabel.Text = "1 / " + TimelineBars();
            if (arrangement != null) { arrangement.PlayheadBar = 0; arrangement.Invalidate(); }
        }

        void RestartIfPlaying() { if (playing) StartTransport(); }

        void ToggleRecording()
        {
            if (recording) { FinishRecording(); return; }
            int startMs = 0;
            if (recordModeBox.SelectedIndex == 1)
                foreach (Track existing in tracks)
                {
                    int end = existing.TrimEndMs > 0 ? existing.TrimEndMs : (existing.LengthMs > 0 ? existing.LengthMs : LoopMs());
                    startMs = Math.Max(startMs, existing.StartMs + end);
                }
            Track t = new Track { Name = "Track " + (tracks.Count + 1), ColorIndex = tracks.Count % 5, StartBar = startMs / Math.Max(1, BarMs()), StartMs = startMs, LengthMs = bars > 0 ? LoopMs() : BarMs() };
            tracks.Add(t); recording = true; pending.Clear();
            recordingStartMs = startMs;
            recordButton.Text = "●  FINISH TRACK"; recordButton.BackColor = Color.FromArgb(190, 73, 57);
            StartTransportAt(recordingStartMs); DrawTracks();
        }

        void FinishRecording()
        {
            if (!recording) return;
            int loop = bars == 0 ? Math.Max(100, (int)(Elapsed() - recordingStartMs)) : LoopMs();
            CurrentTrack().LengthMs = loop;
            foreach (PendingNote p in pending.Values)
                CurrentTrack().Events.Add(new NoteEvent { Note = p.Note, Velocity = p.Velocity, Start = p.Start, Duration = Math.Max(40, loop - p.Start) });
            pending.Clear(); recording = false;
            recordButton.Text = "●  RECORD A TRACK"; recordButton.BackColor = Coral;
            if (CurrentTrack().Events.Count == 0) tracks.RemoveAt(tracks.Count - 1);
            statusLabel.Text = playing ? "PLAYING" : "READY"; DrawTracks();
            if (bars == 0 && playing) { playing = false; AllNotesOff(); scheduledOffs.Clear(); playButton.Text = "▶  PLAY"; statusLabel.Text = "READY"; progressFill.Width = 0; }
        }

        void TransportTick(object sender, EventArgs e)
        {
            long nowClock = clock.ElapsedMilliseconds;
            for (int i = scheduledOffs.Count - 1; i >= 0; i--)
                if (scheduledOffs[i].Due <= nowClock) { Send(0x80, scheduledOffs[i].Note, 0); scheduledOffs.RemoveAt(i); }
            if (!playing) return;
            long elapsed = Elapsed(); if (elapsed < 0) return;
            if (recording && bars == 0)
            {
                CurrentTrack().LengthMs = Math.Max(BarMs() / 8, (int)(elapsed - recordingStartMs));
                arrangement.DefaultDurationMs = CurrentTrack().LengthMs; arrangement.TotalBars = Math.Max(8, TimelineBars()); arrangement.Invalidate();
            }
            int loop = TimelineMs();
            long from = lastElapsed; long to = elapsed;
            foreach (Track track in tracks)
            {
                if (track.Muted || (recording && track == CurrentTrack())) continue;
                foreach (NoteEvent ev in track.Events)
                {
                    long firstCycle = Math.Max(0, from / loop);
                    long lastCycle = to / loop;
                    for (long cycle = firstCycle; cycle <= lastCycle; cycle++)
                    {
                        long at = cycle * loop + track.StartMs + ev.Start;
                        if (at > from && at <= to)
                        {
                            Send(0x90, ev.Note, ev.Velocity);
                            scheduledOffs.Add(new ScheduledOff { Note = ev.Note, Due = nowClock + ev.Duration });
                        }
                    }
                }
            }
            lastElapsed = elapsed;
            int pos = recording && bars == 0 ? (int)Math.Min(elapsed, loop) : (int)(elapsed % loop);
            Panel progress = progressFill.Parent as Panel;
            if (progress != null) progressFill.Width = (int)(progress.Width * (pos / (double)loop));
            if (arrangement != null) { arrangement.PlayheadBar = pos / (double)Math.Max(1, BarMs()); arrangement.Invalidate(); }
            int totalBars = TimelineBars(); int bar = Math.Min(totalBars, pos / Math.Max(1, BarMs()) + 1); barLabel.Text = bar + " / " + totalBars;
            if (recording && bars > 0 && elapsed >= recordingStartMs + LoopMs()) FinishRecording();
            else if (!recording && bars == 0 && elapsed >= TimelineMs()) StopTransport();
        }

        void DrawTracks()
        {
            clearButton.Enabled = tracks.Count > 0;
            arrangement.Tracks = tracks;
            arrangement.BarDurationMs = BarMs();
            arrangement.DefaultDurationMs = LoopMs();
            arrangement.TotalBars = Math.Max(8, TimelineBars());
            arrangement.Height = Math.Max(trackList.ClientSize.Height - 2, 28 + Math.Max(1, tracks.Count) * 54);
            arrangement.Invalidate();
        }

        void SplitSelectedTrack()
        {
            Track source = arrangement.SelectedTrack;
            if (source == null) { MessageBox.Show(this, "Click the point in a clip where you want to split it, then press Split.", "Split a track", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            int split = arrangement.SelectedTimeMs;
            int end = source.TrimEndMs > 0 ? source.TrimEndMs : (source.LengthMs > 0 ? source.LengthMs : LoopMs());
            if (split <= source.TrimStartMs + 25 || split >= end - 25) { MessageBox.Show(this, "Choose a point inside the clip, away from its edges.", "Split a track", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            Track second = new Track { Name = "Track " + (tracks.Count + 1), ColorIndex = (source.ColorIndex + 1) % 5, StartBar = source.StartBar, StartMs = source.StartMs, LengthMs = source.LengthMs, TrimStartMs = split, TrimEndMs = end, Muted = source.Muted };
            List<NoteEvent> firstEvents = new List<NoteEvent>();
            foreach (NoteEvent note in source.Events)
            {
                int noteEnd = note.Start + note.Duration;
                if (note.Start >= split) second.Events.Add(new NoteEvent { Note = note.Note, Velocity = note.Velocity, Start = note.Start, Duration = note.Duration });
                else if (noteEnd > split)
                {
                    firstEvents.Add(new NoteEvent { Note = note.Note, Velocity = note.Velocity, Start = note.Start, Duration = split - note.Start });
                    second.Events.Add(new NoteEvent { Note = note.Note, Velocity = note.Velocity, Start = split, Duration = noteEnd - split });
                }
                else firstEvents.Add(note);
            }
            source.Events.Clear(); source.Events.AddRange(firstEvents); source.TrimEndMs = split;
            int index = tracks.IndexOf(source); tracks.Insert(index + 1, second);
            arrangement.SelectedTrack = null; splitButton.BackColor = Color.FromArgb(57, 69, 65);
            DrawTracks(); RestartIfPlaying();
        }

        void SaveProject()
        {
            using (SaveFileDialog dialog = new SaveFileDialog { Filter = "Layer Keys project (*.lkproj)|*.lkproj", DefaultExt = "lkproj", FileName = "My Music.lkproj" })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    using (BinaryWriter writer = new BinaryWriter(File.Create(dialog.FileName)))
                    {
                        writer.Write("LKSP4"); writer.Write(tempo); writer.Write(bars); writer.Write(soundBox.SelectedIndex); writer.Write(recordModeBox.SelectedIndex); writer.Write(tracks.Count);
                        foreach (Track track in tracks)
                        {
                            writer.Write(track.Name ?? "Track"); writer.Write(track.ColorIndex); writer.Write(track.StartBar); writer.Write(track.StartMs); writer.Write(track.LengthMs); writer.Write(track.TrimStartMs); writer.Write(track.TrimEndMs); writer.Write(track.Muted); writer.Write(track.Events.Count);
                            foreach (NoteEvent note in track.Events) { writer.Write(note.Note); writer.Write(note.Velocity); writer.Write(note.Start); writer.Write(note.Duration); }
                        }
                    }
                    statusLabel.Text = "PROJECT SAVED";
                }
                catch (Exception ex) { MessageBox.Show(this, "Could not save the project.\n\n" + ex.Message, "Layer Keys", MessageBoxButtons.OK, MessageBoxIcon.Error); }
            }
        }

        void LoadProject()
        {
            using (OpenFileDialog dialog = new OpenFileDialog { Filter = "Layer Keys project (*.lkproj)|*.lkproj" })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    StopTransport();
                    using (BinaryReader reader = new BinaryReader(File.OpenRead(dialog.FileName)))
                    {
                        string version = reader.ReadString(); bool version4 = version == "LKSP4", version3 = version == "LKSP3"; if (!version4 && !version3 && version != "LKSP2") throw new InvalidDataException("This is not a supported Layer Keys project.");
                        int savedTempo = reader.ReadInt32(), savedBars = reader.ReadInt32(), savedSound = reader.ReadInt32(), savedMode = reader.ReadInt32(), count = reader.ReadInt32();
                        tracks.Clear(); tempoBox.Value = Math.Max(50, Math.Min(180, savedTempo));
                        barsBox.SelectedIndex = savedBars == 0 ? 0 : savedBars == 2 ? 1 : savedBars == 8 ? 3 : 2;
                        soundBox.SelectedIndex = Math.Max(0, Math.Min(soundBox.Items.Count - 1, savedSound)); recordModeBox.SelectedIndex = Math.Max(0, Math.Min(1, savedMode));
                        for (int i = 0; i < count; i++)
                        {
                            Track track = new Track { Name = reader.ReadString(), ColorIndex = reader.ReadInt32(), StartBar = reader.ReadInt32() };
                            track.StartMs = version4 ? reader.ReadInt32() : track.StartBar * (4 * 60000 / Math.Max(1, savedTempo));
                            track.LengthMs = (version4 || version3) ? reader.ReadInt32() : savedBars * (4 * 60000 / Math.Max(1, savedTempo)); track.TrimStartMs = reader.ReadInt32(); track.TrimEndMs = reader.ReadInt32(); track.Muted = reader.ReadBoolean();
                            int notes = reader.ReadInt32(); for (int n = 0; n < notes; n++) track.Events.Add(new NoteEvent { Note = reader.ReadInt32(), Velocity = reader.ReadInt32(), Start = reader.ReadInt32(), Duration = reader.ReadInt32() });
                            tracks.Add(track);
                        }
                    }
                    arrangement.SelectedTrack = null; DrawTracks(); statusLabel.Text = "PROJECT LOADED";
                }
                catch (Exception ex) { MessageBox.Show(this, "Could not load the project.\n\n" + ex.Message, "Layer Keys", MessageBoxButtons.OK, MessageBoxIcon.Error); }
            }
        }

        void BeginExportUi(string message)
        {
            exporting = true; abortExportRequested = false; headerPanel.Enabled = false; scrollPanel.Enabled = false; ControlBox = false;
            if (exportOverlay == null)
            {
                exportOverlay = new Panel { Dock = DockStyle.Fill, BackColor = Dark };
                Label title = new Label { Text = "Exporting your music", AutoSize = true, ForeColor = Color.White, BackColor = Color.Transparent, Font = new Font("Georgia", 30, FontStyle.Regular) };
                exportInfoLabel = new Label { AutoSize = false, Size = new Size(560, 54), TextAlign = ContentAlignment.MiddleCenter, ForeColor = Color.FromArgb(190, 205, 197), BackColor = Color.Transparent, Font = new Font("Segoe UI", 11, FontStyle.Regular) };
                abortExportButton = MakeButton("ABORT EXPORT", Coral, Color.White, 190, 52);
                abortExportButton.Click += delegate { abortExportRequested = true; abortExportButton.Enabled = false; abortExportButton.Text = "ABORTING…"; exportInfoLabel.Text = "Stopping capture and removing the incomplete file…"; };
                exportOverlay.Controls.Add(title); exportOverlay.Controls.Add(exportInfoLabel); exportOverlay.Controls.Add(abortExportButton);
                exportOverlay.Resize += delegate
                {
                    title.Location = new Point((exportOverlay.Width - title.Width) / 2, Math.Max(90, exportOverlay.Height / 2 - 105));
                    exportInfoLabel.Location = new Point((exportOverlay.Width - exportInfoLabel.Width) / 2, title.Bottom + 14);
                    abortExportButton.Location = new Point((exportOverlay.Width - abortExportButton.Width) / 2, exportInfoLabel.Bottom + 24);
                };
                Controls.Add(exportOverlay);
            }
            abortExportButton.Enabled = true; abortExportButton.Text = "ABORT EXPORT"; exportInfoLabel.Text = message;
            exportOverlay.Visible = true; exportOverlay.BringToFront(); exportOverlay.Focus();
        }

        void EndExportUi()
        {
            exporting = false; abortExportRequested = false;
            if (exportOverlay != null) exportOverlay.Visible = false;
            headerPanel.Enabled = true; scrollPanel.Enabled = true; ControlBox = true;
        }

        void DeleteIncomplete(string fileName)
        {
            try { if (File.Exists(fileName)) File.Delete(fileName); } catch { }
        }

        void ExportWav()
        {
            if (tracks.Count == 0) { MessageBox.Show(this, "Record or load at least one track before exporting.", "Export WAV", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            using (SaveFileDialog dialog = new SaveFileDialog { Filter = "Wave audio (*.wav)|*.wav", DefaultExt = "wav", FileName = "Layer Keys Mix.wav" })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    BeginExportUi("Playing and capturing the Windows synthesizer…");
                    CaptureWindowsSynthWav(dialog.FileName);
                    statusLabel.Text = "WAV EXPORTED";
                }
                catch (OperationCanceledException) { DeleteIncomplete(dialog.FileName); statusLabel.Text = "EXPORT ABORTED"; }
                catch (Exception ex) { DeleteIncomplete(dialog.FileName); statusLabel.Text = "READY"; MessageBox.Show(this, "Could not export the audio.\n\n" + ex.Message, "Layer Keys", MessageBoxButtons.OK, MessageBoxIcon.Error); }
                finally { EndExportUi(); }
            }
        }

        void RenderWavFile(string fileName)
        {
            const int sampleRate = 44100; int frames = (TimelineMs() + 900) * sampleRate / 1000; double[] mix = new double[frames];
            foreach (Track track in tracks)
            {
                if (track.Muted) continue;
                foreach (NoteEvent note in track.Events)
                {
                    int start = (track.StartMs + note.Start) * sampleRate / 1000;
                    int length = Math.Max(1, (note.Duration + 180) * sampleRate / 1000); double frequency = 440.0 * Math.Pow(2, (note.Note - 69) / 12.0); double volume = note.Velocity / 127.0 * .22;
                    for (int i = 0; i < length && start + i < frames; i++)
                    {
                        double time = i / (double)sampleRate; double attack = Math.Min(1, i / (sampleRate * .012)); double release = i < note.Duration * sampleRate / 1000 ? 1 : Math.Max(0, 1 - (i - note.Duration * sampleRate / 1000.0) / (sampleRate * .18));
                        double sample = Math.Sin(2 * Math.PI * frequency * time) + .18 * Math.Sin(4 * Math.PI * frequency * time);
                        if (start + i >= 0) mix[start + i] += sample * volume * attack * release;
                    }
                }
            }
            using (BinaryWriter writer = new BinaryWriter(File.Create(fileName)))
            {
                int dataBytes = frames * 4; writer.Write(new char[] {'R','I','F','F'}); writer.Write(36 + dataBytes); writer.Write(new char[] {'W','A','V','E'}); writer.Write(new char[] {'f','m','t',' '}); writer.Write(16); writer.Write((short)1); writer.Write((short)2); writer.Write(sampleRate); writer.Write(sampleRate * 4); writer.Write((short)4); writer.Write((short)16); writer.Write(new char[] {'d','a','t','a'}); writer.Write(dataBytes);
                for (int i = 0; i < frames; i++) { short value = (short)(Math.Max(-1, Math.Min(1, mix[i])) * 32760); writer.Write(value); writer.Write(value); }
            }
        }

        void CaptureWindowsSynthWav(string fileName)
        {
            StopTransport(); AllNotesOff();
            statusLabel.Text = "EXPORTING AUDIO · PLAYS IN REAL TIME"; Application.DoEvents();
            List<TimedMidiEvent> events = new List<TimedMidiEvent>();
            foreach (Track track in tracks) if (!track.Muted) foreach (NoteEvent note in track.Events)
            {
                int at = track.StartMs + note.Start;
                if (at + note.Duration < 0) continue;
                at = Math.Max(0, at);
                events.Add(new TimedMidiEvent { At = at, Note = note.Note, Velocity = note.Velocity, NoteOn = true });
                events.Add(new TimedMidiEvent { At = at + Math.Max(40, note.Duration), Note = note.Note, Velocity = 0, NoteOn = false });
            }
            events.Sort(delegate(TimedMidiEvent a, TimedMidiEvent b) { int c = a.At.CompareTo(b.At); return c != 0 ? c : a.NoteOn.CompareTo(b.NoteOn); });

            Exception captureError = null; ManualResetEvent stopped = new ManualResetEvent(false); bool started = false;
            using (WasapiLoopbackCapture capture = new WasapiLoopbackCapture())
            using (WaveFileWriter writer = new WaveFileWriter(fileName, capture.WaveFormat))
            {
                capture.DataAvailable += delegate(object sender, WaveInEventArgs args) { if (args.BytesRecorded > 0) writer.Write(args.Buffer, 0, args.BytesRecorded); };
                capture.RecordingStopped += delegate(object sender, StoppedEventArgs args) { captureError = args.Exception; stopped.Set(); };
                try
                {
                    capture.StartRecording(); started = true; WaitWithMessages(350); SetProgram(program);
                    Stopwatch renderClock = Stopwatch.StartNew(); int eventIndex = 0; int duration = TimelineMs();
                    while (renderClock.ElapsedMilliseconds <= duration + 250)
                    {
                        if (abortExportRequested) throw new OperationCanceledException();
                        while (eventIndex < events.Count && events[eventIndex].At <= renderClock.ElapsedMilliseconds)
                        {
                            TimedMidiEvent item = events[eventIndex++]; Send(item.NoteOn ? 0x90 : 0x80, item.Note, item.Velocity);
                        }
                        Panel progress = progressFill.Parent as Panel;
                        if (progress != null) progressFill.Width = (int)(progress.Width * Math.Min(1, renderClock.ElapsedMilliseconds / (double)Math.Max(1, duration)));
                        if (exportInfoLabel != null) exportInfoLabel.Text = "Playing and capturing the Windows synthesizer…  " + Math.Min(duration, (int)renderClock.ElapsedMilliseconds) / 1000.0 + " / " + duration / 1000.0 + " seconds";
                        Application.DoEvents(); Thread.Sleep(2);
                    }
                    AllNotesOff(); WaitWithMessages(700);
                }
                finally
                {
                    AllNotesOff();
                    if (started) { capture.StopRecording(); while (!stopped.WaitOne(10)) Application.DoEvents(); }
                    progressFill.Width = 0;
                }
            }
            stopped.Dispose();
            if (captureError != null) throw new InvalidOperationException("Windows audio capture failed.", captureError);
        }

        void WaitWithMessages(int milliseconds)
        {
            Stopwatch wait = Stopwatch.StartNew();
            while (wait.ElapsedMilliseconds < milliseconds) { if (abortExportRequested) throw new OperationCanceledException(); Application.DoEvents(); Thread.Sleep(5); }
        }

        void ExportMp3()
        {
            if (tracks.Count == 0) { MessageBox.Show(this, "Record or load at least one track before exporting.", "Export MP3", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            using (SaveFileDialog dialog = new SaveFileDialog { Filter = "MP3 audio (*.mp3)|*.mp3", DefaultExt = "mp3", FileName = "Layer Keys Mix.mp3" })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                string tempWav = Path.Combine(Path.GetTempPath(), "LayerKeys-" + Guid.NewGuid().ToString("N") + ".wav");
                try
                {
                    string ffmpeg = FindFfmpeg();
                    if (ffmpeg == null) throw new FileNotFoundException("MP3 export needs FFmpeg. Install FFmpeg or use Export WAV instead.");
                    BeginExportUi("Playing and capturing the Windows synthesizer…"); CaptureWindowsSynthWav(tempWav);
                    exportInfoLabel.Text = "Encoding the captured audio as MP3…"; Application.DoEvents();
                    ProcessStartInfo info = new ProcessStartInfo(ffmpeg, "-y -hide_banner -loglevel error -i \"" + tempWav + "\" -codec:a libmp3lame -q:a 2 \"" + dialog.FileName + "\"");
                    info.UseShellExecute = false; info.CreateNoWindow = true;
                    using (Process process = Process.Start(info))
                    {
                        while (!process.HasExited) { Application.DoEvents(); if (abortExportRequested) { process.Kill(); process.WaitForExit(); throw new OperationCanceledException(); } Thread.Sleep(10); }
                        if (process.ExitCode != 0) throw new InvalidOperationException("FFmpeg could not create the MP3 file.");
                    }
                    statusLabel.Text = "MP3 EXPORTED";
                }
                catch (OperationCanceledException) { DeleteIncomplete(dialog.FileName); statusLabel.Text = "EXPORT ABORTED"; }
                catch (Exception ex) { DeleteIncomplete(dialog.FileName); statusLabel.Text = "READY"; MessageBox.Show(this, "Could not export the MP3.\n\n" + ex.Message, "Layer Keys", MessageBoxButtons.OK, MessageBoxIcon.Error); }
                finally { DeleteIncomplete(tempWav); EndExportUi(); }
            }
        }

        string FindFfmpeg()
        {
            string path = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (string folder in path.Split(Path.PathSeparator))
            {
                try { string candidate = Path.Combine(folder.Trim(), "ffmpeg.exe"); if (File.Exists(candidate)) return candidate; } catch { }
            }
            try
            {
                string packages = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Packages");
                if (Directory.Exists(packages))
                    foreach (string file in Directory.GetFiles(packages, "ffmpeg.exe", SearchOption.AllDirectories)) return file;
            }
            catch { }
            return null;
        }

        void ExportMidi()
        {
            if (tracks.Count == 0) { MessageBox.Show(this, "Record or load at least one track before exporting.", "Export MIDI", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            using (SaveFileDialog dialog = new SaveFileDialog { Filter = "MIDI file (*.mid)|*.mid", DefaultExt = "mid", FileName = "Layer Keys Arrangement.mid" })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    const int ppq = 480; List<MidiFileEvent> events = new List<MidiFileEvent>();
                    foreach (Track track in tracks) if (!track.Muted) foreach (NoteEvent note in track.Events)
                    {
                        long tick = Math.Max(0, (track.StartMs + note.Start) * (long)tempo * ppq / 60000L); long end = tick + Math.Max(1, note.Duration * (long)tempo * ppq / 60000L);
                        events.Add(new MidiFileEvent { Tick = tick, NoteOn = true, Note = note.Note, Velocity = note.Velocity }); events.Add(new MidiFileEvent { Tick = end, NoteOn = false, Note = note.Note, Velocity = 0 });
                    }
                    events.Sort(delegate(MidiFileEvent a, MidiFileEvent b) { int c = a.Tick.CompareTo(b.Tick); return c != 0 ? c : a.NoteOn.CompareTo(b.NoteOn); });
                    using (MemoryStream trackData = new MemoryStream()) using (BinaryWriter tw = new BinaryWriter(trackData))
                    {
                        WriteVariable(tw, 0); tw.Write((byte)0xFF); tw.Write((byte)0x51); tw.Write((byte)3); int micros = 60000000 / tempo; tw.Write((byte)(micros >> 16)); tw.Write((byte)(micros >> 8)); tw.Write((byte)micros);
                        long previous = 0; foreach (MidiFileEvent ev in events) { WriteVariable(tw, ev.Tick - previous); previous = ev.Tick; tw.Write((byte)(ev.NoteOn ? 0x90 : 0x80)); tw.Write((byte)ev.Note); tw.Write((byte)ev.Velocity); }
                        WriteVariable(tw, 0); tw.Write((byte)0xFF); tw.Write((byte)0x2F); tw.Write((byte)0);
                        using (BinaryWriter writer = new BinaryWriter(File.Create(dialog.FileName))) { writer.Write(new byte[] {77,84,104,100}); WriteBigEndian(writer, 6); writer.Write((byte)0); writer.Write((byte)0); writer.Write((byte)0); writer.Write((byte)1); writer.Write((byte)(ppq >> 8)); writer.Write((byte)(ppq & 0xFF)); writer.Write(new byte[] {77,84,114,107}); WriteBigEndian(writer, (int)trackData.Length); writer.Write(trackData.ToArray()); }
                    }
                    statusLabel.Text = "MIDI EXPORTED";
                }
                catch (Exception ex) { MessageBox.Show(this, "Could not export the MIDI file.\n\n" + ex.Message, "Layer Keys", MessageBoxButtons.OK, MessageBoxIcon.Error); }
            }
        }

        static void WriteBigEndian(BinaryWriter writer, int value) { writer.Write((byte)(value >> 24)); writer.Write((byte)(value >> 16)); writer.Write((byte)(value >> 8)); writer.Write((byte)value); }
        static void WriteVariable(BinaryWriter writer, long value)
        {
            long buffer = value & 0x7F; while ((value >>= 7) > 0) { buffer <<= 8; buffer |= ((value & 0x7F) | 0x80); }
            while (true) { writer.Write((byte)buffer); if ((buffer & 0x80) != 0) buffer >>= 8; else break; }
        }

        void ChangeOctave(int amount)
        {
            AllNotesOff(); octave = Math.Max(-3, Math.Min(3, octave + amount)); octaveLabel.Text = octave > 0 ? "+" + octave : octave.ToString();
        }

        void FormKeyDown(object sender, KeyEventArgs e)
        {
            if (exporting) { e.Handled = true; e.SuppressKeyPress = true; return; }
            if (e.KeyCode == Keys.Z) { ChangeOctave(-1); e.Handled = true; }
            else if (e.KeyCode == Keys.X) { ChangeOctave(1); e.Handled = true; }
            else if (e.KeyCode == Keys.Space) { ToggleRecording(); e.Handled = true; }
        }

        void SetProgram(int value) { Send(0xC0, value, 0); }
        void Send(int status, int data1, int data2) { if (midiOut != IntPtr.Zero) Midi.midiOutShortMsg(midiOut, Midi.Message(status, data1, data2)); }
        void AllNotesOff()
        {
            Send(0xB0, 123, 0); liveOutputNotes.Clear(); piano.ActiveNotes.Clear(); piano.Invalidate();
        }
        void CloseMidiInput()
        {
            if (midiIn == IntPtr.Zero) return;
            Midi.midiInStop(midiIn); Midi.midiInReset(midiIn); Midi.midiInClose(midiIn); midiIn = IntPtr.Zero;
        }
        void HandleFormClosing(object sender, FormClosingEventArgs e)
        {
            if (exporting) { e.Cancel = true; return; }
            timer.Stop(); deviceTimer.Stop(); CloseMidiInput();
            if (midiOut != IntPtr.Zero) { Midi.midiOutReset(midiOut); Midi.midiOutClose(midiOut); midiOut = IntPtr.Zero; }
        }
    }
}
