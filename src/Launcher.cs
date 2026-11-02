// Pro Pinball - Big Race USA : LAN-Launcher
// Startet das Spiel (im cnc-ddraw-Fenster) und organisiert die LAN-Lobby:
//  - Alle Launcher im Netz finden sich per UDP-Broadcast (Port 47700) und zeigen sich gegenseitig an.
//  - Jeder Launcher betreibt den nachgebauten Empire-Lobby-Server (Lobby.cs, Port 2914). Benutzt wird der des
//    Launchers, der zuerst geoeffnet wurde - so landen alle in derselben Lobby, auch wenn spaeter jemand dazukommt.
//    Bei fast gleichzeitigem Start entscheidet die niedrigere IP. Geht dieser PC weg, uebernimmt der naechste.
//  - Die Adresse dieser Lobby steht in propin.ini ([empire] Server=), aktualisiert bei jeder Aenderung.
//  - Name, IP, Lobby und naechster Schritt landen im Shared Memory "Local\BigRaceLanBar"; die dinput.dll im
//    Spielordner (BarHook.c) zeigt sie im Fenstertitel des Spiels an.
// Wird das Spiel beendet, schliesst sich der Launcher.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace BigRaceLanLauncher
{
    public class LauncherForm : Form
    {
        const string GameExeName = "BigRaceUSA.exe";
        const string GameProcessName = "BigRaceUSA";
        const int InfoPort = 47700;
        const string Magic = "BRLAN2";
        // Lobby-Dialog des Spiels (Spielerliste + Chat-Eingabe) - nur zum Erkennen, ob der Spieler in der Lobby ist
        const int IDC_LOBBY_LIST = 0x3f7, IDC_LOBBY_CHAT = 0x3f8;
        // Layout wie SharedBar in BarHook.c: magic, heartbeat, WCHAR info[400], WCHAR status[200]
        const int BarMagic = 0x4C524242, BarInfoLen = 400, BarStatusLen = 200;

        // Farben im Stil des Spiels: Asphalt, Taxi-Gelb, Logo-Rot
        static readonly Color Asphalt = Color.FromArgb(18, 19, 23);
        static readonly Color Panel = Color.FromArgb(32, 34, 41);
        static readonly Color Yellow = Color.FromArgb(255, 200, 30);
        static readonly Color Red = Color.FromArgb(214, 36, 42);
        static readonly Color Grey = Color.FromArgb(150, 155, 165);
        static readonly Color Go = Color.FromArgb(70, 210, 90);

        class Peer
        {
            public string Id, Name, State, Ip;
            public DateTime Start;      // geschaetzter Startzeitpunkt des Launchers (UTC)
            public bool Server;         // Lobby-Server laeuft dort
            public DateTime Seen;
            public bool Self;
        }

        TextBox nameBox, gameDirBox;
        Button browseButton;
        GameButton startButton;
        PlayerList playerList;
        Label statusLabel, ipValue;
        Image header;
        readonly float scale;
        Rectangle closeBox, minBox;

        Timer timer;
        Process game;
        Socket socket;
        IPAddress ownIp, broadcast;
        readonly Dictionary<string, Peer> peers = new Dictionary<string, Peer>();
        string gameState = "setup";     // setup | game | lobby
        int ticks;
        string iniHost;                 // zuletzt in propin.ini eingetragene Lobby-IP
        LobbyServer lobby;
        volatile string ownName = "";   // fuer den Lobby-Server-Thread
        readonly string myId = Guid.NewGuid().ToString("N");
        readonly DateTime myStart = DateTime.UtcNow;
        MemoryMappedFile barMap;
        MemoryMappedViewAccessor barView;
        readonly string settingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BigRaceLanLauncher.ini");

        public LauncherForm()
        {
            using (Graphics g = CreateGraphics()) scale = g.DpiX / 96f;
            Text = "Big Race USA - LAN";
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleMode = AutoScaleMode.None;
            BackColor = Asphalt;
            DoubleBuffered = true;
            ClientSize = new Size(S(520), S(700));
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            Font body = new Font(FontOr("Bahnschrift", "Segoe UI"), 11f);
            Font small = new Font(FontOr("Bahnschrift SemiBold", "Segoe UI Semibold"), 9f);

            AddCaption("FAHRERNAME", 24, 232, small);
            nameBox = AddBox(24, 252, 220, body);
            nameBox.MaxLength = 15;

            AddCaption("DEINE LAN-IP", 276, 232, small);
            ipValue = new Label { AutoSize = false, Location = P(276, 254), Size = Sz(220, 28), ForeColor = Yellow, BackColor = Color.Transparent, Font = new Font(FontOr("Impact", "Arial Black"), 15f) };
            Controls.Add(ipValue);

            AddCaption("SPIELORDNER", 24, 298, small);
            gameDirBox = AddBox(24, 318, 432, new Font(body.FontFamily, 9.5f));
            browseButton = new Button { Text = "...", Location = P(462, 318), Size = new Size(S(34), gameDirBox.Height), FlatStyle = FlatStyle.Flat, ForeColor = Yellow, BackColor = Panel,
                                        Font = new Font(body.FontFamily, 8f, FontStyle.Bold), Padding = Padding.Empty, TextAlign = ContentAlignment.MiddleCenter, Cursor = Cursors.Hand };
            browseButton.FlatAppearance.BorderColor = Color.FromArgb(70, 74, 85);
            browseButton.Click += delegate { Browse(); };
            Controls.Add(browseButton);

            AddCaption("FAHRERFELD  -  SPIELER IM NETZWERK", 24, 366, small);
            playerList = new PlayerList(this) { Location = P(24, 386), Size = Sz(472, 176) };
            Controls.Add(playerList);

            startButton = new GameButton { Text = "START", Location = P(24, 576), Size = Sz(472, 58), Font = new Font(FontOr("Impact", "Arial Black"), 24f) };
            startButton.Click += delegate { StartGame(); };
            Controls.Add(startButton);

            statusLabel = new Label { AutoSize = false, Location = P(24, 642), Size = Sz(472, 48), ForeColor = Grey, BackColor = Color.Transparent, Font = new Font(body.FontFamily, 9.5f) };
            Controls.Add(statusLabel);

            header = LoadHeader();
            closeBox = new Rectangle(ClientSize.Width - S(40), S(10), S(30), S(30));
            minBox = new Rectangle(ClientSize.Width - S(76), S(10), S(30), S(30));

            nameBox.TextChanged += delegate { ownName = nameBox.Text.Trim(); UpdateTexts(); };
            gameDirBox.TextChanged += delegate { if (header == null) { header = LoadHeader(); Invalidate(); } };

            LoadSettings();
            ownName = nameBox.Text.Trim();
            FindLanAddress();
            ipValue.Text = ownIp == null ? "nicht gefunden" : ownIp.ToString();
            OpenSocket();
            try
            {
                barMap = MemoryMappedFile.CreateOrOpen(@"Local\BigRaceLanBar", 8 + 2 * (BarInfoLen + BarStatusLen));
                barView = barMap.CreateViewAccessor();
            }
            catch { barMap = null; barView = null; }

            lobby = new LobbyServer(LobbyName, Path.Combine(Path.GetTempPath(), "BigRace-Lobby-Protokoll.txt"));
            lobby.Start();

            // Laeuft das Spiel schon (z. B. ohne Launcher gestartet), wird es uebernommen
            Process[] running = Process.GetProcessesByName(GameProcessName);
            if (running.Length > 0) { game = running[0]; gameState = "game"; }

            timer = new Timer { Interval = 500 };
            timer.Tick += delegate { Tick(); };
            timer.Start();
            ActiveControl = startButton;
            UpdateTexts();
            Send();
        }

        int S(float v) { return (int)Math.Round(v * scale); }
        Point P(float x, float y) { return new Point(S(x), S(y)); }
        Size Sz(float w, float h) { return new Size(S(w), S(h)); }

        static string FontOr(string wanted, string fallback)
        {
            foreach (FontFamily f in FontFamily.Families) if (f.Name == wanted) return wanted;
            return fallback;
        }

        void AddCaption(string text, float x, float y, Font font)
        {
            Controls.Add(new Label { Text = text, AutoSize = true, Location = P(x, y), ForeColor = Red, BackColor = Color.Transparent, Font = font });
        }

        TextBox AddBox(float x, float y, float w, Font font)
        {
            var box = new TextBox { Location = P(x, y), Width = S(w), BorderStyle = BorderStyle.FixedSingle, BackColor = Panel, ForeColor = Color.White, Font = font };
            Controls.Add(box);
            return box;
        }

        // Titelbild des Spiels aus dem Spielordner (nicht in die EXE eingebaut)
        Image LoadHeader()
        {
            try
            {
                string path = Path.Combine(gameDirBox == null ? "" : gameDirBox.Text.Trim(), @"DAT\pc\slides\1\SLIDE_31.JPG");
                if (File.Exists(path)) using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read)) return Image.FromStream(new MemoryStream(ReadAll(fs)));
            }
            catch { }
            return null;
        }

        static byte[] ReadAll(Stream s) { var ms = new MemoryStream(); s.CopyTo(ms); return ms.ToArray(); }

        // ---------------------------------------------------------------- Zeichnen

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            int w = ClientSize.Width, hh = S(200);

            using (var bg = new LinearGradientBrush(new Rectangle(0, hh, w, ClientSize.Height - hh), Color.FromArgb(28, 30, 36), Asphalt, 90f))
                g.FillRectangle(bg, 0, hh, w, ClientSize.Height - hh);

            // Kopf: Titelbild, oben ausgeschnitten, unten in den Asphalt ausgeblendet
            if (header != null)
            {
                float srcH = header.Width * (float)hh / w;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(header, new Rectangle(0, 0, w, hh), new RectangleF(0, 0, header.Width, srcH), GraphicsUnit.Pixel);
            }
            else
            {
                using (var b = new LinearGradientBrush(new Rectangle(0, 0, w, hh), Color.FromArgb(40, 60, 140), Color.FromArgb(20, 20, 40), 90f)) g.FillRectangle(b, 0, 0, w, hh);
                DrawOutlined(g, "BIG RACE USA", new Font(FontOr("Impact", "Arial Black"), 40f, FontStyle.Italic), new PointF(S(24), S(40)), Yellow, Red);
            }
            using (var fade = new LinearGradientBrush(new Rectangle(0, hh - S(70), w, S(70)), Color.FromArgb(0, 0, 0, 0), Color.FromArgb(230, 0, 0, 0), 90f))
                g.FillRectangle(fade, 0, hh - S(70), w, S(70));
            using (var tag = new Font(FontOr("Impact", "Arial Black"), 20f, FontStyle.Italic))
                DrawOutlined(g, "LAN  HEAD-TO-HEAD", tag, new PointF(S(22), hh - S(44)), Color.White, Color.Black);

            // Zielflaggen-Streifen
            int sq = S(8);
            for (int x = 0, i = 0; x < w; x += sq, i++)
                for (int row = 0; row < 2; row++)
                    using (var b = new SolidBrush((i + row) % 2 == 0 ? Color.White : Color.Black))
                        g.FillRectangle(b, x, hh + row * sq, sq, sq);

            // Fensterknoepfe
            DrawWindowButton(g, minBox, false);
            DrawWindowButton(g, closeBox, true);

            using (var pen = new Pen(Color.FromArgb(60, 64, 74))) g.DrawRectangle(pen, 0, 0, w - 1, ClientSize.Height - 1);
        }

        static void DrawOutlined(Graphics g, string text, Font font, PointF at, Color fill, Color outline)
        {
            using (var path = new GraphicsPath())
            {
                path.AddString(text, font.FontFamily, (int)font.Style, g.DpiY * font.SizeInPoints / 72f, at, StringFormat.GenericDefault);
                using (var pen = new Pen(outline, Math.Max(2f, font.SizeInPoints / 6f)) { LineJoin = LineJoin.Round }) g.DrawPath(pen, path);
                using (var b = new SolidBrush(fill)) g.FillPath(b, path);
            }
        }

        void DrawWindowButton(Graphics g, Rectangle r, bool close)
        {
            using (var b = new SolidBrush(Color.FromArgb(160, 0, 0, 0))) g.FillEllipse(b, r);
            using (var pen = new Pen(close ? Yellow : Color.White, Math.Max(2f, scale * 2f)))
            {
                int m = r.Width / 3;
                if (close)
                {
                    g.DrawLine(pen, r.Left + m, r.Top + m, r.Right - m, r.Bottom - m);
                    g.DrawLine(pen, r.Right - m, r.Top + m, r.Left + m, r.Bottom - m);
                }
                else g.DrawLine(pen, r.Left + m, r.Top + r.Height / 2, r.Right - m, r.Top + r.Height / 2);
            }
        }

        [DllImport("user32.dll")] static extern bool ReleaseCapture();
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            if (closeBox.Contains(e.Location)) { Close(); return; }
            if (minBox.Contains(e.Location)) { WindowState = FormWindowState.Minimized; return; }
            if (e.Y < S(216))
            {
                ReleaseCapture();
                SendMessage(Handle, 0xA1, (IntPtr)2, IntPtr.Zero);     // WM_NCLBUTTONDOWN, HTCAPTION: Fenster verschieben
            }
        }

        // ---------------------------------------------------------------- Einstellungen

        void LoadSettings()
        {
            var s = new Dictionary<string, string>();
            try
            {
                foreach (string line in File.ReadAllLines(settingsPath))
                {
                    int i = line.IndexOf('=');
                    if (i > 0) s[line.Substring(0, i)] = line.Substring(i + 1);
                }
            }
            catch { }
            string v;
            nameBox.Text = s.TryGetValue("name", out v) && v.Length > 0 ? v : Environment.UserName;
            gameDirBox.Text = s.TryGetValue("gamedir", out v) && File.Exists(Path.Combine(v, GameExeName)) ? v : FindGameDir();
        }

        void SaveSettings()
        {
            try { File.WriteAllLines(settingsPath, new[] { "name=" + nameBox.Text.Trim(), "gamedir=" + gameDirBox.Text.Trim() }); }
            catch { }
        }

        static string FindGameDir()
        {
            string exeDir = Path.GetDirectoryName(Application.ExecutablePath);
            foreach (string dir in new[] { exeDir, Path.GetDirectoryName(exeDir) })
            {
                if (dir != null && File.Exists(Path.Combine(dir, GameExeName))) return dir;
            }
            foreach (string key in new[] { @"SOFTWARE\WOW6432Node\GOG.com\Games\1207658735", @"SOFTWARE\GOG.com\Games\1207658735" })
            {
                try
                {
                    using (var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(key))
                    {
                        string p = k == null ? null : k.GetValue("path") as string;
                        if (p != null && File.Exists(Path.Combine(p, GameExeName))) return p;
                    }
                }
                catch { }
            }
            return "";
        }

        void Browse()
        {
            using (var dlg = new FolderBrowserDialog { Description = "Ordner mit " + GameExeName + " waehlen" })
            {
                if (Directory.Exists(gameDirBox.Text)) dlg.SelectedPath = gameDirBox.Text;
                if (dlg.ShowDialog(this) == DialogResult.OK) gameDirBox.Text = dlg.SelectedPath;
            }
        }

        // LAN-Adresse der Netzwerkkarte mit Standardgateway (keine VPN-/virtuellen Adapter) und deren Broadcast-Adresse
        void FindLanAddress()
        {
            foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType != NetworkInterfaceType.Ethernet && ni.NetworkInterfaceType != NetworkInterfaceType.Wireless80211) continue;
                IPInterfaceProperties props = ni.GetIPProperties();
                bool hasGateway = false;
                foreach (GatewayIPAddressInformation g in props.GatewayAddresses)
                    if (g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any)) hasGateway = true;
                if (!hasGateway) continue;
                foreach (UnicastIPAddressInformation a in props.UnicastAddresses)
                {
                    if (a.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    ownIp = a.Address;
                    if (a.IPv4Mask != null)
                    {
                        byte[] ip = a.Address.GetAddressBytes(), mask = a.IPv4Mask.GetAddressBytes();
                        for (int i = 0; i < 4; i++) ip[i] = (byte)(ip[i] | ~mask[i]);
                        broadcast = new IPAddress(ip);
                    }
                    return;
                }
            }
        }

        // ---------------------------------------------------------------- Mitspieler finden (UDP-Broadcast)

        void OpenSocket()
        {
            try
            {
                socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                socket.EnableBroadcast = true;
                socket.Bind(new IPEndPoint(IPAddress.Any, InfoPort));
            }
            catch { socket = null; }
        }

        void Send()
        {
            if (socket == null) return;
            long age = (long)(DateTime.UtcNow - myStart).TotalMilliseconds;
            string name = nameBox.Text.Trim().Replace("|", "");
            bool server = lobby != null && lobby.Error == null;
            byte[] data = Encoding.UTF8.GetBytes(Magic + "|" + myId + "|" + name + "|" + gameState + "|" + age + "|" + (server ? "1" : "0"));
            var targets = new List<IPAddress> { IPAddress.Broadcast };
            if (broadcast != null) targets.Add(broadcast);
            foreach (IPAddress target in targets)
            {
                try { socket.SendTo(data, new IPEndPoint(target, InfoPort)); } catch { }
            }
        }

        void Receive()
        {
            bool isNew = false;
            if (socket != null)
            {
                var buf = new byte[512];
                try
                {
                    while (socket.Available > 0)
                    {
                        EndPoint from = new IPEndPoint(IPAddress.Any, 0);
                        int n = socket.ReceiveFrom(buf, ref from);
                        string[] p = Encoding.UTF8.GetString(buf, 0, n).Split('|');
                        long age;
                        if (p.Length < 6 || p[0] != Magic || p[1] == myId || !long.TryParse(p[4], out age)) continue;
                        Peer peer;
                        if (!peers.TryGetValue(p[1], out peer)) { peer = new Peer { Id = p[1], Start = DateTime.MaxValue }; peers[p[1]] = peer; isNew = true; }
                        peer.Name = p[2];
                        peer.State = p[3];
                        peer.Server = p[5] == "1";
                        peer.Ip = ((IPEndPoint)from).Address.ToString();
                        peer.Seen = DateTime.UtcNow;
                        // Startzeit nur nach vorne korrigieren, damit Laufzeitschwankungen die Lobby-Wahl nicht kippen
                        DateTime start = DateTime.UtcNow.AddMilliseconds(-age);
                        if (start < peer.Start) peer.Start = start;
                    }
                }
                catch { }
            }
            var gone = new List<string>();
            foreach (Peer p in peers.Values) if ((DateTime.UtcNow - p.Seen).TotalSeconds > 7) gone.Add(p.Id);
            foreach (string id in gone) peers.Remove(id);
            if (isNew) Send();   // sofort antworten, damit sich alle schnell sehen
        }

        Peer SelfPeer()
        {
            return new Peer { Id = myId, Name = nameBox.Text.Trim(), State = gameState, Ip = ownIp == null ? null : ownIp.ToString(),
                              Start = myStart, Server = lobby != null && lobby.Error == null, Self = true };
        }

        // Alle Spieler, die Lobby zuerst, dann nach Name
        List<Peer> AllPlayers()
        {
            var list = new List<Peer> { SelfPeer() };
            list.AddRange(peers.Values);
            Peer host = LobbyHost(list);
            list.Sort((a, b) => a == host ? -1 : b == host ? 1 : a.Self ? -1 : b.Self ? 1 : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            return list;
        }

        // Die Lobby stellt, wer den Launcher zuerst geoeffnet hat (bei weniger als 0,5 s Abstand die niedrigere IP)
        static Peer LobbyHost(List<Peer> players)
        {
            Peer best = null;
            foreach (Peer p in players)
            {
                if (!p.Server || p.Ip == null) continue;
                if (best == null) { best = p; continue; }
                double diff = (p.Start - best.Start).TotalMilliseconds;
                if (diff < -500 || (Math.Abs(diff) <= 500 && IpValue(p.Ip) < IpValue(best.Ip))) best = p;
            }
            return best;
        }

        static long IpValue(string ip)
        {
            IPAddress a;
            if (!IPAddress.TryParse(ip, out a)) return long.MaxValue;
            byte[] b = a.GetAddressBytes();
            return ((long)b[0] << 24) | ((long)b[1] << 16) | ((long)b[2] << 8) | b[3];
        }

        // Name eines Spielers in der Lobby: der Launcher-Name des PCs, von dem die Verbindung kommt
        string LobbyName(IPAddress ip)
        {
            if (IPAddress.IsLoopback(ip) || ip.Equals(ownIp)) return ownName;
            string text = ip.ToString();
            lock (peers) foreach (Peer p in peers.Values) if (p.Ip == text) return p.Name;
            return "Spieler";
        }

        // ---------------------------------------------------------------- Start

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi)]
        static extern bool WritePrivateProfileString(string section, string key, string value, string file);

        bool WriteLobbyIni(string dir, string hostIp)
        {
            string ini = Path.Combine(dir, "propin.ini");
            bool ok = WritePrivateProfileString("empire", "Server", hostIp, ini)
                    & WritePrivateProfileString("empire", "Port", LobbyServer.Port.ToString(), ini)
                    & WritePrivateProfileString("empire", "Socks4", "", ini);
            if (ok) iniHost = hostIp;
            return ok;
        }

        void StartGame()
        {
            if (GameRunning()) return;
            string dir = gameDirBox.Text.Trim();
            string exe = Path.Combine(dir, GameExeName);
            if (!File.Exists(exe))
            {
                MessageBox.Show(this, GameExeName + " nicht gefunden. Bitte den Spielordner waehlen.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            var missing = new List<string>();
            if (!File.Exists(Path.Combine(dir, "ddraw.dll"))) missing.Add("ddraw.dll (Fenstermodus)");
            if (!File.Exists(Path.Combine(dir, "dinput.dll"))) missing.Add("dinput.dll (Infos im Fenstertitel)");
            if (missing.Count > 0)
            {
                var r = MessageBox.Show(this, "Im Spielordner\r\n    " + dir + "\r\nfehlt: " + string.Join(", ", missing.ToArray()) + ".\r\n\r\n" +
                    "Bitte BigRace-LAN-Patcher.exe auf DIESEM PC ausfuehren und dort genau diesen Spielordner eintragen.\r\n\r\nTrotzdem starten?",
                    Text, MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (r != DialogResult.Yes) return;
            }
            Peer host = LobbyHost(AllPlayers());
            if (host != null && !WriteLobbyIni(dir, host.Ip))
                MessageBox.Show(this, "Konnte " + Path.Combine(dir, "propin.ini") + " nicht schreiben (Schreibschutz?).\r\nDas Spiel findet die Lobby dann nicht.",
                    Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            SaveSettings();
            game = Process.Start(new ProcessStartInfo(exe) { WorkingDirectory = dir, UseShellExecute = true });
            gameState = "game";
            Send();
            UpdateTexts();
            // Die Infos stehen im Fenstertitel des Spiels - der Launcher wartet in der Taskleiste
            if (File.Exists(Path.Combine(dir, "dinput.dll"))) WindowState = FormWindowState.Minimized;
        }

        bool GameRunning()
        {
            if (game == null) return false;
            try { return !game.HasExited; }
            catch { return Process.GetProcessesByName(GameProcessName).Length > 0; }    // z. B. als Administrator gestartet
        }

        // ---------------------------------------------------------------- Laufend

        void Tick()
        {
            lock (peers) Receive();
            if (game != null)
            {
                if (!GameRunning())
                {
                    // Spiel beendet -> Launcher auch
                    timer.Stop();
                    Close();
                    return;
                }
                string state = "game";
                foreach (IntPtr dlg in GameDialogs((uint)game.Id))
                    if (Has(dlg, IDC_LOBBY_LIST, IDC_LOBBY_CHAT)) state = "lobby";
                if (state != gameState) { gameState = state; Send(); }

                // Lobby gewechselt (z. B. weil der Lobby-PC gegangen ist): fuer den naechsten Lobby-Besuch umtragen
                Peer host = LobbyHost(AllPlayers());
                if (host != null && host.Ip != iniHost) WriteLobbyIni(gameDirBox.Text.Trim(), host.Ip);
            }
            if (++ticks % 4 == 0) Send();
            UpdateTexts();
        }

        static string StateText(string state)
        {
            if (state == "lobby") return "in der Lobby";
            if (state == "game") return "im Spiel";
            return "bereit";
        }

        void UpdateTexts()
        {
            List<Peer> players = AllPlayers();
            Peer host = LobbyHost(players);
            int others = players.Count - 1;
            playerList.SetPlayers(players, host);

            string status;
            if (!File.Exists(Path.Combine(gameDirBox.Text.Trim(), GameExeName))) status = "Spielordner nicht gefunden - bitte mit \"...\" waehlen.";
            else if (lobby != null && lobby.Error != null && others == 0) status = "Lobby-Server startet nicht (Port " + LobbyServer.Port + " belegt?) und kein anderer PC gefunden.";
            else if (others == 0) status = "Suche andere Spieler im Netzwerk ... (Launcher dort offen? Gleiches Netz? VPN aus?)";
            else status = "START, Tisch laden, dann Esc > Head-To-Head > Friendly (Lobby). Dort den Gegner anklicken - ein Klick startet das Match.";
            if (statusLabel.Text != status) statusLabel.Text = status;
            startButton.Enabled = !GameRunning();

            // Fenstertitel des Spiels (dinput.dll) - kurz halten, sonst kuerzt Windows ihn
            string ownText = ownIp == null ? "?" : ownIp.ToString();
            string barInfo = "Du: " + ownName + " " + ownText + "   ·   Lobby: " +
                (host == null ? "keine" : host.Self ? "bei dir" : host.Name + " " + host.Ip) +
                "   ·   " + (others == 1 ? "1 Mitspieler" : others + " Mitspieler") + " im Netz";
            string barStatus;
            if (others == 0) barStatus = "Noch kein anderer Spieler gefunden";
            else if (gameState == "lobby") barStatus = "Gegner in der Liste anklicken - ein Klick startet das Match";
            else barStatus = "Tisch laden, dann Esc > Head-To-Head > Friendly (Lobby)";
            WriteBar(barInfo, barStatus);
        }

        void WriteBar(string infoText, string statusText)
        {
            if (barView == null) return;
            WriteChars(8, infoText, BarInfoLen);
            WriteChars(8 + 2 * BarInfoLen, statusText, BarStatusLen);
            barView.Write(0, BarMagic);
            barView.Write(4, Environment.TickCount);
        }

        void WriteChars(long offset, string text, int max)
        {
            if (text.Length > max - 1) text = text.Substring(0, max - 1);
            char[] chars = (text + "\0").ToCharArray();
            barView.WriteArray(offset, chars, 0, chars.Length);
        }

        // ---------------------------------------------------------------- Win32-Helfer

        delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc proc, IntPtr lParam);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hwnd, StringBuilder sb, int max);
        [DllImport("user32.dll")] static extern IntPtr GetDlgItem(IntPtr dlg, int id);

        static List<IntPtr> GameDialogs(uint pid)
        {
            var list = new List<IntPtr>();
            EnumWindows((h, l) =>
            {
                uint p;
                GetWindowThreadProcessId(h, out p);
                if (p == pid && IsWindowVisible(h))
                {
                    var sb = new StringBuilder(64);
                    GetClassName(h, sb, sb.Capacity);
                    if (sb.ToString() == "#32770") list.Add(h);
                }
                return true;
            }, IntPtr.Zero);
            return list;
        }

        static bool Has(IntPtr dlg, params int[] ids)
        {
            foreach (int id in ids) if (GetDlgItem(dlg, id) == IntPtr.Zero) return false;
            return true;
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            SaveSettings();
            if (socket != null) socket.Close();
            if (lobby != null) lobby.Stop();
            base.OnFormClosing(e);
        }

        // ---------------------------------------------------------------- Bausteine im Spielstil

        // Spielerliste: Statuslampe, Name, IP, Zustand; die Lobby bekommt die Zielflagge
        class PlayerList : Control
        {
            readonly LauncherForm owner;
            List<Peer> players = new List<Peer>();
            Peer host;
            string signature = "";

            public PlayerList(LauncherForm owner)
            {
                this.owner = owner;
                DoubleBuffered = true;
                BackColor = Panel;
            }

            public void SetPlayers(List<Peer> list, Peer lobbyHost)
            {
                var sb = new StringBuilder(lobbyHost == null ? "" : lobbyHost.Id);
                foreach (Peer p in list) sb.Append('|').Append(p.Id).Append(p.Name).Append(p.Ip).Append(p.State);
                if (sb.ToString() == signature) return;
                signature = sb.ToString();
                players = list;
                host = lobbyHost;
                Invalidate();
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                Graphics g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                float s = owner.scale;
                int rowH = (int)(36 * s);
                using (var nameFont = new Font(FontOr("Bahnschrift SemiBold", "Segoe UI Semibold"), 11.5f))
                using (var smallFont = new Font(FontOr("Bahnschrift", "Segoe UI"), 9f))
                using (var grey = new SolidBrush(Grey))
                using (var white = new SolidBrush(Color.White))
                using (var yellow = new SolidBrush(Yellow))
                {
                    for (int i = 0; i < players.Count; i++)
                    {
                        Peer p = players[i];
                        int y = i * rowH;
                        if (i % 2 == 1) using (var b = new SolidBrush(Color.FromArgb(38, 41, 49))) g.FillRectangle(b, 0, y, Width, rowH);

                        // Statuslampe: gruen = gefunden, gelb = in der Lobby, blau = im Spiel
                        Color lamp = p.State == "lobby" ? Yellow : p.State == "game" ? Color.FromArgb(80, 160, 255) : Go;
                        int d = (int)(12 * s);
                        using (var b = new SolidBrush(lamp)) g.FillEllipse(b, 10 * s, y + (rowH - d) / 2f, d, d);
                        using (var b = new SolidBrush(Color.FromArgb(90, Color.White))) g.FillEllipse(b, 12 * s, y + (rowH - d) / 2f + 2 * s, d / 2.5f, d / 2.5f);

                        g.DrawString(p.Name + (p.Self ? "  (du)" : ""), nameFont, p.Self ? yellow : white, 32 * s, y + 7 * s);
                        g.DrawString(p.Ip ?? "?", smallFont, grey, 220 * s, y + 10 * s);
                        string state = StateText(p.State);
                        SizeF sz = g.MeasureString(state, smallFont);
                        g.DrawString(state, smallFont, grey, Width - sz.Width - 10 * s, y + 10 * s);

                        if (p == host)
                        {
                            // kleine Zielflagge + "LOBBY"
                            float fx = 330 * s, fy = y + 11 * s, q = 4 * s;
                            for (int r = 0; r < 3; r++)
                                for (int c = 0; c < 4; c++)
                                    using (var b = new SolidBrush((r + c) % 2 == 0 ? Color.White : Color.Black)) g.FillRectangle(b, fx + c * q, fy + r * q, q, q);
                            g.DrawString("LOBBY", smallFont, yellow, fx + 18 * s, y + 10 * s);
                        }
                    }
                    if (players.Count <= 1)
                        g.DrawString("Noch keine anderen Spieler gefunden ...", smallFont, grey, 32 * s, rowH + 10 * s);
                }
                using (var pen = new Pen(Color.FromArgb(70, 74, 85))) g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
            }
        }

        // Grosser roter Startknopf mit gelber Schrift
        class GameButton : Control
        {
            bool hover, down;

            public GameButton()
            {
                DoubleBuffered = true;
                Cursor = Cursors.Hand;
                SetStyle(ControlStyles.SupportsTransparentBackColor, true);
                BackColor = Color.Transparent;
            }

            protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
            protected override void OnMouseLeave(EventArgs e) { hover = false; down = false; Invalidate(); base.OnMouseLeave(e); }
            protected override void OnMouseDown(MouseEventArgs e) { down = true; Invalidate(); base.OnMouseDown(e); }
            protected override void OnMouseUp(MouseEventArgs e) { down = false; Invalidate(); base.OnMouseUp(e); }
            protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

            protected override void OnPaint(PaintEventArgs e)
            {
                Graphics g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                var r = new Rectangle(1, 1, Width - 3, Height - 3);
                int radius = Height / 3;
                using (var path = Rounded(r, radius))
                {
                    Color top = !Enabled ? Color.FromArgb(70, 70, 76) : hover ? Color.FromArgb(240, 60, 60) : Red;
                    Color bottom = !Enabled ? Color.FromArgb(45, 45, 50) : down ? Color.FromArgb(120, 10, 15) : Color.FromArgb(150, 16, 22);
                    using (var b = new LinearGradientBrush(r, top, bottom, 90f)) g.FillPath(b, path);
                    using (var pen = new Pen(Enabled ? Yellow : Color.FromArgb(90, 90, 95), 2f)) g.DrawPath(pen, path);
                }
                string text = Enabled ? Text : "SPIEL LAEUFT";
                using (var path = new GraphicsPath())
                {
                    float em = g.DpiY * Font.SizeInPoints / 72f;
                    var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                    path.AddString(text, Font.FontFamily, (int)FontStyle.Italic, em, new RectangleF(0, down ? 2 : 0, Width, Height), fmt);
                    using (var pen = new Pen(Color.FromArgb(90, 0, 0), 4f) { LineJoin = LineJoin.Round }) g.DrawPath(pen, path);
                    using (var b = new SolidBrush(Enabled ? Yellow : Color.FromArgb(150, 150, 155))) g.FillPath(b, path);
                }
            }

            static GraphicsPath Rounded(Rectangle r, int radius)
            {
                var p = new GraphicsPath();
                int d = radius * 2;
                p.AddArc(r.Left, r.Top, d, d, 180, 90);
                p.AddArc(r.Right - d, r.Top, d, d, 270, 90);
                p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
                p.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
                p.CloseFigure();
                return p;
            }
        }

        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new LauncherForm());
        }
    }
}
