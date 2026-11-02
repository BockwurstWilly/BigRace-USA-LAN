// Pro Pinball - Big Race USA : Nachbau des Empire-Lobby-Servers fuers LAN
// Das Spiel spricht im Modus "Friendly (Lobby)" mit einem Server (propin.ini [empire] Server/Port, Standard
// Port 2914). Der Original-Server ist abgeschaltet; dieser hier laeuft im Launcher des Hosts. Das eigentliche
// Match laeuft danach ueber eine direkte TCP-Verbindung zwischen den beiden Spielen - ohne DirectPlay.
//
// Protokoll (rekonstruiert aus BigRaceUSA.exe):
//   Laenge:    1. Byte = (Anzahl Folgebytes << 6) | obere 6 Bit, Rest big-endian
//   Nachricht: Laenge(1 + Felder) | Sequenzbyte | Felder
//   Feld:      Laenge(1 + Daten) | Tag | Daten        - Tag 1 = Nachrichtentyp
// Client -> Server: 01 Anmeldung 1, 03 Anmeldung 2, 12 InfoSync, 81 ListUsers, 10 PingMe, 15 Keepalive,
//                   0C Chat, 83 Herausforderung (A5 = 82 an / 80 aus, 0F = Zielname), 14 Diagnose, 0D Abmelden
// Server -> Client: 02/04 Anmeldung ok, 13 eigene Daten, 82 Spielerliste (84 = 1 Liste, 2 neu, 3 weg),
//                   83 Herausforderung von, 0C Chat, 97 Spielstart (1C = 1 warten, sonst verbinden; 06 = Schluessel)
// Fordert ein Spieler einen anderen heraus, startet der Server sofort das Match: erst bekommt der Wartende die
// 97 (er nimmt die Verbindung an), zwei Sekunden spaeter der Verbindende - sonst nimmt die Lobby-Schleife des
// Wartenden die Verbindung als "Ping" an und verwirft sie.
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace BigRaceLanLauncher
{
    public class LobbyServer
    {
        public const int Port = 2914;
        const int EntrySize = 26;

        class Client
        {
            public TcpClient Tcp;
            public NetworkStream Stream;
            public IPAddress Ip;
            public string Name = "";
            public byte[] ListenPort = new byte[2];     // big-endian fuer andere Clients
            public bool LoggedIn;
            public readonly HashSet<string> Challenges = new HashSet<string>();
            public DateTime BusyUntil;                      // Match wird gerade aufgebaut
            public byte Seq;
            public readonly object SendLock = new object();
        }

        readonly object sync = new object();
        readonly List<Client> clients = new List<Client>();
        readonly Func<IPAddress, string> nameFor;
        readonly string logPath;
        TcpListener listener;
        volatile bool running;

        /// <summary>Wird bei jeder Aenderung der Spielerliste aufgerufen (aus einem Hintergrund-Thread).</summary>
        public event Action Changed;

        public LobbyServer(Func<IPAddress, string> nameFor, string logPath)
        {
            this.nameFor = nameFor;
            this.logPath = logPath;
        }

        public string Error { get; private set; }

        public void Start()
        {
            try
            {
                listener = new TcpListener(IPAddress.Any, Port);
                listener.Start();
                running = true;
                var t = new Thread(AcceptLoop) { IsBackground = true, Name = "Lobby" };
                t.Start();
                Log("Lobby-Server laeuft auf Port " + Port);
            }
            catch (Exception ex)
            {
                Error = ex.Message;
                Log("Lobby-Server startet nicht: " + ex.Message);
            }
        }

        public void Stop()
        {
            running = false;
            try { if (listener != null) listener.Stop(); } catch { }
            lock (sync) foreach (Client c in clients.ToArray()) Drop(c);
        }

        public string[] PlayerNames()
        {
            lock (sync)
            {
                var names = new List<string>();
                foreach (Client c in clients) if (c.LoggedIn) names.Add(c.Name);
                return names.ToArray();
            }
        }

        // ---------------------------------------------------------------- Verbindungen

        void AcceptLoop()
        {
            while (running)
            {
                TcpClient tcp;
                try { tcp = listener.AcceptTcpClient(); }
                catch { break; }
                var c = new Client { Tcp = tcp, Ip = ((IPEndPoint)tcp.Client.RemoteEndPoint).Address };
                tcp.NoDelay = true;
                c.Stream = tcp.GetStream();
                lock (sync) clients.Add(c);
                Log("Verbindung von " + c.Ip);
                var t = new Thread(() => ClientLoop(c)) { IsBackground = true, Name = "Lobby " + c.Ip };
                t.Start();
            }
        }

        void ClientLoop(Client c)
        {
            try
            {
                while (running)
                {
                    Dictionary<byte, byte[]> msg = ReadMessage(c.Stream);
                    if (msg == null) break;
                    Handle(c, msg);
                }
            }
            catch (Exception ex)
            {
                Log(c.Name + "@" + c.Ip + ": " + ex.Message);
            }
            Drop(c);
        }

        void Drop(Client c)
        {
            bool wasLoggedIn;
            lock (sync)
            {
                if (!clients.Remove(c)) return;
                wasLoggedIn = c.LoggedIn;
                c.LoggedIn = false;
                foreach (Client o in clients) o.Challenges.Remove(c.Name);
            }
            try { c.Tcp.Close(); } catch { }
            Log("Getrennt: " + c.Name + "@" + c.Ip);
            if (wasLoggedIn)
            {
                foreach (Client o in Others(c)) Send(o, UserListMessage(3, new[] { c }, o));
                OnChanged();
            }
        }

        IEnumerable<Client> Others(Client c)
        {
            lock (sync)
            {
                var list = new List<Client>();
                foreach (Client o in clients) if (o != c && o.LoggedIn) list.Add(o);
                return list;
            }
        }

        Client Find(string name)
        {
            lock (sync)
            {
                foreach (Client o in clients) if (o.LoggedIn && string.Equals(o.Name, name, StringComparison.OrdinalIgnoreCase)) return o;
                return null;
            }
        }

        void OnChanged()
        {
            Action h = Changed;
            if (h != null) try { h(); } catch { }
        }

        // ---------------------------------------------------------------- Nachrichten

        void Handle(Client c, Dictionary<byte, byte[]> msg)
        {
            byte[] typeField;
            if (!msg.TryGetValue(1, out typeField) || typeField.Length < 1) { Log("Nachricht ohne Typ"); return; }
            byte type = typeField[0];
            Log("<- " + c.Name + "@" + c.Ip + " " + Describe(msg));

            switch (type)
            {
                case 0x01:  // Anmeldung Stufe 1 -> Sitzungsdaten
                    Send(c, Msg(0x02).Add(5, new byte[] { 1, 0, 0, 0 }).Add(6, RandomBytes(8)));
                    break;

                case 0x03:  // Anmeldung Stufe 2 -> angemeldet
                {
                    byte[] port;
                    if (msg.TryGetValue(8, out port) && port.Length == 2)
                    {
                        // Das Spiel meldet seinen Port in Host-Byte-Reihenfolge, erwartet ihn spaeter aber big-endian
                        c.ListenPort = new byte[] { port[1], port[0] };
                    }
                    string wanted = nameFor(c.Ip);
                    lock (sync) c.Name = UniqueName(wanted, c);
                    Send(c, Msg(0x04).Add(0x1a, Le16(100)));        // Keepalive alle 100 * 300 ms
                    Send(c, Msg(0x13).Add(0x0f, Str(c.Name)).Add(0xa4, Str("LAN")).Add(0x0b, Str("")).Add(0xa6, Le16(0)));
                    lock (sync) c.LoggedIn = true;
                    foreach (Client o in Others(c)) Send(o, UserListMessage(2, new[] { c }, o));
                    Log("Angemeldet: " + c.Name + "@" + c.Ip + " Port " + ((c.ListenPort[0] << 8) | c.ListenPort[1]));
                    OnChanged();
                    break;
                }

                case 0x81:  // ListUsers
                {
                    var others = new List<Client>(Others(c));
                    if (others.Count > 0) Send(c, UserListMessage(1, others.ToArray(), c));
                    break;
                }

                case 0x0C:  // Chat an einen oder alle
                {
                    byte[] text, target;
                    msg.TryGetValue(0x0b, out text);
                    msg.TryGetValue(0x0f, out target);
                    string to = target == null ? "" : CStr(target);
                    foreach (Client o in Others(c))
                    {
                        if (to.Length > 0 && !string.Equals(o.Name, to, StringComparison.OrdinalIgnoreCase)) continue;
                        Send(o, Msg(0x0C).Add(0x0f, Str(c.Name)).Add(0xa4, Str("LAN")).Add(0x0b, Str(text == null ? "" : CStr(text))).Add(0x15, new byte[] { 0 }));
                    }
                    break;
                }

                case 0x83:  // Herausforderung an/aus
                {
                    byte[] flag, target;
                    if (!msg.TryGetValue(0xa5, out flag) || !msg.TryGetValue(0x0f, out target)) break;
                    Client t = Find(CStr(target));
                    if (t == null || t == c) break;
                    // Das Spiel schickt 82 = "ich fordere heraus" / 80 = "zurueck"; an das Ziel geht 81 = "fordert dich heraus"
                    bool on = flag.Length > 0 && flag[0] != 0x80;
                    // Ein Klick genuegt: wer den anderen anklickt, startet das Match sofort. (Im Original mussten
                    // sich beide gegenseitig herausfordern; das Spiel meldet einen zweiten Klick auf einen schon
                    // markierten Eintrag aber oft gar nicht.)
                    bool start;
                    lock (sync)
                    {
                        start = on && DateTime.Now >= c.BusyUntil && DateTime.Now >= t.BusyUntil;
                        if (start) c.BusyUntil = t.BusyUntil = DateTime.Now.AddSeconds(15);
                    }
                    Log(c.Name + (on ? " fordert " : " nimmt Herausforderung zurueck: ") + t.Name + (start ? " -> Spielstart" : ""));
                    if (start) StartMatch(t, c);
                    break;
                }

                case 0x0D:  // Abmelden
                    Drop(c);
                    break;

                // 0x12 InfoSync, 0x10 PingMe, 0x15 Keepalive, 0x14 Diagnose: keine Antwort noetig
            }
        }

        // waiter hat zuerst herausgefordert und wartet auf die Verbindung, connector verbindet sich
        void StartMatch(Client waiter, Client connector)
        {
            byte[] key = RandomBytes(8);
            lock (sync)
            {
                waiter.Challenges.Clear();
                connector.Challenges.Clear();
            }
            Send(waiter, StartMessage(connector, 1, key));
            var t = new Thread(() =>
            {
                Thread.Sleep(2000);
                Send(connector, StartMessage(waiter, 0, key));
            }) { IsBackground = true };
            t.Start();
        }

        Builder StartMessage(Client opponent, byte role, byte[] key)
        {
            return Msg(0x97).AddRecord(opponent).Add(0x1d, new byte[4]).Add(0x1c, new[] { role }).Add(6, key);
        }

        // Spielerliste aus Sicht von receiver: Flags 0x81 = fordert dich heraus, 0x82 = du forderst ihn heraus
        Builder UserListMessage(byte kind, Client[] users, Client receiver)
        {
            var entries = new byte[users.Length * EntrySize];
            for (int i = 0; i < users.Length; i++)
            {
                Client u = users[i];
                int o = i * EntrySize;
                byte[] name = Encoding.ASCII.GetBytes(u.Name);
                Array.Copy(name, 0, entries, o, Math.Min(8, name.Length));
                Encoding.ASCII.GetBytes("LAN").CopyTo(entries, o + 8);
                u.Ip.GetAddressBytes().CopyTo(entries, o + 16);
                entries[o + 20] = u.ListenPort[0];
                entries[o + 21] = u.ListenPort[1];
                entries[o + 22] = 6;    // TCP
                byte flags = 0x80;
                lock (sync)
                {
                    if (u.Challenges.Contains(receiver.Name)) flags |= 0x01;
                    if (receiver.Challenges.Contains(u.Name)) flags |= 0x02;
                }
                entries[o + 23] = flags;
            }
            Builder b = Msg(0x82).Add(0x84, new[] { kind }).Add(0x11, Le16(users.Length)).Add(0x25, Le16(EntrySize)).Add(0x81, entries);
            if (kind == 3) b.Add(0x1b, new byte[] { 0 });
            return b;
        }

        string UniqueName(string wanted, Client self)
        {
            var sb = new StringBuilder();
            foreach (char ch in wanted ?? "")
                if (ch > ' ' && ch < 127 && ch != '|') sb.Append(ch);
            string name = sb.Length == 0 ? "Spieler" : sb.ToString();
            if (name.Length > 8) name = name.Substring(0, 8);
            string result = name;
            for (int n = 2; ; n++)
            {
                bool taken = false;
                foreach (Client o in clients) if (o != self && o.LoggedIn && string.Equals(o.Name, result, StringComparison.OrdinalIgnoreCase)) taken = true;
                if (!taken) return result;
                string suffix = n.ToString();
                result = name.Substring(0, Math.Min(name.Length, 8 - suffix.Length)) + suffix;
            }
        }

        // ---------------------------------------------------------------- Kodierung

        class Builder
        {
            public readonly MemoryStream Items = new MemoryStream();
            public Builder Add(byte tag, byte[] data)
            {
                byte[] len = EncodeLength(data.Length + 1);
                Items.Write(len, 0, len.Length);
                Items.WriteByte(tag);
                Items.Write(data, 0, data.Length);
                return this;
            }
            // Datensatz eines Spielers, wie ihn die Nachrichten 83 und 97 erwarten
            public Builder AddRecord(Client u)
            {
                return Add(0x0f, Str(u.Name)).Add(0xa4, Str("LAN")).Add(0x24, new byte[5]).Add(0x22, new byte[] { 6 })
                      .Add(7, u.Ip.GetAddressBytes()).Add(8, u.ListenPort).Add(0xa6, Le16(0));
            }
        }

        static Builder Msg(byte type) { return new Builder().Add(1, new[] { type }); }

        void Send(Client c, Builder b)
        {
            byte[] items = b.Items.ToArray();
            byte[] len = EncodeLength(items.Length + 1);
            var msg = new byte[len.Length + 1 + items.Length];
            len.CopyTo(msg, 0);
            lock (c.SendLock)
            {
                msg[len.Length] = c.Seq++;
                items.CopyTo(msg, len.Length + 1);
                try { c.Stream.Write(msg, 0, msg.Length); }
                catch (Exception ex) { Log("Senden an " + c.Name + " fehlgeschlagen: " + ex.Message); return; }
            }
            Log("-> " + c.Name + "@" + c.Ip + " " + Describe(Parse(items)));
        }

        static byte[] EncodeLength(int n)
        {
            if (n < 0x40) return new[] { (byte)n };
            if (n < 0x4000) return new[] { (byte)(0x40 | (n >> 8)), (byte)n };
            if (n < 0x400000) return new[] { (byte)(0x80 | (n >> 16)), (byte)(n >> 8), (byte)n };
            return new[] { (byte)(0xC0 | ((n >> 24) & 0x3f)), (byte)(n >> 16), (byte)(n >> 8), (byte)n };
        }

        static int DecodeLength(byte[] buf, ref int pos)
        {
            int extra = buf[pos] >> 6;
            int n = buf[pos++] & 0x3f;
            for (int i = 0; i < extra; i++) n = (n << 8) | buf[pos++];
            return n;
        }

        static Dictionary<byte, byte[]> ReadMessage(NetworkStream s)
        {
            int first = s.ReadByte();
            if (first < 0) return null;
            int extra = first >> 6;
            int len = first & 0x3f;
            for (int i = 0; i < extra; i++)
            {
                int b = s.ReadByte();
                if (b < 0) return null;
                len = (len << 8) | b;
            }
            if (len < 1 || len > 65536) throw new IOException("ungueltige Laenge " + len);
            var body = new byte[len];
            for (int got = 0; got < len; )
            {
                int n = s.Read(body, got, len - got);
                if (n <= 0) return null;
                got += n;
            }
            var items = new byte[len - 1];     // erstes Byte ist die Sequenznummer
            Array.Copy(body, 1, items, 0, items.Length);
            return Parse(items);
        }

        static Dictionary<byte, byte[]> Parse(byte[] items)
        {
            var result = new Dictionary<byte, byte[]>();
            int pos = 0;
            while (pos < items.Length)
            {
                int n = DecodeLength(items, ref pos);
                if (n < 1 || pos + n > items.Length) break;
                byte tag = items[pos];
                var data = new byte[n - 1];
                Array.Copy(items, pos + 1, data, 0, data.Length);
                if (!result.ContainsKey(tag)) result[tag] = data;
                pos += n;
            }
            return result;
        }

        static byte[] Str(string s) { return Encoding.ASCII.GetBytes(s + "\0"); }
        static string CStr(byte[] b) { int n = Array.IndexOf(b, (byte)0); return Encoding.ASCII.GetString(b, 0, n < 0 ? b.Length : n); }
        static byte[] Le16(int v) { return new[] { (byte)v, (byte)(v >> 8) }; }

        static readonly Random random = new Random();
        static byte[] RandomBytes(int n) { var b = new byte[n]; lock (random) random.NextBytes(b); return b; }

        // ---------------------------------------------------------------- Protokoll

        static string Describe(Dictionary<byte, byte[]> msg)
        {
            var sb = new StringBuilder();
            foreach (KeyValuePair<byte, byte[]> kv in msg)
            {
                sb.AppendFormat("{0:X2}=", kv.Key);
                bool text = kv.Value.Length > 1;
                foreach (byte b in kv.Value) if ((b < 32 || b > 126) && b != 0) text = false;
                if (text) sb.Append('"').Append(CStr(kv.Value)).Append('"');
                else foreach (byte b in kv.Value) sb.AppendFormat("{0:X2}", b);
                sb.Append(' ');
            }
            return sb.ToString();
        }

        void Log(string text)
        {
            if (logPath == null) return;
            try
            {
                lock (this) File.AppendAllText(logPath, DateTime.Now.ToString("HH:mm:ss.fff") + "  " + text + "\r\n");
            }
            catch { }
        }
    }
}
