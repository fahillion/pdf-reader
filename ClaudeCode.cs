// Asks Claude through the person's own installed, signed-in Claude Code. This app runs the official
// program and reads its output; it never reads Claude credentials. All tools, settings, hooks and
// MCP servers are off, so a PDF's contents can't make Claude Code do anything but answer.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace PdfReaderApp
{
    class ClaudeStatus
    {
        public bool Installed;
        public bool SignedIn;
        public string Plan;
    }

    static class ClaudeCode
    {
        public const string InstallUrl = "https://code.claude.com/docs/en/setup";

        public static bool IsInstalled { get { return Locate() != null; } }

        static string Locate()
        {
            string test = Environment.GetEnvironmentVariable("PDFREADER_TEST_CLAUDE");
            if (test != null) return File.Exists(test) ? test : null;
            var dirs = new List<string>();
            dirs.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin"));
            string path = Environment.GetEnvironmentVariable("PATH");
            if (path != null) dirs.AddRange(path.Split(';').Where(d => d.Trim().Length > 0));
            dirs.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm"));
            foreach (string name in new[] { "claude.exe", "claude.cmd" })
                foreach (string d in dirs)
                {
                    try
                    {
                        string full = Path.Combine(d.Trim(), name);
                        if (File.Exists(full)) return full;
                    }
                    catch (ArgumentException) { }
                }
            return null;
        }

        static ProcessStartInfo Start(string exe, string args)
        {
            ProcessStartInfo info;
            if (exe.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase))
                info = new ProcessStartInfo(Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe", "/d /s /c \"\"" + exe + "\" " + args + "\"");
            else
                info = new ProcessStartInfo(exe, args);
            info.UseShellExecute = false;
            info.CreateNoWindow = true;
            info.RedirectStandardOutput = true;
            info.RedirectStandardError = true;
            info.StandardOutputEncoding = Encoding.UTF8;

            // Use the person's subscription, not any API key that happens to be set, and don't
            // pick up a project's instructions from whatever folder this app was started in.
            info.EnvironmentVariables.Remove("ANTHROPIC_API_KEY");
            info.EnvironmentVariables.Remove("ANTHROPIC_AUTH_TOKEN");
            info.EnvironmentVariables.Remove("CLAUDECODE");
            string quiet = Path.Combine(Path.GetTempPath(), "PdfReader-claude");
            Directory.CreateDirectory(quiet);
            info.WorkingDirectory = quiet;
            return info;
        }

        // A .cmd launcher runs Claude Code as a child process, so stop the whole tree.
        static void KillTree(Process p)
        {
            try
            {
                var info = new ProcessStartInfo("taskkill", "/PID " + p.Id + " /T /F");
                info.UseShellExecute = false;
                info.CreateNoWindow = true;
                using (Process.Start(info)) { }
            }
            catch (Exception) { try { p.Kill(); } catch (Exception) { } }
        }

        public static async Task<ClaudeStatus> Check()
        {
            var status = new ClaudeStatus();
            string exe = Locate();
            if (exe == null) return status;
            status.Installed = true;
            try
            {
                using (Process p = Process.Start(Start(exe, "auth status")))
                {
                    Task<string> err = p.StandardError.ReadToEndAsync();
                    string text = await p.StandardOutput.ReadToEndAsync();
                    await err;
                    var json = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(text);
                    status.SignedIn = json.ContainsKey("loggedIn") && true.Equals(json["loggedIn"]);
                    status.Plan = json.ContainsKey("subscriptionType") ? json["subscriptionType"] as string : null;
                }
            }
            catch (Exception) { status.SignedIn = true; } // Older version with no status command: find out on first use.
            return status;
        }

        public static async Task<AiResult> Ask(string system, string pdfBase64, List<Turn> turns, Action<string> onText, CancellationToken cancel)
        {
            string exe = Locate();
            if (exe == null) throw new SignInRequired("Claude Code isn't installed.");

            // Each question is a fresh, unsaved run, so earlier turns travel in the prompt.
            var prompt = new StringBuilder();
            if (turns.Count > 1)
            {
                prompt.Append("Earlier in this conversation:\n");
                foreach (Turn t in turns.Take(turns.Count - 1))
                    prompt.Append(t.IsUser ? "Reader: " : "You: ").Append(t.Text).Append("\n\n");
                prompt.Append("The reader now asks:\n");
            }
            prompt.Append(turns[turns.Count - 1].Text);

            var json = new JavaScriptSerializer();
            json.MaxJsonLength = int.MaxValue;
            string line = json.Serialize(new Dictionary<string, object>
            {
                { "type", "user" },
                { "message", new Dictionary<string, object>
                    {
                        { "role", "user" },
                        { "content", new List<object>
                            {
                                new Dictionary<string, object>
                                {
                                    { "type", "document" },
                                    { "source", new Dictionary<string, object> { { "type", "base64" }, { "media_type", "application/pdf" }, { "data", pdfBase64 } } },
                                },
                                new Dictionary<string, object> { { "type", "text" }, { "text", prompt.ToString() } },
                            }
                        },
                    }
                },
            });

            string args = "-p --input-format stream-json --output-format stream-json --verbose --include-partial-messages"
                + " --tools \"\" --setting-sources \"\" --strict-mcp-config --no-session-persistence --disable-slash-commands"
                + " --system-prompt \"" + system.Replace("\"", "'") + "\"";

            ProcessStartInfo info = Start(exe, args);
            info.RedirectStandardInput = true;

            var result = new AiResult();
            bool finished = false;
            using (Process p = Process.Start(info))
            using (cancel.Register(delegate { KillTree(p); }))
            {
                Task<string> errors = p.StandardError.ReadToEndAsync();
                Task sending = Task.Run(async delegate
                {
                    try
                    {
                        byte[] bytes = new UTF8Encoding(false).GetBytes(line + "\n");
                        await p.StandardInput.BaseStream.WriteAsync(bytes, 0, bytes.Length);
                        await p.StandardInput.BaseStream.FlushAsync();
                        p.StandardInput.Close();
                    }
                    catch (IOException) { } // The process ended early; its error is reported below.
                });

                string raw;
                while ((raw = await p.StandardOutput.ReadLineAsync()) != null)
                {
                    Dictionary<string, object> e;
                    try { e = json.Deserialize<Dictionary<string, object>>(raw); }
                    catch (Exception) { continue; }
                    string type = e.ContainsKey("type") ? e["type"] as string : null;
                    if (type == "stream_event")
                    {
                        // Not every streaming event has a typed delta: message_delta's has only stop_reason.
                        var ev = e.ContainsKey("event") ? e["event"] as Dictionary<string, object> : null;
                        var delta = ev != null && ev.ContainsKey("delta") ? ev["delta"] as Dictionary<string, object> : null;
                        if (delta != null && delta.ContainsKey("type") && (delta["type"] as string) == "text_delta" && delta.ContainsKey("text"))
                        {
                            string piece = (string)delta["text"];
                            result.Text += piece;
                            onText(piece);
                        }
                    }
                    else if (type == "result")
                    {
                        finished = true;
                        bool failed = e.ContainsKey("is_error") && true.Equals(e["is_error"]);
                        string final = e.ContainsKey("result") ? e["result"] as string : null;
                        if (failed)
                        {
                            string msg = final ?? "Claude Code reported a problem.";
                            if (msg.IndexOf("login", StringComparison.OrdinalIgnoreCase) >= 0 || msg.IndexOf("log in", StringComparison.OrdinalIgnoreCase) >= 0 || msg.IndexOf("authenticat", StringComparison.OrdinalIgnoreCase) >= 0)
                                throw new SignInRequired("Claude Code needs you to sign in again.");
                            throw new AiError(msg);
                        }
                        if (result.Text.Length == 0 && !string.IsNullOrEmpty(final))
                        {
                            result.Text = final;
                            onText(final);
                        }
                    }
                }
                await sending;
                string stderr = await errors;
                p.WaitForExit();
                cancel.ThrowIfCancellationRequested();
                if (!finished)
                    throw new AiError("Claude Code stopped before answering." + (stderr.Trim().Length > 0 ? " " + stderr.Trim().Split('\n')[0] : ""));
            }
            return result;
        }
    }
}
