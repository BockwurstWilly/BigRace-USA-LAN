// Pro Pinball - Big Race USA : LAN-Patcher
// Installiert Fenstermodus (cnc-ddraw), Titel-DLL (dinput.dll), LAN-Launcher und Verknuepfung in den Spielordner
// und ruft fuer Firewall/Netzwerkprofil BigRace-LAN-Fix.ps1 auf (beides als Ressource eingebettet).
// Bauen: build.cmd (nutzt den in Windows enthaltenen .NET-Framework-4-Compiler).
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

namespace BigRaceLanPatcher
{
    public class MainForm : Form
    {
        const string GameExeName = "BigRaceUSA.exe";
        const string GogGameId = "1207658735";

        // cnc-ddraw (MIT, github.com/FunkyFr3sh/cnc-ddraw): zwingt das Spiel ins Fenster, damit die
        // Dialoge des Spiels (Head-To-Head-Auswahl, Lobby) nicht hinter dem Vollbild verschwinden.
        const string DdrawMarker = "Angepasst vom Big Race USA LAN-Patcher";

        TextBox gameDirBox;
        CheckBox setPrivateBox;
        Button applyButton, removeButton, browseButton;
        RichTextBox log;
        Process running;

        public MainForm()
        {
            Text = "Big Race USA - LAN-Patcher";
            Font = new Font("Segoe UI", 9.5f);
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(640, 600);
            MinimumSize = new Size(560, 520);
            StartPosition = FormStartPosition.CenterScreen;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(14, 10, 14, 12) };
            for (int i = 0; i < 6; i++) layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            Controls.Add(layout);

            layout.Controls.Add(Section("Was wird installiert?",
                "Fenstermodus fürs Spiel (cnc-ddraw)",
                "Infos zu Mitspielern und Lobby im Fenstertitel",
                "LAN-Launcher + Verknüpfung „Big Race USA LAN“ auf dem Desktop",
                "Firewall-Freigaben fürs Heimnetz"));
            layout.Controls.Add(Section("So wird gespielt",
                "Auf jedem PC einmal patchen, vorher das Spiel schließen",
                "„Big Race USA LAN“ starten, Namen eintragen, START",
                "Tisch laden, dann Esc › Head-To-Head › Friendly (Lobby)",
                "Gegner in der Liste anklicken – das Match startet"));

            // Spielordner
            var dirRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, AutoSize = true, Margin = new Padding(0) };
            dirRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            dirRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            dirRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            dirRow.Margin = new Padding(0, 6, 0, 0);
            dirRow.Controls.Add(new Label { Text = "Spielordner:", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 6, 0) });
            gameDirBox = new TextBox { Dock = DockStyle.Fill, Text = FindGameDir() };
            dirRow.Controls.Add(gameDirBox);
            browseButton = new Button { Text = "Durchsuchen...", AutoSize = true };
            browseButton.Click += delegate { Browse(); };
            dirRow.Controls.Add(browseButton);
            layout.Controls.Add(dirRow);

            setPrivateBox = new CheckBox
            {
                Text = "Netzwerk auf „Privat“ stellen, falls es „Öffentlich“ ist",
                Checked = true,
                AutoSize = true,
                Margin = new Padding(0, 6, 0, 0)
            };
            layout.Controls.Add(setPrivateBox);


            // Aktionen
            var actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = true, Margin = new Padding(0, 0, 0, 6) };
            applyButton = new Button { Text = "Patch anwenden", AutoSize = true, Padding = new Padding(10, 4, 10, 4), Font = new Font(Font, FontStyle.Bold) };
            applyButton.Click += delegate { RunFix(false); };
            removeButton = new Button { Text = "Patch entfernen", AutoSize = true, Padding = new Padding(6, 4, 6, 4) };
            removeButton.Click += delegate { RunFix(true); };
            actions.Controls.Add(applyButton);
            actions.Controls.Add(removeButton);
            layout.Controls.Add(actions);

            log = new RichTextBox
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                BackColor = Color.FromArgb(24, 24, 28),
                ForeColor = Color.Gainsboro,
                Font = new Font("Consolas", 9.5f),
                BorderStyle = BorderStyle.None,
                WordWrap = false
            };
            layout.Controls.Add(log);

            AppendLine("Bereit.");
        }

        // Überschrift + Stichpunkte
        Control Section(string title, params string[] points)
        {
            var box = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Margin = new Padding(0, 0, 0, 10), Dock = DockStyle.Fill };
            box.Controls.Add(new Label { Text = title, AutoSize = true, Font = new Font(Font.FontFamily, 11f, FontStyle.Bold), Margin = new Padding(0, 0, 0, 2) });
            foreach (string p in points)
                box.Controls.Add(new Label { Text = "•  " + p, AutoSize = true, Margin = new Padding(6, 1, 0, 1) });
            return box;
        }

        static string FindGameDir()
        {
            string exeDir = Path.GetDirectoryName(Application.ExecutablePath);
            foreach (string dir in new[] { exeDir, Path.GetDirectoryName(exeDir) })
            {
                if (dir != null && File.Exists(Path.Combine(dir, GameExeName))) return dir;
            }
            foreach (string key in new[] { @"SOFTWARE\WOW6432Node\GOG.com\Games\" + GogGameId, @"SOFTWARE\GOG.com\Games\" + GogGameId })
            {
                try
                {
                    using (RegistryKey k = Registry.LocalMachine.OpenSubKey(key))
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
            using (var dlg = new FolderBrowserDialog { Description = "Ordner mit " + GameExeName + " auswaehlen" })
            {
                if (Directory.Exists(gameDirBox.Text)) dlg.SelectedPath = gameDirBox.Text;
                if (dlg.ShowDialog(this) == DialogResult.OK) gameDirBox.Text = dlg.SelectedPath;
            }
        }

        void RunFix(bool remove)
        {
            if (running != null) return;
            string gameDir = gameDirBox.Text.Trim();
            if (!remove && !File.Exists(Path.Combine(gameDir, GameExeName)))
            {
                MessageBox.Show(this, GameExeName + " ist nicht in diesem Ordner. Bitte den Spielordner wählen.",
                    Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // Laufendes Spiel sperrt ddraw.dll/dinput.dll - dann lieber gar nicht erst anfangen
            if (Process.GetProcessesByName("BigRaceUSA").Length > 0 || Process.GetProcessesByName("BigRace-LAN-Launcher").Length > 0)
            {
                MessageBox.Show(this, "Spiel oder Launcher läuft noch. Bitte beide schließen und nochmal klicken.",
                    Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            log.Clear();
            string fileError = null;
            if (File.Exists(Path.Combine(gameDir, GameExeName)))
            {
                try
                {
                    string launcher = Path.Combine(gameDir, "BigRace-LAN-Launcher.exe");
                    if (remove)
                    {
                        RemoveWindowMode(gameDir);
                        if (File.Exists(launcher)) File.Delete(launcher);
                        RemoveShortcut();
                    }
                    else
                    {
                        InstallWindowMode(gameDir);
                        File.WriteAllBytes(launcher, ReadResource("BigRace-LAN-Launcher.exe"));
                        AppendLine("   [OK]  LAN-Launcher");
                        CreateShortcut(gameDir, launcher);
                        fileError = VerifyInstall(gameDir);
                        if (fileError == null) AppendLine("   [OK]  Alle Dateien geprüft");
                        AppendLine("");
                    }
                }
                catch (Exception ex)
                {
                    fileError = ex.Message;
                }
                if (fileError != null)
                    AppendLine("   FEHLER im Spielordner: " + fileError);
            }

            string script = Path.Combine(Path.GetTempPath(), "BigRaceLanFix_" + Guid.NewGuid().ToString("N") + ".ps1");
            File.WriteAllText(script, ReadResourceText("BigRace-LAN-Fix.ps1"), new UTF8Encoding(true));

            var cmd = new StringBuilder();
            cmd.Append("[Console]::OutputEncoding=[Text.Encoding]::UTF8; & ").Append(Quote(script));
            cmd.Append(" -NoPause -GameDir ").Append(Quote(gameDir));
            if (remove) cmd.Append(" -Remove");
            if (setPrivateBox.Checked) cmd.Append(" -SetPrivate");
            cmd.Append("; exit $LASTEXITCODE");

            var psi = new ProcessStartInfo("powershell.exe",
                "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"" + cmd.ToString().Replace("\"", "\\\"") + "\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            SetBusy(true);
            running = new Process { StartInfo = psi, EnableRaisingEvents = true };
            running.OutputDataReceived += (s, e) => { if (e.Data != null) BeginInvoke((Action)(() => AppendLine(e.Data))); };
            running.ErrorDataReceived += (s, e) => { if (e.Data != null) BeginInvoke((Action)(() => AppendLine("FEHLER: " + e.Data))); };
            running.Exited += (s, e) => BeginInvoke((Action)(() =>
            {
                running.WaitForExit();
                int exitCode = running.ExitCode;
                running.Dispose();
                running = null;
                try { File.Delete(script); } catch { }
                SetBusy(false);
                // Klares Ergebnis am Ende, damit niemand mit einer halben Installation spielt
                if (fileError == null && exitCode == 0)
                    MessageBox.Show(this, remove ? "Patch entfernt."
                        : "Fertig.\r\n\r\nZum Spielen „Big Race USA LAN“ auf dem Desktop starten.",
                        Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                else
                    MessageBox.Show(this, "Das hat nicht ganz geklappt:\r\n\r\n" +
                        (fileError != null ? "Spielordner: " + fileError + "\r\n" : "") +
                        (exitCode != 0 ? "Firewall: siehe rote Zeilen im Fenster.\r\n" : "") +
                        "\r\nSpiel und Launcher schließen und nochmal versuchen.",
                        Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }));
            try
            {
                running.Start();
                running.BeginOutputReadLine();
                running.BeginErrorReadLine();
            }
            catch (Exception ex)
            {
                AppendLine("FEHLER: PowerShell konnte nicht gestartet werden: " + ex.Message);
                running = null;
                SetBusy(false);
            }
        }

        static string Quote(string s) { return "'" + s.Replace("'", "''") + "'"; }

        static byte[] ReadResource(string name)
        {
            using (Stream res = typeof(MainForm).Assembly.GetManifestResourceStream(name))
            using (var ms = new MemoryStream())
            {
                res.CopyTo(ms);
                return ms.ToArray();
            }
        }

        static string ReadResourceText(string name)
        {
            return new UTF8Encoding(false).GetString(ReadResource(name));
        }

        static bool SameBytes(string path, byte[] data)
        {
            if (!File.Exists(path)) return false;
            byte[] existing = File.ReadAllBytes(path);
            if (existing.Length != data.Length) return false;
            for (int i = 0; i < data.Length; i++) if (existing[i] != data[i]) return false;
            return true;
        }

        void InstallWindowMode(string gameDir)
        {
            AppendLine("== Spielordner");
            byte[] dll = ReadResource("ddraw.dll");
            string dllPath = Path.Combine(gameDir, "ddraw.dll");
            string iniPath = Path.Combine(gameDir, "ddraw.ini");

            // Fremde ddraw.dll/ddraw.ini (z. B. DxWnd, aeltere cnc-ddraw) einmalig sichern
            if (File.Exists(dllPath) && !SameBytes(dllPath, dll) && !File.Exists(dllPath + ".bak"))
            {
                File.Copy(dllPath, dllPath + ".bak");
                AppendLine("   [!!]  Vorhandene ddraw.dll gesichert als ddraw.dll.bak");
            }
            if (File.Exists(iniPath) && !File.ReadAllText(iniPath).Contains(DdrawMarker) && !File.Exists(iniPath + ".bak"))
            {
                File.Copy(iniPath, iniPath + ".bak");
                AppendLine("   [!!]  Vorhandene ddraw.ini gesichert als ddraw.ini.bak");
            }

            File.WriteAllBytes(dllPath, dll);
            // Eine bereits von uns geschriebene ddraw.ini behalten: cnc-ddraw speichert dort Fenstergroesse/-position
            if (!File.Exists(iniPath) || !File.ReadAllText(iniPath).Contains(DdrawMarker))
                File.WriteAllBytes(iniPath, ReadResource("ddraw.ini"));
            File.WriteAllBytes(Path.Combine(gameDir, "cnc-ddraw-LICENSE.txt"), ReadResource("cnc-ddraw-LICENSE.txt"));
            AppendLine("   [OK]  Fenstermodus (cnc-ddraw)");

            // Infoleiste: dinput.dll leitet an die echte dinput.dll weiter und zeichnet die LAN-Infos ins Spielfenster
            string barPath = Path.Combine(gameDir, "dinput.dll");
            if (File.Exists(barPath) && !IsOurBar(barPath) && !File.Exists(barPath + ".bak"))
            {
                File.Copy(barPath, barPath + ".bak");
                AppendLine("   [!!]  Vorhandene dinput.dll gesichert als dinput.dll.bak");
            }
            File.WriteAllBytes(barPath, ReadResource("dinput.dll"));
            AppendLine("   [OK]  Infos im Fenstertitel (dinput.dll)");
        }

        // Liegen alle Dateien so im Spielordner, wie sie sollen? null = ja, sonst Fehlertext
        static string VerifyInstall(string gameDir)
        {
            var missing = new System.Collections.Generic.List<string>();
            if (!SameBytes(Path.Combine(gameDir, "ddraw.dll"), ReadResource("ddraw.dll"))) missing.Add("ddraw.dll (Fenstermodus)");
            string ini = Path.Combine(gameDir, "ddraw.ini");
            if (!File.Exists(ini) || !File.ReadAllText(ini).Contains(DdrawMarker)) missing.Add("ddraw.ini");
            if (!SameBytes(Path.Combine(gameDir, "dinput.dll"), ReadResource("dinput.dll"))) missing.Add("dinput.dll (Infos im Fenstertitel)");
            if (!SameBytes(Path.Combine(gameDir, "BigRace-LAN-Launcher.exe"), ReadResource("BigRace-LAN-Launcher.exe"))) missing.Add("BigRace-LAN-Launcher.exe");
            if (missing.Count == 0) return null;
            return "fehlt oder verändert: " + string.Join(", ", missing.ToArray()) +
                   " (Virenscanner? Schreibschutz?)";
        }

        // Verknuepfung "Big Race USA LAN" auf dem Desktop aller Benutzer - der Patcher laeuft als Administrator,
        // der eigene Desktop waere evtl. der eines anderen Kontos
        static string ShortcutPath()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), "Big Race USA LAN.lnk");
        }

        void CreateShortcut(string gameDir, string launcher)
        {
            try
            {
                Type shellType = Type.GetTypeFromProgID("WScript.Shell");
                object shell = Activator.CreateInstance(shellType);
                object link = shellType.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { ShortcutPath() });
                Type linkType = link.GetType();
                linkType.InvokeMember("TargetPath", System.Reflection.BindingFlags.SetProperty, null, link, new object[] { launcher });
                linkType.InvokeMember("WorkingDirectory", System.Reflection.BindingFlags.SetProperty, null, link, new object[] { gameDir });
                linkType.InvokeMember("IconLocation", System.Reflection.BindingFlags.SetProperty, null, link, new object[] { Path.Combine(gameDir, GameExeName) + ",0" });
                linkType.InvokeMember("Description", System.Reflection.BindingFlags.SetProperty, null, link, new object[] { "Pro Pinball - Big Race USA im LAN spielen" });
                linkType.InvokeMember("Save", System.Reflection.BindingFlags.InvokeMethod, null, link, null);
                AppendLine("   [OK]  Verknüpfung „Big Race USA LAN“ auf dem Desktop");
            }
            catch (Exception ex)
            {
                AppendLine("   [!!]  Verknüpfung nicht angelegt: " + ex.Message);
            }
        }

        void RemoveShortcut()
        {
            try { if (File.Exists(ShortcutPath())) { File.Delete(ShortcutPath()); AppendLine("   [OK]  Verknüpfung entfernt"); } }
            catch { }
        }

        // Unsere dinput.dll enthaelt den Namen des Shared Memory (UTF-16) - so wird sie auch in aelteren Versionen erkannt
        static bool IsOurBar(string path)
        {
            if (!File.Exists(path)) return false;
            string marker = Encoding.ASCII.GetString(Encoding.Unicode.GetBytes(@"Local\BigRaceLanBar"));
            return Encoding.ASCII.GetString(File.ReadAllBytes(path)).Contains(marker);
        }

        void RemoveWindowMode(string gameDir)
        {
            AppendLine("== Spielordner");
            string dllPath = Path.Combine(gameDir, "ddraw.dll");
            string iniPath = Path.Combine(gameDir, "ddraw.ini");
            bool ours = SameBytes(dllPath, ReadResource("ddraw.dll"));
            if (ours) File.Delete(dllPath);
            if (File.Exists(iniPath) && File.ReadAllText(iniPath).Contains(DdrawMarker)) File.Delete(iniPath);
            File.Delete(Path.Combine(gameDir, "cnc-ddraw-LICENSE.txt"));
            string barPath = Path.Combine(gameDir, "dinput.dll");
            if (IsOurBar(barPath))
            {
                File.Delete(barPath);
                AppendLine("   [OK]  dinput.dll entfernt");
            }
            foreach (string p in new[] { dllPath, iniPath, barPath })
            {
                if (File.Exists(p + ".bak") && !File.Exists(p))
                {
                    File.Move(p + ".bak", p);
                    AppendLine("   [OK]  " + Path.GetFileName(p) + " aus der Sicherung zurückgeholt");
                }
            }
            if (ours) AppendLine("   [OK]  Fenstermodus entfernt");
        }

        void SetBusy(bool busy)
        {
            applyButton.Enabled = removeButton.Enabled = browseButton.Enabled = !busy;
            UseWaitCursor = busy;
            if (busy) AppendLine("Bitte warten ...");
        }

        void AppendLine(string line)
        {
            Color c = Color.Gainsboro;
            if (line.Contains("[OK]")) c = Color.LightGreen;
            else if (line.Contains("[!!]")) c = Color.Orange;
            else if (line.Contains("FEHLER")) c = Color.Tomato;
            else if (line.StartsWith("==")) c = Color.DeepSkyBlue;
            else if (line.Contains("LAN-IP DIESES PCs")) c = Color.White;
            log.SelectionStart = log.TextLength;
            log.SelectionLength = 0;
            log.SelectionColor = c;
            log.AppendText(line + "\r\n");
            log.ScrollToCaret();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (running != null) e.Cancel = true;
            base.OnFormClosing(e);
        }

        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }
}
